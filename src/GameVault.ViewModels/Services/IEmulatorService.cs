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
