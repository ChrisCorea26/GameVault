namespace GameVault.ViewModels.Services;

public interface INavigationService
{
    AppPage? CurrentPage { get; }

    event EventHandler<AppPage>? Navigated;

    void NavigateTo(AppPage page);
}
