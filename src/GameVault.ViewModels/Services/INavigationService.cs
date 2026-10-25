namespace GameVault.ViewModels.Services;

public interface INavigationService
{
    AppPage? CurrentPage { get; }

    event EventHandler<AppPage>? Navigated;

    void NavigateTo(AppPage page);

    /// <summary>Navigates with a parameter, such as the game file path for the Player.</summary>
    void NavigateTo(AppPage page, object? parameter);

    bool CanGoBack { get; }

    void GoBack();
}
