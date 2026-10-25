using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GameVault.App.Views;

public sealed partial class GameDetailsPage : Page
{
    private string? _gameKey;

    public GameDetailsPage()
    {
        ViewModel = App.GetService<GameDetailsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public GameDetailsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameKey = e.Parameter as string;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Detach();
        base.OnNavigatedFrom(e);
    }

    // Data loads on Loaded, never in the constructor (SPEC §9.1).
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadCommand.ExecuteAsync(_gameKey);
        PlayButton.Focus(FocusState.Programmatic);
    }

    private void Text_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SaveTextCommand.Execute(null);
}
