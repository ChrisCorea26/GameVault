using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The main window: title bar and top-level navigation.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly AppEnvironment _environment;

    public ShellViewModel(INavigationService navigation, AppEnvironment environment)
    {
        _navigation = navigation;
        _environment = environment;
        IsSampleData = environment.IsSampleData;
        _navigation.Navigated += (_, page) => CurrentPage = page;
    }

    public string Title => "GameVault";

    /// <summary>Shows a "Sample data" badge in the title bar so previews are never mistaken for a real library.</summary>
    public bool IsSampleData { get; }

    [ObservableProperty]
    public partial AppPage? CurrentPage { get; private set; }

    /// <summary>Called once the window has loaded. Opens the Library, then the requested start page.</summary>
    [RelayCommand]
    private void Start()
    {
        if (_navigation.CurrentPage is not null)
        {
            return;
        }

        _navigation.NavigateTo(AppPage.Library);
        switch (_environment.StartPage)
        {
            case AppPage.Settings:
                _navigation.NavigateTo(AppPage.Settings);
                break;
            case AppPage.Player:
                // Only sample data has a game to show without the user picking one.
                _navigation.NavigateTo(AppPage.Player, _environment.IsSampleData ? SampleData.SampleGameFilePicker.SamplePath : null);
                break;
        }
    }

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        if (_navigation.CurrentPage != page)
        {
            _navigation.NavigateTo(page);
        }
    }
}
