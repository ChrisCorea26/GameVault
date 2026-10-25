namespace GameVault.ViewModels.Services;

public enum EmulatorStatus
{
    Idle,
    Running,
    Paused,
    Faulted,
}

/// <summary>
/// Runs a game on its own thread. Commands are queued and take effect between frames (SPEC §5.1).
/// <see cref="StatusChanged"/> is raised on the thread that created the service (the UI thread in the app).
/// </summary>
public interface IEmulatorService
{
    EmulatorStatus Status { get; }

    /// <summary>File name of the loaded game, without its extension.</summary>
    string? GameTitle { get; }

    /// <summary>The running game's library key ("sha1:…", headerless ROM hash); null when idle.</summary>
    string? GameKey { get; }

    /// <summary>True when the game is set up for the Zapper, so the mouse aims (SPEC §7.5).</summary>
    bool UsesZapper { get; }

    /// <summary>True when the game is set up for the Four Score (four players).</summary>
    bool UsesFourScore { get; }

    /// <summary>True while rewind is held (SPEC §7.2).</summary>
    bool IsRewinding { get; }

    /// <summary>How many seconds of rewind history are available.</summary>
    double RewindSeconds { get; }

    /// <summary>Raised on the UI thread when rewinding starts, moves, or stops.</summary>
    event EventHandler? RewindChanged;

    /// <summary>Time actually played (running, not paused), reported every minute and on pause or stop.</summary>
    event EventHandler<TimeSpan>? PlaytimeElapsed;

    /// <summary>Frames emulated since the game started (practice timer, SPEC §7.6).</summary>
    long FrameCount { get; }

    /// <summary>F9: note a password by hand (SPEC §7.3).</summary>
    event EventHandler? ManualPasswordRequested;

    /// <summary>F12: save a screenshot to the journal (SPEC §7.4).</summary>
    event EventHandler? ScreenshotRequested;

    /// <summary>F6 / F7: practice retry and split (SPEC §7.6).</summary>
    event EventHandler? PracticeRetryRequested;

    event EventHandler? PracticeSplitRequested;

    /// <summary>Assist mode for this session (SPEC §7.6). Applied between frames.</summary>
    void SetAssist(AssistOptions options);

    /// <summary>A save state of the running game (practice points). Null when idle.</summary>
    Task<byte[]?> CaptureStateAsync();

    Task<bool> RestoreStateAsync(byte[] state);

    /// <summary>The current screen as a PNG. Null when idle.</summary>
    Task<byte[]?> CaptureScreenshotAsync();

    /// <summary>Two-player netplay on the local network (SPEC §7.7). Off for a normal game.</summary>
    NetplayStatus NetplayStatus => NetplayStatus.Off;

    /// <summary>The code the other player types to join, while hosting.</summary>
    string? NetplayInviteCode => null;

    /// <summary>Raised on the UI thread when <see cref="NetplayStatus"/> or the invite code changes.</summary>
    event EventHandler? NetplayChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Hosts the running game for a second player on this network and returns the invite code. The game
    /// restarts from power-on when they join. Throws <see cref="NetplayException"/> with a user-facing message.
    /// </summary>
    Task<string> HostNetplayAsync() => Task.FromException<string>(new NetplayException("Netplay isn't available here."));

    /// <summary>Joins a game hosted on this network. The same game must be running here.</summary>
    Task JoinNetplayAsync(string inviteCode) => Task.FromException(new NetplayException("Netplay isn't available here."));

    /// <summary>Ends netplay; the game keeps running here without saving to the game's save file.</summary>
    void LeaveNetplay()
    {
    }

    /// <summary>True while any Game Genie or RAM code is on (SPEC §7.8).</summary>
    bool CheatsActive => false;

    /// <summary>Replaces the running game's active codes; applied between frames.</summary>
    void SetCheats(IReadOnlyList<GameVault.Core.Cheats.CheatCode> codes)
    {
    }

    /// <summary>Disk sides of a Famicom Disk System game (0 for cartridges).</summary>
    int DiskSideCount => 0;

    /// <summary>The disk side in the drive, or null while it is empty (or for cartridges).</summary>
    int? InsertedDiskSide => null;

    /// <summary>Raised on the UI thread when a disk side is inserted or ejected (including by F4).</summary>
    event EventHandler? DiskChanged
    {
        add { }
        remove { }
    }

    /// <summary>Ejects the current side and inserts <paramref name="side"/> after a short pause, like a real drive.</summary>
    void InsertDiskSide(int side)
    {
    }

    void EjectDisk()
    {
    }

    /// <summary>Plain-language reason for <see cref="EmulatorStatus.Faulted"/>.</summary>
    string? FaultMessage { get; }

    event EventHandler? StatusChanged;

    /// <summary>
    /// Something the player should know that doesn't stop the game, such as run-ahead switching itself off.
    /// Raised on the UI thread.
    /// </summary>
    event EventHandler<string>? Notice;

    /// <summary>Loads a game file and starts it. Throws <see cref="GameLoadException"/> with a user-facing message.</summary>
    Task LoadAsync(string path, CancellationToken cancellationToken = default);

    void Pause();

    void Resume();

    void Reset();

    /// <summary>Stops and unloads the game.</summary>
    void Stop();
}

/// <summary>Where a netplay session stands (SPEC §7.7).</summary>
public enum NetplayStatus
{
    Off,

    /// <summary>Hosting: the invite code is shown and nobody has joined yet.</summary>
    WaitingForPlayer,

    /// <summary>Joining: waiting for the host to answer.</summary>
    Connecting,

    Playing,

    /// <summary>The other player has fallen behind; the game waits for them.</summary>
    Waiting,

    /// <summary>Nothing has arrived from the other player for a few seconds.</summary>
    ConnectionLost,

    /// <summary>The two games stopped matching; the session has stopped.</summary>
    OutOfSync,

    /// <summary>The other player is running a different game (or version of it).</summary>
    DifferentGame,
}

/// <summary>A netplay action failed. <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class NetplayException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A game file could not be loaded. <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class GameLoadException(string message, Exception? inner = null) : Exception(message, inner);
