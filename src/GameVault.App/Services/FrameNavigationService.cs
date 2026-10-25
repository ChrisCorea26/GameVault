using GameVault.App.Views;
using GameVault.ViewModels.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameVault.App.Services;

/// <summary>Navigates the shell's content <see cref="Frame"/> to the page for each <see cref="AppPage"/>.</summary>
public sealed class FrameNavigationService : INavigationService
{
    private readonly Stack<AppPage> _history = new();
    private Frame? _frame;

    public AppPage? CurrentPage { get; private set; }

    public bool CanGoBack => _frame?.CanGoBack == true && _history.Count > 0;

    public event EventHandler<AppPage>? Navigated;

    /// <summary>Called by <see cref="MainWindow"/> once its frame exists.</summary>
    public void Initialize(Frame frame) => _frame = frame;

    public void NavigateTo(AppPage page) => NavigateTo(page, null);

    public void NavigateTo(AppPage page, object? parameter)
    {
        var frame = _frame ?? throw new InvalidOperationException("Navigation frame has not been initialized.");
        var pageType = page switch
        {
            AppPage.Library => typeof(LibraryPage),
            AppPage.Settings => typeof(SettingsPage),
            AppPage.Player => typeof(PlayerPage),
            AppPage.GameDetails => typeof(GameDetailsPage),
            AppPage.CatalogImport => typeof(CatalogImportPage),
            AppPage.DumpCartridge => typeof(DumpCartridgePage),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
        };

        var previous = CurrentPage;
        if (frame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo()))
        {
            if (previous is { } p)
            {
                _history.Push(p);
            }

            CurrentPage = page;
            Navigated?.Invoke(this, page);
        }
    }

    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _frame!.GoBack();
        CurrentPage = _history.Pop();
        Navigated?.Invoke(this, CurrentPage.Value);
    }
}
