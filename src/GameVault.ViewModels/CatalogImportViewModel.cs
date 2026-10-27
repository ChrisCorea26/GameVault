using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

public enum CatalogImportStep
{
    Choose,
    Preview,
    Importing,
    Done,
}

/// <summary>
/// The Catalog Import wizard (SPEC §6.1, §9.2): choose a DAT file (for example a No-Intro DAT the user
/// downloaded), see what it contains, import it, and see the result. GameVault ships no catalog data.
/// </summary>
public sealed partial class CatalogImportViewModel(ILibraryService library, IGameFilePicker picker, INavigationService navigation)
    : ObservableObject
{
    private string? _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsPreviewing), nameof(IsImporting), nameof(IsDone))]
    public partial CatalogImportStep Step { get; private set; }

    public bool IsChoosing => Step == CatalogImportStep.Choose;

    public bool IsPreviewing => Step == CatalogImportStep.Preview;

    public bool IsImporting => Step == CatalogImportStep.Importing;

    public bool IsDone => Step == CatalogImportStep.Done;

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        ErrorMessage = null;
        var path = await picker.PickCatalogFileAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            var preview = await library.PreviewCatalogAsync(path);
            if (preview.Games == 0)
            {
                ErrorMessage = "That file doesn't list any games GameVault plays. Choose a DAT file in the Logiqx XML format, such as a No-Intro or Redump DAT.";
                return;
            }

            _path = path;
            PreviewText = $"\"{preview.Name}\" lists {preview.Games:N0} games ({preview.Licensed:N0} licensed releases).";
            Step = CatalogImportStep.Preview;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            ErrorMessage = "GameVault couldn't read that file. " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_path is null)
        {
            return;
        }

        Step = CatalogImportStep.Importing;
        try
        {
            var result = await library.ImportCatalogAsync(_path);
            ResultText = $"Added {result.Games:N0} games to the checklist. Games in your folders that match are now named and grouped.";
            Step = CatalogImportStep.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            ErrorMessage = "The import didn't finish. " + ex.Message;
            Step = CatalogImportStep.Preview;
        }
    }

    [RelayCommand]
    private void StartOver()
    {
        _path = null;
        ErrorMessage = null;
        Step = CatalogImportStep.Choose;
    }

    [RelayCommand]
    private void Finish()
    {
        if (navigation.CanGoBack)
        {
            navigation.GoBack();
        }
        else
        {
            navigation.NavigateTo(AppPage.Library);
        }
    }
}
