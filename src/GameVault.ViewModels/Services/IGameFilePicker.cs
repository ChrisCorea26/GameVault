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
}
