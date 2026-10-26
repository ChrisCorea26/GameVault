using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        Codes = App.GetService<CodeCollectionsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => ViewModel.RetroAchievements?.Detach();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Settings → Codes: the player's imported code collections (SPEC §7.8).</summary>
    public CodeCollectionsViewModel Codes { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadCommand.Execute(null);
        Codes.LoadCommand.Execute(null);
        ViewModel.RetroAchievements?.Attach();
    }

    private void DumperPath_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SaveDumperPathCommand.Execute(null);

    private void FdsBiosPath_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SaveFdsBiosPathCommand.Execute(null);

    private void AppleRoms_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SaveAppleRomPathsCommand.Execute(null);

    private void PlayStationBiosPath_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SavePlayStationBiosPathCommand.Execute(null);
}
