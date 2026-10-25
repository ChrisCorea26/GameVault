using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.SampleData;

/// <summary>A window that only remembers whether it is full screen (tests).</summary>
public sealed class InMemoryWindowService : IWindowService
{
    public bool IsFullScreen { get; private set; }

    public event EventHandler? FullScreenChanged;

    public void SetFullScreen(bool fullScreen)
    {
        if (IsFullScreen != fullScreen)
        {
            IsFullScreen = fullScreen;
            FullScreenChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
