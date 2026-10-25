namespace GameVault.ViewModels.Services;

public interface IGameFilePicker
{
    /// <summary>Asks the user for a game file. Returns null if they cancel.</summary>
    Task<string?> PickGameFileAsync();
}
