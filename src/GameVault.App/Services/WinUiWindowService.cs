using GameVault.App.Controls;
using GameVault.ViewModels.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace GameVault.App.Services;

/// <summary>Full screen through the window's AppWindow presenter (Windows App SDK).</summary>
public sealed class WinUiWindowService : IWindowService
{
    private bool _watching;

    /// <summary>The presenter asked for and not yet applied; see <see cref="SetFullScreen"/>.</summary>
    private bool? _wanted;
    private DispatcherQueueTimer? _releaseTimer;

    public bool IsFullScreen => _wanted ?? App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public event EventHandler? FullScreenChanged;

    /// <summary>
    /// The presenter changes on the next dispatcher turn, never inside the caller: callers run in XAML callbacks
    /// (Loaded, key accelerators, a game starting), and changing the presenter from inside one re-enters WinUI's layout,
    /// input and windowing code, which ends the app with a fail-fast (docs/bugs.md GV-027).
    /// </summary>
    public void SetFullScreen(bool fullScreen)
    {
        Watch();
        var queued = _wanted is not null;
        _wanted = fullScreen;
        if (!queued)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(ApplyPresenter);
        }
    }

    private void ApplyPresenter() => UiGuard.Run("Changing the window presenter", () =>
    {
        var wanted = _wanted;
        _wanted = null;
        var isFullScreen = App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        if (wanted is { } fullScreen && fullScreen != isFullScreen)
        {
            // No frame is drawn or presented while the window changes size and presenter; the game picture catches
            // up (with the new size) once the window has settled (GV-027).
            PresentGate.Hold();
            UiTrace.Write($"window presenter: changing to {(fullScreen ? "FullScreen" : "Overlapped")} (frames held)", onUiThread: true);
            try
            {
                App.MainWindow.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            }
            finally
            {
                ReleaseFramesSoon();
            }
        }
    });

    /// <summary>Lets frames through again once the layout that follows the presenter change has run.</summary>
    private void ReleaseFramesSoon()
    {
        _releaseTimer ??= CreateReleaseTimer();
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    private DispatcherQueueTimer CreateReleaseTimer()
    {
        var timer = App.MainWindow.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            PresentGate.Release();
            UiTrace.Write("window presenter: frames released", onUiThread: true);
        };
        return timer;
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
            // Raised by the Windows App SDK's native windowing code: an exception escaping here is a fail-fast in
            // Microsoft.UI.Windowing.Core with no crash.log (GV-027), so it's caught and logged instead.
            if (args.DidPresenterChange)
            {
                UiTrace.Write($"window presenter is now {App.MainWindow.AppWindow.Presenter.Kind}", onUiThread: true);
                UiGuard.Run("Full screen change", () => FullScreenChanged?.Invoke(this, EventArgs.Empty));
            }
        };
    }
}
