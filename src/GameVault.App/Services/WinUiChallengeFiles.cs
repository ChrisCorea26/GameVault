using GameVault.ViewModels.Services;
using Microsoft.Windows.Storage.Pickers;

namespace GameVault.App.Services;

/// <summary>Windows App SDK pickers for challenge and result files (SPEC §7.9).</summary>
public sealed class WinUiChallengeFiles : IChallengeFiles
{
    public async Task<string?> OpenAsync(ChallengeFileKind kind)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Open",
        };
        picker.FileTypeFilter.Add(Extension(kind));

        var result = await picker.PickSingleFileAsync();
        return result is null ? null : await File.ReadAllTextAsync(result.Path);
    }

    public async Task<bool> SaveAsync(ChallengeFileKind kind, string suggestedName, string content)
    {
        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = string.Concat(suggestedName.Split(Path.GetInvalidFileNameChars())),
        };
        picker.FileTypeChoices.Add(kind == ChallengeFileKind.Challenge ? "GameVault challenge" : "GameVault challenge result", [Extension(kind)]);

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return false;
        }

        await File.WriteAllTextAsync(result.Path, content);
        return true;
    }

    private static string Extension(ChallengeFileKind kind) => kind == ChallengeFileKind.Challenge ? ".gvchallenge" : ".gvresult";
}
