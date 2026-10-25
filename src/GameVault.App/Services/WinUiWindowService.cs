using GameVault.ViewModels.Services;
using Microsoft.UI.Windowing;

namespace GameVault.App.Services;

/// <summary>Full screen through the window's AppWindow presenter (Windows App SDK).</summary>
public sealed class WinUiWindowService : IWindowService
{
    private bool _watching;

    public bool IsFullScreen => App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public event EventHandler? FullScreenChanged;

    public void SetFullScreen(bool fullScreen)
    {
        Watch();
        if (IsFullScreen != fullScreen)
        {
            App.MainWindow.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        }
    }

    // Windows can also change the presenter (for example Win+Shift+Enter), so follow the window itself.
    private void Watch()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        App.MainWindow.AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange)
            {
                FullScreenChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }
}
