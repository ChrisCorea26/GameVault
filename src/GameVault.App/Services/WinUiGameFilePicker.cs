using GameVault.ViewModels.Services;
using Microsoft.Windows.Storage.Pickers;

namespace GameVault.App.Services;

/// <summary>Windows App SDK file picker; works for unpackaged apps without window-handle setup.</summary>
public sealed class WinUiGameFilePicker : IGameFilePicker
{
    public async Task<string?> PickGameFileAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Play",
        };
        picker.FileTypeFilter.Add(".nes");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
