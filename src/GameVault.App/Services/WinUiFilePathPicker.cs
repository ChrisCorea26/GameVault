using GameVault.ViewModels.Services;
using Microsoft.Windows.Storage.Pickers;

namespace GameVault.App.Services;

/// <summary>The "Browse…" and "Choose BIOS file…" buttons in Settings: a Windows App SDK file picker.</summary>
public sealed class WinUiFilePathPicker : IFilePathPicker
{
    public async Task<string?> PickFileAsync(FilePathRequest request)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = request.CommitButtonText,
        };

        // "*" shows every file, so it isn't mixed with extensions.
        if (request.FileTypes.Count == 0 || request.FileTypes.Contains("*"))
        {
            picker.FileTypeFilter.Add("*");
        }
        else
        {
            foreach (var extension in request.FileTypes)
            {
                picker.FileTypeFilter.Add(extension);
            }
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
