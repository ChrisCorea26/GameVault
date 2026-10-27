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

    /// <summary>True when the game is set up for the Arkanoid paddle, so the mouse turns it (SPEC §4.7).</summary>
    bool UsesPaddle => false;

    /// <summary>True when the game is set up for the PlayStation's GunCon, so the mouse aims it.</summary>
    bool UsesGun => false;

    /// <summary>True when the game is set up for the PlayStation Mouse.</summary>
    bool UsesMouse => false;

    /// <summary>True when the game is set up for the Four Score (four players).</summary>
    bool UsesFourScore { get; }

    /// <summary>True while rewind is held (SPEC §7.2).</summary>
    bool IsRewinding { get; }

    /// <summary>How many seconds of rewind history are available.</summary>
    double RewindSeconds { get; }

    /// <summary>The scrub bar's length: the seconds of history there were when rewinding started.</summary>
    double RewindMaxSeconds => RewindSeconds;

    /// <summary>
    /// The scrub bar was grabbed (SPEC §7.2): the game holds still at the current point of the history, even once the
    /// rewind button is let go, until <see cref="EndScrub"/>.
    /// </summary>
    void BeginScrub()
    {
    }

    /// <summary>Shows the point of the history <paramref name="seconds"/> from its start (either direction).</summary>
    void ScrubTo(double seconds)
    {
    }

    /// <summary>The scrub bar was let go: play continues from the chosen point, and the newer history is dropped.</summary>
    void EndScrub()
    {
    }

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

    /// <summary>
    /// Hosts over the internet: through the relay in Settings when there is one, or directly, in which case the joiner
    /// sends back a reply code for <see cref="AcceptNetplayReplyAsync"/>.
    /// </summary>
    Task<string> HostInternetNetplayAsync() => Task.FromException<string>(new NetplayException("Netplay isn't available here."));

    /// <summary>The internet host types the joiner's reply code.</summary>
    Task AcceptNetplayReplyAsync(string replyCode) => Task.FromException(new NetplayException("Netplay isn't available here."));

    /// <summary>Hosting over the internet directly: waiting for the joiner's reply code.</summary>
    bool NetplayNeedsReply => false;

    /// <summary>The joiner's reply code for an internet host, to send back to them.</summary>
    string? NetplayReplyCode => null;

    /// <summary>Joins a game with the host's invite code (on this network, over the internet or through a relay). The same game must be running here.</summary>
    Task JoinNetplayAsync(string inviteCode) => Task.FromException(new NetplayException("Netplay isn't available here."));

    /// <summary>Ends netplay; the game keeps running here without saving to the game's save file.</summary>
    void LeaveNetplay()
    {
    }

    /// <summary>The running game's system (SPEC §7.11); null when idle.</summary>
    GameVault.Core.Systems.GameSystem? System => null;

    /// <summary>The names of the running game's memory-map values, for building challenge goals.</summary>
    IReadOnlyList<string> MemoryValueNames => [];

    /// <summary>True while a challenge attempt is running (SPEC §7.9).</summary>
    bool IsChallengeActive => false;

    /// <summary>
    /// A challenge starting from this moment of the running game. The goal's named values come from the
    /// game's memory map. Throws <see cref="ChallengeException"/> with a user-facing message.
    /// </summary>
    Task<GameVault.Library.Challenges.ChallengeFile> CreateChallengeAsync(
        string name, IReadOnlyList<GameVault.Library.Memory.MemoryCondition> goal, int? timeLimitFrames, string? note) =>
        Task.FromException<GameVault.Library.Challenges.ChallengeFile>(new ChallengeException("Challenges aren't available here."));

    /// <summary>Loads the challenge's start state and records the attempt until the goal holds or time runs out.</summary>
    Task StartChallengeAsync(GameVault.Library.Challenges.ChallengeFile challenge) =>
        Task.FromException(new ChallengeException("Challenges aren't available here."));

    void CancelChallenge()
    {
    }

    /// <summary>Raised on the UI thread when an attempt ends.</summary>
    event EventHandler<ChallengeOutcome>? ChallengeFinished
    {
        add { }
        remove { }
    }

    /// <summary>Replays a result on a fresh console and says whether it really reaches the goal.</summary>
    Task<ChallengeCheck> VerifyChallengeAsync(GameVault.Library.Challenges.ChallengeFile challenge, GameVault.Library.Challenges.ChallengeResult result) =>
        Task.FromResult(new ChallengeCheck(false, "Checking results isn't available here."));

    /// <summary>True when the running game has a memory map, so stat overlays, maps and death markers work (SPEC §7.9).</summary>
    bool HasMemoryMap => false;

    /// <summary>The stat overlay's values for the running game ("Health", "3 of 16").</summary>
    IReadOnlyList<StatValue> StatValues => [];

    /// <summary>Raised on the UI thread when <see cref="StatValues"/> change (a few times a second at most).</summary>
    event EventHandler? StatValuesChanged
    {
        add { }
        remove { }
    }

    /// <summary>True while any Game Genie or RAM code is on (SPEC §7.8).</summary>
    bool CheatsActive => false;

    /// <summary>Replaces the running game's active codes; applied between frames.</summary>
    void SetCheats(IReadOnlyList<GameVault.Core.Cheats.CheatCode> codes)
    {
    }

    /// <summary>Disk sides of a Famicom Disk System game, or discs of a multi-disc PlayStation game (0 for others).</summary>
    int DiskSideCount => 0;

    /// <summary>The discs of a multi-disc PlayStation game (.m3u), by name; empty for other games.</summary>
    IReadOnlyList<string> DiscNames => [];

    /// <summary>The disk side in the drive, or null while it is empty (or for cartridges).</summary>
    int? InsertedDiskSide => null;

    /// <summary>Raised on the UI thread when a disk side is inserted or ejected (including by F4).</summary>
    event EventHandler? DiskChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Ejects the current side and inserts <paramref name="side"/> after a short pause, like a real drive (for a
    /// PlayStation, changes to disc <paramref name="side"/>).
    /// </summary>
    void InsertDiskSide(int side)
    {
    }

    void EjectDisk()
    {
    }

    /// <summary>The disk in each Apple II drive (its name), or null for an empty drive; empty for other systems.</summary>
    IReadOnlyList<string?> AppleDriveNames => [];

    /// <summary>Reads a disk image and puts it in Apple II drive <paramref name="drive"/> (0 or 1).</summary>
    Task InsertAppleDiskAsync(int drive, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    void EjectAppleDisk(int drive)
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

/// <summary>How a challenge attempt ended.</summary>
public enum ChallengeOutcomeKind
{
    Done,
    TimeUp,
    GaveUp,
}

/// <summary>An attempt's end: with <see cref="Result"/> (ready to save and share) when the goal was reached.</summary>
public sealed record ChallengeOutcome(ChallengeOutcomeKind Kind, int Frames, GameVault.Library.Challenges.ChallengeResult? Result);

/// <summary>Whether a result checks out, in words for the player.</summary>
public sealed record ChallengeCheck(bool Verified, string Message);

/// <summary>A challenge action failed. <see cref="Exception.Message"/> is safe to show.</summary>
public sealed class ChallengeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One value of the stat overlay (SPEC §7.9): "Health" and "3 of 16".</summary>
public sealed record StatValue(string Label, string Text);

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
