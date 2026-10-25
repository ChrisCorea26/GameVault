using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        ViewModel = App.GetService<LibraryViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LibraryViewModel ViewModel { get; }

    // Data loads on Loaded, never in the constructor (SPEC §9.1).
    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Detach();

    private void Games_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenDetailsCommand.Execute(e.ClickedItem as GameTileViewModel);

    private void PlayMenu_Click(object sender, RoutedEventArgs e) =>
        ViewModel.PlayCommand.Execute((sender as FrameworkElement)?.Tag as GameTileViewModel);
}
