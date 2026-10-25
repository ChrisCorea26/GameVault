using GameVault.ViewModels.Services;
using Microsoft.Windows.Storage.Pickers;

namespace GameVault.App.Services;

/// <summary>Windows App SDK pickers for code lists (SPEC §7.8).</summary>
public sealed class WinUiCodeFilePicker : ICodeFilePicker
{
    public async Task<string?> PickImportFileAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Import",
        };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".cht");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickCollectionFileAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Import",
        };
        picker.FileTypeFilter.Add(".xlsx");
        picker.FileTypeFilter.Add(".csv");

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public async Task<string?> PickExportFileAsync(string suggestedName)
    {
        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        picker.FileTypeChoices.Add("Code list", [".txt"]);

        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }
}
