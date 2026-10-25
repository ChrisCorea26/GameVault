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

    private void OnPauseRequested(object? sender, EventArgs e) => ViewModel.TogglePauseCommand.Execute(null);

    // Coming back to the window resumes a game that paused itself and gives the keyboard back to the game
    // (input.md §4.4), unless the password box is being typed into.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ViewModel.OnWindowDeactivated();
            return;
        }

        ViewModel.OnWindowActivated();
        ReturnFocusToGame();
    }

    private void Flyout_Closed(object sender, object e) => ReturnFocusToGame();

    private void ReturnFocusToGame()
    {
        if (!ViewModel.IsEnteringPassword)
        {
            Game.Focus(FocusState.Programmatic);
        }
    }

    private void Pause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.TogglePauseCommand.Execute(null);
        args.Handled = true;
    }
}
