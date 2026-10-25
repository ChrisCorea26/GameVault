using Microsoft.UI.Xaml;

namespace GameVault.App.Helpers;

/// <summary>Small x:Bind function helpers that turn ViewModel values into WinUI types.</summary>
public static class BindingHelpers
{
    public static Thickness SelectionBorder(bool selected) => new(selected ? 2 : 0);

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
