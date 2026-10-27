using GameVault.ViewModels.Services.Input;

namespace GameVault.Platform.Input.Controllers;

/// <summary>Controller buttons by position (SDL GameController numbering with button labels off).</summary>
public enum PadButton
{
    South = 0,
    East = 1,
    West = 2,
    North = 3,
    Back = 4,
    Guide = 5,
    Start = 6,
    LeftStick = 7,
    RightStick = 8,
    LeftShoulder = 9,
    RightShoulder = 10,
    DPadUp = 11,
    DPadDown = 12,
    DPadLeft = 13,
    DPadRight = 14,
}

public enum PadAxis
{
    LeftX = 0,
    LeftY = 1,
    RightX = 2,
    RightY = 3,
    TriggerLeft = 4,
    TriggerRight = 5,
}

/// <summary>UI navigation from a pad outside the game (docs/design/input.md §7).</summary>
public enum NavigationAction
{
    Up,
    Down,
    Left,
    Right,
    /// <summary>South (A / Cross).</summary>
    Activate,
    /// <summary>East (B / Circle).</summary>
    Back,
    /// <summary>Left shoulder: the previous main page.</summary>
    PreviousPage,
    /// <summary>Right shoulder: the next main page.</summary>
    NextPage,
    /// <summary>Left trigger: about a screen up.</summary>
    PageUp,
    /// <summary>Right trigger: about a screen down.</summary>
    PageDown,
    /// <summary>North (Y / Triangle): the focused control's context menu.</summary>
    ContextMenu,
    /// <summary>Start: the main menu.</summary>
    Menu,
}

/// <summary>Raw controller events, delivered on the backend's thread.</summary>
public interface IControllerEvents
{
    void OnConnected(ControllerInfo controller);

    void OnDisconnected(int instanceId);

    void OnButton(int instanceId, PadButton button, bool down);

    void OnAxis(int instanceId, PadAxis axis, short value);

    /// <summary>Called regularly (every few ms) with a monotonic time, for holds and key repeat.</summary>
    void OnTick(long nowMs);
}

/// <summary>Where controller events come from: SDL in the app, a fake in tests.</summary>
public interface IControllerBackend : IDisposable
{
    void Start(IControllerEvents events);

    /// <summary>Runs a pad's rumble motors at <paramref name="level"/> (0 stops them), where the pad has them.</summary>
    void SetRumble(int instanceId, float level)
    {
    }
}
