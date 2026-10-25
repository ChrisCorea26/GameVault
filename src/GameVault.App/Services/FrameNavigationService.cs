using GameVault.App.Views;
using GameVault.ViewModels.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace GameVault.App.Services;

/// <summary>Navigates the shell's content <see cref="Frame"/> to the page for each <see cref="AppPage"/>.</summary>
public sealed class FrameNavigationService : INavigationService
{
    private Frame? _frame;

    public AppPage? CurrentPage { get; private set; }

    public event EventHandler<AppPage>? Navigated;

    /// <summary>Called by <see cref="MainWindow"/> once its frame exists.</summary>
    public void Initialize(Frame frame) => _frame = frame;

    public void NavigateTo(AppPage page)
    {
        var frame = _frame ?? throw new InvalidOperationException("Navigation frame has not been initialized.");
        var pageType = page switch
        {
            AppPage.Library => typeof(LibraryPage),
            AppPage.Settings => typeof(SettingsPage),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
        };

        if (frame.Navigate(pageType, null, new EntranceNavigationTransitionInfo()))
        {
            CurrentPage = page;
            Navigated?.Invoke(this, page);
        }
    }
}
