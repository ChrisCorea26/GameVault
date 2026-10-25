using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Views;

public sealed partial class DumpCartridgePage : Page
{
    public DumpCartridgePage()
    {
        ViewModel = App.GetService<DumpCartridgeViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public DumpCartridgeViewModel ViewModel { get; }

    // Data loads on Loaded, never in the constructor (SPEC §9.1).
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(null);
        SearchBox.Focus(FocusState.Programmatic);
    }
}
