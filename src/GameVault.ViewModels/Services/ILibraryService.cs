using GameVault.Library.Catalog;
using GameVault.Library.Models;
using GameVault.Library.Profiles;
using GameVault.Library.Scanning;

namespace GameVault.ViewModels.Services;

/// <summary>A DAT file's contents before importing it (the import wizard's preview).</summary>
public sealed record CatalogPreview(string Name, int Games, int Licensed, int Skipped);

/// <summary>
/// The user's library (SPEC §6): games from imported catalogs and scanned folders, collection fields,
/// playtime, folders and game profiles. Events are raised on the UI thread.
/// </summary>
public interface ILibraryService
{
    /// <summary>The library changed (a scan finished, a folder changed on disk, a catalog was imported).</summary>
    event EventHandler? Changed;

    Task<IReadOnlyList<GameEntry>> GetGamesAsync(CancellationToken cancellationToken = default);

    Task<GameEntry?> GetGameAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Saves the fields the user edits (ownership, status, favorite, rating, notes, cover, title).</summary>
    Task SaveGameAsync(GameEntry game);

    Task AddPlaytimeAsync(string key, TimeSpan time);

    Task<IReadOnlyList<string>> GetFoldersAsync();

    /// <summary>Adds a folder and scans it.</summary>
    Task<ScanResult> AddFolderAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    Task RemoveFolderAsync(string path);

    /// <summary>Rescans every library folder.</summary>
    Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<CatalogPreview> PreviewCatalogAsync(string path);

    Task<CatalogImportResult> ImportCatalogAsync(string path);

    /// <summary>The game's profile with the user's overrides applied.</summary>
    Task<GameProfile> GetProfileAsync(string key);

    /// <summary>The user's own overrides only (Game Properties); null clears them.</summary>
    Task<GameProfile?> GetProfileOverridesAsync(string key);

    Task SaveProfileOverridesAsync(string key, GameProfile? overrides);
}
