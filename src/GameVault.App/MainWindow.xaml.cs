using System.ComponentModel;
using GameVault.App.Services;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.Platform.Storage;
using GameVault.ViewModels;
using GameVault.ViewModels.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace GameVault.App;

/// <summary>The shell window. Code-behind is limited to title bar, frame hookup and selection sync.</summary>
public sealed partial class MainWindow : Window
{
    private readonly FrameNavigationService _navigation;

    public MainWindow(ShellViewModel viewModel, FrameNavigationService navigation, IWindowService window)
    {
        ViewModel = viewModel;
        _navigation = navigation;
        window.FullScreenChanged += (_, _) => ShowChrome(!window.IsFullScreen);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 820));

        _navigation.Initialize(ContentFrame);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += OnActivated;
    }

    /// <summary>Full-screen play hides the title bar and the navigation pane, leaving only the game.</summary>
    private void ShowChrome(bool visible)
    {
        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.RowDefinitions[0].Height = visible ? new GridLength(48) : new GridLength(0);
        NavView.IsPaneVisible = visible;
    }

    public ShellViewModel ViewModel { get; }

    // Devices start once the window is up, never in a constructor (SPEC §9.1).
    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.StartCommand.Execute(null);

        if (App.TryGetService<LibraryService>() is { } library)
        {
            await library.StartWatchingAsync(); // folder changes rescan the library (SPEC §6.2)

            // First launch: the freely licensed homebrew GameVault ships (SPEC §6.4) goes into the library once.
            var store = App.GetService<ISettingsStore>();
            var settings = await store.LoadAsync();
            if (!settings.BundledHomebrewAdded)
            {
                await library.AddBundledHomebrewAsync(Path.Combine(AppContext.BaseDirectory, "data", "homebrew"));
                await store.SaveAsync(await store.LoadAsync() with { BundledHomebrewAdded = true });
            }
        }

        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            if (SynchronizationContext.Current is { } ui)
            {
                controllers.UseContext(ui);
            }

            var padNavigation = App.GetService<PadNavigation>();
            padNavigation.Attach(this, ContentFrame, NavView);
            controllers.Navigate += (_, action) =>
            {
                // WinUI must only be called on its own thread; anything else ends the app with a fail-fast (GV-027).
                if (DispatcherQueue.HasThreadAccess)
                {
                    padNavigation.Handle(action);
                }
                else
                {
                    UiTrace.Write($"pad navigation {action} arrived off the UI thread; moved to it", onUiThread: false);
                    DispatcherQueue.TryEnqueue(() => padNavigation.Handle(action));
                }
            };
            await controllers.StartAsync();
        }
    }

    // input.md §4.4 and §7: nothing stays held, and pads are ignored, while another window is active.
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            controllers.AppActive = active;
        }

        if (!active)
        {
            App.GetService<InputService>().ReleaseAll();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ViewModel.NavigateCommand.Execute(AppPage.Settings);
        }
        else if (args.SelectedItemContainer?.Tag is string tag && Enum.TryParse<AppPage>(tag, out var page))
        {
            ViewModel.NavigateCommand.Execute(page);
        }
    }

    // Keeps the highlighted menu item in step when navigation starts from the ViewModel.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentPage))
        {
            return;
        }

        object? selected = ViewModel.CurrentPage switch
        {
            AppPage.Library => LibraryItem,
            AppPage.Settings => NavView.SettingsItem,
            _ => null, // the Player has no menu item
        };

        if (!ReferenceEquals(NavView.SelectedItem, selected))
        {
            NavView.SelectedItem = selected;
        }
    }
}
