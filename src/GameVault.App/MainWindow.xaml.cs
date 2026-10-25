using System.ComponentModel;
using GameVault.App.Services;
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

    public MainWindow(ShellViewModel viewModel, FrameNavigationService navigation)
    {
        ViewModel = viewModel;
        _navigation = navigation;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 820));

        _navigation.Initialize(ContentFrame);
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public ShellViewModel ViewModel { get; }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e) => ViewModel.StartCommand.Execute(null);

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
