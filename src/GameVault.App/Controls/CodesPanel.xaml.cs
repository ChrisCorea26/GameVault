using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Controls;

/// <summary>The Codes list (SPEC §7.8). Set <see cref="ViewModel"/> from the page with x:Bind.</summary>
public sealed partial class CodesPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CodesViewModel), typeof(CodesPanel), new PropertyMetadata(null, OnViewModelChanged));

    public CodesPanel()
    {
        InitializeComponent();
    }

    public CodesViewModel? ViewModel
    {
        get => (CodesViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // The control's own x:Bind paths start at ViewModel, so refresh them when the page sets it.
    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CodesPanel)d).Bindings.Update();
}
