namespace GameVault.ViewModels.Services;

/// <summary>The app window's full-screen state, for full-screen play (Player, Settings → Display).</summary>
public interface IWindowService
{
    bool IsFullScreen { get; }

    void SetFullScreen(bool fullScreen);

    /// <summary>Raised on the UI thread whenever full screen turns on or off, including by Windows itself.</summary>
    event EventHandler? FullScreenChanged;
}
