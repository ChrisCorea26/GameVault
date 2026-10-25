using GameVault.ViewModels.Services;
using Windows.ApplicationModel.DataTransfer;

namespace GameVault.App.Services;

/// <summary>Copies text to the Windows clipboard (works for unpackaged apps).</summary>
public sealed class WinUiClipboard : IClipboardService
{
    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
