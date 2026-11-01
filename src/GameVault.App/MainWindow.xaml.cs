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
        // Run after WinUI has finished the presenter change that raised this, not inside its callback (GV-027).
        window.FullScreenChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            UiGuard.Run("Showing the window chrome", () => ShowChrome(!window.IsFullScreen)));

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 820));

        _navigation.Initialize(ContentFrame);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Activated += OnActivated;
    }

    /// <summary>Full-screen play hides the title bar and the navigation pane, leaving only the game.</summary>
    /// <remarks>
    /// The title bar element is handed back to WinUI (<c>SetTitleBar(null)</c>) before it's collapsed, and only given
    /// to it again once it's visible: WinUI keeps the window's caption and drag regions in step with that element, and
    /// collapsing it while WinUI still owns it is the likeliest cause of the fail-fasts in Microsoft.UI.Input and
    /// Microsoft.UI.Windowing.Core just after the window entered or left full screen (docs/bugs.md GV-027).
    /// </remarks>
    private void ShowChrome(bool visible)
    {
        UiTrace.Write($"window chrome: {(visible ? "showing" : "hiding")}", onUiThread: true);
        if (!visible)
        {
            SetTitleBar(null);
        }

        AppTitleBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.RowDefinitions[0].Height = visible ? new GridLength(48) : new GridLength(0);
        NavView.IsPaneVisible = visible;
        if (visible)
        {
            SetTitleBar(AppTitleBar);
        }

        UiTrace.Write($"window chrome: {(visible ? "shown" : "hidden")}", onUiThread: true);
    }

    public ShellViewModel ViewModel { get; }

    // Devices start once the window is up, never in a constructor (SPEC §9.1).
    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.StartCommand.Execute(null);

        // USER/GDI object counts in ui-trace.log, to find the leak behind the popup fail-fasts (GV-027).
        new HandleMonitor(_navigation).Start(DispatcherQueue);

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
                    UiGuard.Run($"Pad navigation {action}", () => padNavigation.Handle(action));
                }
                else
                {
                    UiTrace.Write($"pad navigation {action} arrived off the UI thread; moved to it", onUiThread: false);
                    DispatcherQueue.TryEnqueue(() => UiGuard.Run($"Pad navigation {action}", () => padNavigation.Handle(action)));
                }
            };
            await controllers.StartAsync();
        }
    }

    // input.md §4.4 and §7: nothing stays held, and pads are ignored, while another window is active.
    private void OnActivated(object sender, WindowActivatedEventArgs args) =>
        UiGuard.Run("Window activation", () => OnActivated(args.WindowActivationState != WindowActivationState.Deactivated));

    private static void OnActivated(bool active)
    {
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
