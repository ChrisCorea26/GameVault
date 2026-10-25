using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.LoadCommand.Execute(null);
}
