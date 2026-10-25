namespace GameVault.ViewModels.Services;

/// <summary>A code in the player's list for a game (SPEC §7.8).</summary>
public sealed record CodeItem(string Id, string Code, string Description, bool Enabled);

/// <summary>A code from the bundled database, offered read-only until the player adds it.</summary>
public sealed record CodeSuggestion(string Code, string Description);

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
}

/// <summary>Picks code list files to import and export.</summary>
public interface ICodeFilePicker
{
    Task<string?> PickImportFileAsync();

    Task<string?> PickExportFileAsync(string suggestedName);
}
