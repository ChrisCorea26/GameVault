namespace GameVault.ViewModels.Services;

/// <summary>A code in the player's list for a game (SPEC §7.8).</summary>
public sealed record CodeItem(string Id, string Code, string Description, bool Enabled);

/// <summary>A code from the bundled database, offered read-only until the player adds it.</summary>
public sealed record CodeSuggestion(string Code, string Description);

/// <summary>
/// Codes for one game from a source outside the player's list: their imported code collection or GameVault's
/// GameGenie.com lookup. <see cref="Credit"/> says where they came from and is always shown with them.
/// </summary>
public sealed record CodeSourceResult(
    string Title,
    string Credit,
    Uri? SourceUrl,
    IReadOnlyList<CodeSuggestion> Codes,
    IReadOnlyList<string> Notes);

/// <summary>An imported code collection (Settings → Codes).</summary>
public sealed record CodeCollectionInfo(string Name, int Games, int Codes);

/// <summary>How importing a code collection went.</summary>
public sealed record CodeCollectionImportResult(string Name, int Games, int Codes, IReadOnlyList<string> Errors);

/// <summary>A code source couldn't be reached or read. <see cref="Exception.Message"/> is safe to show.</summary>
public sealed class CodeSourceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How an import went: how many codes were added, or why none were.</summary>
public sealed record CodeImportResult(int Added, IReadOnlyList<string> Errors);

/// <summary>
/// Game Genie and RAM codes per game (SPEC §7.8). Changes to the running game's codes reach the emulator
/// between frames. <see cref="Changed"/> is raised on the UI thread with the game key, including when loading
/// a state or rewinding brought back a different set of codes.
/// </summary>
public interface ICheatService
{
    event EventHandler<string>? Changed;

    Task<IReadOnlyList<CodeItem>> GetAsync(string gameKey);

    Task<IReadOnlyList<CodeSuggestion>> GetSuggestionsAsync(string gameKey);

    /// <summary>Adds a code that <see cref="GameVault.Core.Cheats.CheatCode.TryParse"/> accepts.</summary>
    Task AddAsync(string gameKey, string code, string description);

    Task SetEnabledAsync(string gameKey, string id, bool enabled);

    Task SetAllEnabledAsync(string gameKey, bool enabled);

    Task DeleteAsync(string gameKey, string id);

    /// <summary>Imports a plain-text list or an FCEUX .cht file. One bad line rejects the whole file.</summary>
    Task<CodeImportResult> ImportAsync(string gameKey, string path);

    Task ExportAsync(string gameKey, string path);

    /// <summary>Turns on the game's enabled codes in the emulator; called when the game starts.</summary>
    Task ApplyAsync(string gameKey);

    /// <summary>The player's imported code collections.</summary>
    Task<IReadOnlyList<CodeCollectionInfo>> GetCollectionsAsync();

    /// <summary>Imports a code collection (.xlsx or .csv) the player supplies; it stays in their own app folder.</summary>
    Task<CodeCollectionImportResult> ImportCollectionAsync(string path);

    Task RemoveCollectionAsync(string name);

    /// <summary>
    /// This game's codes in the player's collections, matched by <paramref name="title"/> or by the collection
    /// game the player chose for it. Null when no collection has the game.
    /// </summary>
    Task<CodeSourceResult?> GetCollectionCodesAsync(string gameKey, string? title);

    /// <summary>Titles in the player's collections that contain <paramref name="text"/>.</summary>
    Task<IReadOnlyList<string>> SearchCollectionsAsync(string text);

    /// <summary>Remembers which collection game this game is, when the titles don't match by themselves.</summary>
    Task SetCollectionTitleAsync(string gameKey, string collectionTitle);

    /// <summary>
    /// The game's codes from GameGenie.com, downloaded when the player asks. Null when the site has no page
    /// for the title; throws <see cref="CodeSourceException"/> when the site can't be reached.
    /// </summary>
    Task<CodeSourceResult?> GetOnlineCodesAsync(string gameKey, string? title);
}

/// <summary>Picks code list files to import and export.</summary>
public interface ICodeFilePicker
{
    Task<string?> PickImportFileAsync();

    Task<string?> PickExportFileAsync(string suggestedName);

    /// <summary>A code collection spreadsheet (.xlsx) or .csv file.</summary>
    Task<string?> PickCollectionFileAsync();
}
