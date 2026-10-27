using GameVault.ViewModels.Services;
using Microsoft.Windows.Storage.Pickers;

namespace GameVault.App.Services;

/// <summary>Windows App SDK pickers; they work for unpackaged apps without window-handle setup.</summary>
public sealed class WinUiGameFilePicker : IGameFilePicker
{
    public async Task<string?> PickGameFileAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Play",
        };
        foreach (var extension in GameVault.Library.Scanning.RomFiles.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        picker.FileTypeFilter.Add(".zip");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickSystemFileAsync(IReadOnlyList<string> extensions)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Choose",
        };
        foreach (var extension in extensions.Count > 0 ? extensions : ["*"])
        {
            picker.FileTypeFilter.Add(extension);
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickDiskImageAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Insert",
        };
        foreach (var extension in (string[])[".dsk", ".do", ".po", ".nib", ".woz"])
        {
            picker.FileTypeFilter.Add(extension);
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Add folder",
        };

        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<string?> PickCatalogFileAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = "Choose",
        };
        picker.FileTypeFilter.Add(".dat");
        picker.FileTypeFilter.Add(".xml");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickImageAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = "Use picture",
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
