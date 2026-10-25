using GameVault.Library.Catalog;
using GameVault.Library.Models;
using GameVault.Library.Profiles;
using GameVault.Library.Scanning;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// A realistic but entirely fictional library for <c>--sample-data</c> UI previews and tests.
/// Titles and publishers are invented so no real product names or artwork are involved.
/// </summary>
public sealed class SampleLibraryService : ILibraryService
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 20, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<GameEntry> Games { get; } =
    [
        Game(1, "Star Courier", GameRegion.NorthAmerica, "Bitwave", 1987, "Shooter",
            Ownership.Cartridge | Ownership.Box | Ownership.Manual | Ownership.DigitalDump, PlayStatus.Completed,
            favorite: true, rating: 5, hours: 31.5, lastPlayedDaysAgo: 2),
        Game(2, "Crystal Caverns", GameRegion.NorthAmerica, "Lumen Soft", 1988, "Platformer",
            Ownership.Cartridge | Ownership.DigitalDump, PlayStatus.Playing, rating: 4, hours: 6.2, lastPlayedDaysAgo: 0),
        Game(3, "Knights of Ember Hall", GameRegion.NorthAmerica, "Redstone", 1989, "RPG",
            Ownership.Cartridge | Ownership.Box | Ownership.DigitalDump, PlayStatus.Beaten, rating: 5, hours: 48.0,
            lastPlayedDaysAgo: 40),
        Game(4, "Turbo Alley", GameRegion.NorthAmerica, "Bitwave", 1990, "Racing", Ownership.Cartridge,
            PlayStatus.Unplayed),
        Game(5, "Puzzle Pagoda", GameRegion.NorthAmerica, "Kitefox", 1991, "Puzzle",
            Ownership.DigitalDump, PlayStatus.Playing, hours: 2.75, lastPlayedDaysAgo: 5, players: 2),
        Game(6, "Harbor Patrol", GameRegion.NorthAmerica, "Northlight", 1988, "Action"),
        Game(7, "Moonlit Manor", GameRegion.NorthAmerica, "Lumen Soft", 1990, "Adventure"),
        Game(8, "Gridiron Legends", GameRegion.NorthAmerica, "Redstone", 1991, "Sports", players: 2),
        Game(9, "Sky Duelist", GameRegion.NorthAmerica, "Kitefox", 1989, "Shooter", players: 2),
        Game(10, "Rocket Rally", GameRegion.Japan, "Hoshi Works", 1986, "Racing",
            Ownership.Cartridge, PlayStatus.Beaten, hours: 3.0, lastPlayedDaysAgo: 120),
        Game(11, "Dragon Lantern", GameRegion.Japan, "Hoshi Works", 1987, "RPG"),
        Game(12, "Castle Tumble", GameRegion.Europe, "Northlight", 1992, "Platformer",
            Ownership.Cartridge | Ownership.Manual, PlayStatus.Unplayed),
        Game(13, "Byte Garden (Homebrew)", GameRegion.World, "Community", 2021, "Puzzle",
            Ownership.DigitalDump, PlayStatus.Playing, hours: 1.25, lastPlayedDaysAgo: 1, licensed: false),
        Game(14, "Pixel Pilgrim (Homebrew)", GameRegion.World, "Community", 2023, "Adventure",
            Ownership.DigitalDump, PlayStatus.Unplayed, licensed: false),
    ];

    private readonly List<GameEntry> _games;
    private readonly List<string> _folders = [SampleGameFilePicker.SampleFolder];
    private readonly Dictionary<string, GameProfile> _overrides = [];

    /// <param name="empty">Start with nothing, to preview the first-run screen.</param>
    public SampleLibraryService(bool empty = false)
    {
        _games = empty ? [] : [.. Games];
        if (empty)
        {
            _folders.Clear();
        }
    }

    public event EventHandler? Changed;

    public Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameEntry>>(_games.ToList());

    public Task<GameEntry?> GetGameAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_games.FirstOrDefault(g => g.Key == key));

    public Task SaveGameAsync(GameEntry game)
    {
        var index = _games.FindIndex(g => g.Key == game.Key);
        if (index >= 0)
        {
            _games[index] = game;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task AddPlaytimeAsync(string key, TimeSpan time)
    {
        var index = _games.FindIndex(g => g.Key == key);
        if (index >= 0)
        {
            _games[index] = _games[index] with { Playtime = _games[index].Playtime + time, LastPlayed = DateTimeOffset.Now };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetFoldersAsync() => Task.FromResult<IReadOnlyList<string>>(_folders.ToList());

    /// <summary>"Finds" the sample games in any folder, with a short simulated scan.</summary>
    public async Task<ScanResult> AddFolderAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_folders.Contains(path))
        {
            _folders.Add(path);
        }

        return await ScanAsync(progress, cancellationToken);
    }

    public Task RemoveFolderAsync(string path)
    {
        _folders.Remove(path);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var added = 0;
        for (var i = 0; i < Games.Count; i++)
        {
            progress?.Report(new ScanProgress(Games.Count, i, Games[i].RomPath));
            await Task.Delay(15, cancellationToken);
            if (_games.All(g => g.Key != Games[i].Key))
            {
                _games.Add(Games[i]);
                added++;
            }
        }

        progress?.Report(new ScanProgress(Games.Count, Games.Count, null));
        Changed?.Invoke(this, EventArgs.Empty);
        return new ScanResult(added, 0, 0, Games.Count - added, 0);
    }

    public Task<CatalogPreview> PreviewCatalogAsync(string path) =>
        Task.FromResult(new CatalogPreview(Path.GetFileNameWithoutExtension(path.Replace('\\', '/')), 677, 652, 3));

    public Task<CatalogImportResult> ImportCatalogAsync(string path)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(new CatalogImportResult(Path.GetFileName(path.Replace('\\', '/')), 677, 3));
    }

    public Task<GameProfile> GetProfileAsync(string key) =>
        Task.FromResult(new GameProfile { RunAheadSafe = true }.Merge(_overrides.GetValueOrDefault(key)));

    public Task<GameProfile?> GetProfileOverridesAsync(string key) => Task.FromResult(_overrides.GetValueOrDefault(key));

    public Task SaveProfileOverridesAsync(string key, GameProfile? overrides)
    {
        if (overrides is null)
        {
            _overrides.Remove(key);
        }
        else
        {
            _overrides[key] = overrides;
        }

        return Task.CompletedTask;
    }

    private static GameEntry Game(
        int id,
        string title,
        GameRegion region,
        string publisher,
        int year,
        string genre,
        Ownership owned = Ownership.None,
        PlayStatus status = PlayStatus.Unplayed,
        bool favorite = false,
        int? rating = null,
        double hours = 0,
        int? lastPlayedDaysAgo = null,
        int players = 1,
        bool licensed = true) => new()
        {
            Key = $"sample:{id}",
            IsCataloged = true,
            Title = title,
            Region = region,
            Publisher = publisher,
            ReleaseYear = year,
            Genre = genre,
            Players = players,
            IsLicensed = licensed,
            Owned = owned,
            Status = status,
            IsFavorite = favorite,
            Rating = rating,
            Playtime = TimeSpan.FromHours(hours),
            LastPlayed = lastPlayedDaysAgo is { } days ? Now.AddDays(-days) : null,
            RomPath = owned.HasFlag(Ownership.DigitalDump) ? $@"C:\Games\NES\{title}.nes" : null,
        };
}
