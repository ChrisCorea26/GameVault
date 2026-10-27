using GameVault.Library.Catalog;
using GameVault.Library.Data;
using GameVault.Library.Models;
using GameVault.Library.Profiles;
using GameVault.Library.Scanning;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Storage;

/// <summary>
/// The real library (SPEC §6) over <see cref="LibraryDatabase"/>. Database and disk work runs on the thread
/// pool; <see cref="ILibraryService.Changed"/> is raised on the thread that created the service. Folders are
/// watched while the app runs and rescanned when their contents change.
/// </summary>
public sealed class LibraryService : ILibraryService, IDisposable
{
    private readonly LibraryDatabase _database;
    private readonly LibraryRepository _repository;
    private readonly LibraryScanner _scanner;
    private readonly GameProfiles _profiles;
    private readonly LibraryWatcher _watcher;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public LibraryService(LibraryDatabase database, GameProfiles profiles, TimeSpan? watchDelay = null)
    {
        _database = database;
        _repository = new LibraryRepository(database);
        _scanner = new LibraryScanner(database);
        _profiles = profiles;
        _watcher = new LibraryWatcher(watchDelay);
        _watcher.Changed += async (_, _) => await ScanAsync();
    }

    public event EventHandler? Changed;

    /// <summary>Starts watching the saved folders. Call once the app is up (never from a constructor).</summary>
    public async Task StartWatchingAsync() => _watcher.Watch(await GetFoldersAsync());

    public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(_repository.GetGames, cancellationToken);

    public Task<GameEntry?> GetGameAsync(string key, CancellationToken cancellationToken = default) =>
        Task.Run(() => _repository.GetGame(key), cancellationToken);

    public async Task SaveGameAsync(GameEntry game)
    {
        await Task.Run(() => _repository.SaveCollection(game));
        RaiseChanged();
    }

    public Task AddPlaytimeAsync(string key, TimeSpan time) =>
        Task.Run(() => _repository.AddPlaytime(key, time, DateTimeOffset.Now));

    public Task<IReadOnlyList<string>> GetFoldersAsync() => Task.Run(_repository.GetFolders);

    public async Task<ScanResult> AddFolderAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => _repository.AddFolder(path), cancellationToken);
        var result = await ScanAsync(progress, cancellationToken);
        _watcher.Watch(await GetFoldersAsync());
        return result;
    }

    /// <summary>
    /// Adds the bundled homebrew folder (SPEC §6.4) and fills in its games' details. Run once, on first launch; after
    /// that the folder is an ordinary library folder the player may remove.
    /// </summary>
    public async Task AddBundledHomebrewAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        await AddFolderAsync(folder);
        await Task.Run(() =>
        {
            var games = _repository.GetGames();
            foreach (var (path, bundled) in BundledHomebrew.Read(folder))
            {
                if (games.FirstOrDefault(g => string.Equals(g.RomPath, path, StringComparison.OrdinalIgnoreCase)) is { } entry
                    && BundledHomebrew.Describe(entry, bundled) is var described && described != entry)
                {
                    _repository.SaveCollection(described);
                }
            }
        });
        RaiseChanged();
    }

    public async Task RemoveFolderAsync(string path)
    {
        await Task.Run(() => _repository.RemoveFolder(path));
        _watcher.Watch(await GetFoldersAsync());
        RaiseChanged();
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            var folders = await GetFoldersAsync();
            var result = await Task.Run(() => _scanner.Scan(folders, progress, cancellationToken), cancellationToken);
            RaiseChanged();
            return result;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public Task<CatalogPreview> PreviewCatalogAsync(string path) => Task.Run(() =>
    {
        using var stream = File.OpenRead(path);
        var (games, skipped) = CatalogImporter.Parse(stream);
        return new CatalogPreview(Path.GetFileNameWithoutExtension(path), games.Count, games.Count(g => g.IsLicensed), skipped);
    });

    public async Task<CatalogImportResult> ImportCatalogAsync(string path)
    {
        var result = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            var imported = new CatalogImporter(_database).Import(stream, Path.GetFileName(path));
            _scanner.Rematch(); // files scanned before the import are recognized now
            return imported;
        });
        RaiseChanged();
        return result;
    }

    public Task<GameProfile> GetProfileAsync(string key) => Task.Run(() =>
        _profiles.Resolve(Sha1Of(key), GameProfile.FromJson(_repository.GetOverrides(key))));

    public Task<GameProfile?> GetProfileOverridesAsync(string key) => Task.Run(() => GameProfile.FromJson(_repository.GetOverrides(key)));

    public Task SaveProfileOverridesAsync(string key, GameProfile? overrides) =>
        Task.Run(() => _repository.SetOverrides(key, overrides?.ToJson()));

    /// <summary>The profile the emulator applies, by headerless SHA-1 (no database round trip for the bundle).</summary>
    public GameProfile ResolveProfile(string sha1) =>
        _profiles.Resolve(sha1, GameProfile.FromJson(_repository.GetOverrides("sha1:" + sha1)));

    /// <summary>Total recorded playtime for a game (shown on timeline entries).</summary>
    public TimeSpan PlaytimeOf(string key) => _repository.GetGame(key)?.Playtime ?? TimeSpan.Zero;

    public void Dispose() => _watcher.Dispose();

    private static string? Sha1Of(string key) => key.StartsWith("sha1:", StringComparison.Ordinal) ? key[5..] : null;

    private void RaiseChanged()
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _context.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        }
    }
}
