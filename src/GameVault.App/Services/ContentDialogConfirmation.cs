using GameVault.ViewModels.Services;
using Microsoft.UI.Xaml.Controls;

namespace GameVault.App.Services;

/// <summary>Confirmation before destructive actions (SPEC §9.3), as a ContentDialog over the main window.</summary>
public sealed class ContentDialogConfirmation : IConfirmationService
{
    private bool _open;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        // Only one ContentDialog can be open at a time; a second request while one is up counts as cancelled.
        if (_open || App.MainWindow.Content?.XamlRoot is not { } root)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, // a stray Enter or A press keeps things as they are
        };

        _open = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _open = false;
        }
    }
}
