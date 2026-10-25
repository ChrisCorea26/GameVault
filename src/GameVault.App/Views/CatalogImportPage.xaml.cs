using GameVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Views;

public sealed partial class CatalogImportPage : Page
{
    public CatalogImportPage()
    {
        ViewModel = App.GetService<CatalogImportViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ChooseButton.Focus(FocusState.Programmatic);
    }

    public CatalogImportViewModel ViewModel { get; }
}
