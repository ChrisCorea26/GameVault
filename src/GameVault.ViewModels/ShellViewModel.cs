using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The main window: title bar and top-level navigation.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;

    public ShellViewModel(INavigationService navigation, AppEnvironment environment)
    {
        _navigation = navigation;
        IsSampleData = environment.IsSampleData;
        _navigation.Navigated += (_, page) => CurrentPage = page;
    }

    public string Title => "GameVault";

    /// <summary>Shows a "Sample data" badge in the title bar so previews are never mistaken for a real library.</summary>
    public bool IsSampleData { get; }

    [ObservableProperty]
    public partial AppPage? CurrentPage { get; private set; }

    /// <summary>Called once the window has loaded. Opens the Library.</summary>
    [RelayCommand]
    private void Start()
    {
        if (_navigation.CurrentPage is null)
        {
            _navigation.NavigateTo(AppPage.Library);
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
