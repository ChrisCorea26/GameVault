namespace GameVault.ViewModels.Services;

/// <summary>User settings, persisted as JSON in <c>%LOCALAPPDATA%\GameVault\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int MaxRunAheadFrames = 3;

    /// <summary>Hide the top and bottom 8 lines, like most TVs did (SPEC §5.2).</summary>
    public bool CropOverscan { get; init; } = true;

    /// <summary>0–3 frames of run-ahead (SPEC §5.5). 1 by default.</summary>
    public int RunAheadFrames { get; init; } = 1;

    public bool PauseWhenInactive { get; init; } = true;

    /// <summary>Game volume, 0–100 (0 mutes).</summary>
    public int Volume { get; init; } = 80;

    /// <summary>
    /// Let the game see Left+Right or Up+Down at once. Off by default: the most recent direction on each
    /// axis wins, like a real pad (docs/design/input.md §6).
    /// </summary>
    public bool AllowOppositeDirections { get; init; }
}
