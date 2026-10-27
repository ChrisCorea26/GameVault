using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GameVault.App.Controls;

/// <summary>Port cards and Remap… (docs/design/input.md §9). Set <see cref="ViewModel"/> from the page with x:Bind.</summary>
public sealed partial class ControllerPortsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(InputSettingsViewModel), typeof(ControllerPortsPanel), new PropertyMetadata(null, OnViewModelChanged));

    public ControllerPortsPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => ViewModel?.Remap.CancelListening();
    }

    public InputSettingsViewModel? ViewModel
    {
        get => (InputSettingsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // While Remap… waits for a key, the next key goes to it rather than to focus navigation (view-only glue).
    private void Panel_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel?.Remap is { IsListening: true } remap && remap.CaptureKey(GameVault.Platform.Input.VirtualKeys.Normalize((int)e.Key, e.KeyStatus.ScanCode)))
        {
            e.Handled = true;
        }
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ControllerPortsPanel)d).Bindings.Update();
}
