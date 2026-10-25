using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace GameVault.App.Views;

public sealed partial class PlayerPage : Page
{
    private string? _gamePath;

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
        _gamePath = e.Parameter as string;
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
        ViewModel.StartCommand.Execute(_gamePath);
        Game.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => App.MainWindow.Activated -= OnWindowActivated;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            ViewModel.OnWindowDeactivated();
        }
    }

    private void Pause_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.TogglePauseCommand.Execute(null);
        args.Handled = true;
    }
}
