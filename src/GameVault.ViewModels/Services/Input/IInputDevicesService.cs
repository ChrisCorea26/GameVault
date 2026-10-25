namespace GameVault.ViewModels.Services.Input;

/// <summary>An input source on a port: the keyboard or one game controller.</summary>
public sealed record InputSourceInfo(string Id, string Name, bool IsKeyboard, bool IsPinned);

/// <summary>One console port and what drives it. <paramref name="WaitingFor"/> names a disconnected pad during play.</summary>
public sealed record InputPortInfo(int Port, IReadOnlyList<InputSourceInfo> Sources, string? WaitingFor);

/// <summary>Raised when a pad driving a port disconnects during play (docs/design/input.md §5).</summary>
public sealed class ControllerDisconnectedEventArgs(int port, string name) : EventArgs
{
    public int Port { get; } = port;

    public string Name { get; } = name;
}

/// <summary>
/// Controllers, ports and the keyboard (docs/design/input.md §9). Events are raised on the thread that
/// created the service (the UI thread in the app).
/// </summary>
public interface IInputDevicesService
{
    public const string KeyboardId = "keyboard";

    IReadOnlyList<InputPortInfo> Ports { get; }

    IReadOnlyList<InputSourceInfo> Unassigned { get; }

    /// <summary>"Two players, one keyboard" (input.md §4.3).</summary>
    bool TwoPlayerKeyboard { get; set; }

    /// <summary>Frames per turbo press-and-release (2-8), for pads and keyboard turbo keys (input.md §6).</summary>
    int TurboPeriodFrames { get; set; }

    /// <summary>Ports or devices changed.</summary>
    event EventHandler? Changed;

    /// <summary>A short message to show, such as "8BitDo SN30 Pro is now Player 2".</summary>
    event EventHandler<string>? Notice;

    event EventHandler<ControllerDisconnectedEventArgs>? ControllerDisconnected;

    /// <summary>Tells the service whether a game is running (disconnects only reserve ports during play).</summary>
    void SetGameRunning(bool running);

    /// <summary>2 normally; 4 while a Four Score game runs, so players 3 and 4 get pads.</summary>
    void SetPlayerCount(int count);

    void SwapPorts(int a, int b);

    void AssignToPort(string sourceId, int port);

    void SetPinned(string sourceId, bool pinned);
}
