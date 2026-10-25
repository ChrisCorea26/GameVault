using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public sealed class FakeNavigationService : INavigationService
{
    private readonly Stack<AppPage> _back = new();

    public List<(AppPage Page, object? Parameter)> History { get; } = [];

    public AppPage? CurrentPage { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public event EventHandler<AppPage>? Navigated;

    public void NavigateTo(AppPage page) => NavigateTo(page, null);

    public void NavigateTo(AppPage page, object? parameter)
    {
        if (CurrentPage is { } current)
        {
            _back.Push(current);
        }

        History.Add((page, parameter));
        CurrentPage = page;
        Navigated?.Invoke(this, page);
    }

    public void GoBack()
    {
        CurrentPage = _back.Pop();
        Navigated?.Invoke(this, CurrentPage.Value);
    }
}
