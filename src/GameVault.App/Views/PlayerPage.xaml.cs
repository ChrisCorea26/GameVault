using GameVault.App.Services;
using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace GameVault.App.Views;

public sealed partial class PlayerPage : Page
{
    private object? _launch;
    private DispatcherTimer? _practiceTimer;

    public PlayerPage()
    {
        ViewModel = App.GetService<PlayerViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public PlayerViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _launch = e.Parameter;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Stop();
        base.OnNavigatedFrom(e);
    }

    // The game starts on Loaded, never in the constructor (SPEC §9.1).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.MainWindow.Activated += OnWindowActivated;
        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            controllers.PauseRequested += OnPauseRequested;
        }

        // The Slider handles its own pointer events, so listen for handled ones too (view-only glue).
        RewindSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ViewModel.BeginScrub()), handledEventsToo: true);
        RewindSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        RewindSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);

        ViewModel.StartCommand.Execute(_launch);
        Game.Focus(FocusState.Programmatic);

        // The practice timer shows emulated time; refresh it a few times a second (view-only concern).
        _practiceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _practiceTimer.Tick += (_, _) => ViewModel.Practice.Tick();
        _practiceTimer.Start();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _practiceTimer?.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        App.MainWindow.Activated -= OnWindowActivated;
        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            controllers.PauseRequested -= OnPauseRequested;
        }
    }

    // Focus moves to the password box when the panel opens, and back to the game when it closes.
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsEnteringPassword))
        {
            if (ViewModel.IsEnteringPassword)
            {
                PasswordBox.Focus(FocusState.Programmatic);
            }
            else
            {
                Game.Focus(FocusState.Programmatic);
            }
        }
    }

    private void RewindSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        ViewModel.ScrubTo(e.NewValue);

    private void EndScrub()
    {
        if (ViewModel.IsScrubbing)
        {
            ViewModel.EndScrub();
            ReturnFocusToGame();
        }
    }

    private void OnPauseRequested(object? sender, EventArgs e)
    {
        UiTrace.Write("the pad asked to pause", DispatcherQueue.HasThreadAccess);
        if (DispatcherQueue.HasThreadAccess)
        {
            UiGuard.Run("Pad pause", () => ViewModel.TogglePauseCommand.Execute(null));
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => UiGuard.Run("Pad pause", () => ViewModel.TogglePauseCommand.Execute(null)));
        }
    }

    // Coming back to the window resumes a game that paused itself and gives the keyboard back to the game
    // (input.md §4.4), unless the password box is being typed into.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args) =>
        UiGuard.Run("Player window activation", () => OnWindowActivated(args.WindowActivationState));

    private void OnWindowActivated(WindowActivationState state)
    {
        if (state == WindowActivationState.Deactivated)
        {
            ViewModel.OnWindowDeactivated();
            return;
        }

        ViewModel.OnWindowActivated();
        ReturnFocusToGame();
    }

    private void Flyout_Closed(object sender, object e) => ReturnFocusToGame();

    // Closing the Controllers flyout stops Remap… waiting for a key or button.
    private void ControllersFlyout_Closed(object sender, object e)
    {
        ViewModel.Controllers.Remap.CancelListening();
        ReturnFocusToGame();
    }

    // The list is read when it opens, so it shows the latest unlocks and progress.
    private void AchievementsFlyout_Opening(object sender, object e) => ViewModel.Achievements?.RefreshCommand.Execute(null);

    private void DiskSide_DropDownClosed(object sender, object e) => ReturnFocusToGame();

    private void ReturnFocusToGame()
    {
        if (!ViewModel.IsEnteringPassword)
        {
            Game.Focus(FocusState.Programmatic);
        }
    }

    // Esc and F11 only mark the key handled here; the work runs once WinUI has finished dispatching the key press.
    // Changing the window's presenter (full screen) or the page from inside the accelerator re-enters WinUI's input
    // and windowing code, which ended the app with a fail-fast and no crash.log (docs/bugs.md GV-027).
    private void Pause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        UiTrace.Write("Esc pressed", DispatcherQueue.HasThreadAccess);
        DispatcherQueue.TryEnqueue(() => UiGuard.Run("Esc", () =>
        {
            var fullScreen = ViewModel.IsFullScreen;
            UiTrace.Write($"Esc: {(fullScreen ? "leave full screen" : "pause or resume")}", DispatcherQueue.HasThreadAccess);
            if (!ViewModel.ExitFullScreen())
            {
                ViewModel.TogglePauseCommand.Execute(null);
            }

            UiTrace.Write($"Esc done: {ViewModel.Status}", DispatcherQueue.HasThreadAccess);
        }));
    }

    private void FullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        UiTrace.Write("full screen key pressed", DispatcherQueue.HasThreadAccess);
        DispatcherQueue.TryEnqueue(() => UiGuard.Run("Full screen key", () =>
        {
            ViewModel.ToggleFullScreenCommand.Execute(null);
            ReturnFocusToGame();
        }));
    }
}
