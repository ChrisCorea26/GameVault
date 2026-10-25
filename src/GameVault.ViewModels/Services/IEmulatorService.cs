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

/// <summary>A game file could not be loaded. <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class GameLoadException(string message, Exception? inner = null) : Exception(message, inner);
