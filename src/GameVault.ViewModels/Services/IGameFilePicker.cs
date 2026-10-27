namespace GameVault.ViewModels.Services;

public interface IGameFilePicker
{
    /// <summary>Asks the user for a game file. Returns null if they cancel.</summary>
    Task<string?> PickGameFileAsync();

    /// <summary>Asks for a folder of games. Returns null if they cancel.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Asks for a catalog DAT (.dat or .xml). Returns null if they cancel.</summary>
    Task<string?> PickCatalogFileAsync();

    /// <summary>Asks for a picture (a cover photo). Returns null if they cancel.</summary>
    Task<string?> PickImageAsync();

    /// <summary>Asks for an Apple II disk image (.dsk, .do, .po, .nib or .woz). Returns null if they cancel.</summary>
    Task<string?> PickDiskImageAsync() => PickGameFileAsync();

    /// <summary>
    /// Asks for one of the player's own system files, such as a BIOS (Settings, GV-009). <paramref name="extensions"/>
    /// are like ".rom"; an empty list allows any file. Returns null if they cancel.
    /// </summary>
    Task<string?> PickSystemFileAsync(IReadOnlyList<string> extensions) => Task.FromResult<string?>(null);
}
