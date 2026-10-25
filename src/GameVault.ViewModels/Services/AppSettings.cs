namespace GameVault.ViewModels.Services;

/// <summary>User settings, persisted as JSON in <c>%LOCALAPPDATA%\GameVault\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int MaxRunAheadFrames = 3;

    /// <summary>Hide the top and bottom 8 lines, like most TVs did (SPEC §5.2).</summary>
    public bool CropOverscan { get; init; } = true;

    /// <summary>Picture style (SPEC §5.2).</summary>
    public DisplayPreset DisplayPreset { get; init; } = DisplayPreset.SharpPixels;

    /// <summary>Stretch pixels to the 8:7 shape they had on a TV, instead of square.</summary>
    public bool TvPixelShape { get; init; }

    /// <summary>0–3 frames of run-ahead (SPEC §5.5). 1 by default.</summary>
    public int RunAheadFrames { get; init; } = 1;

    public bool PauseWhenInactive { get; init; } = true;

    /// <summary>"Two players, one keyboard" preset (docs/design/input.md §4.3).</summary>
    public bool TwoPlayerKeyboard { get; init; }

    /// <summary>Pinned controllers as "key=port" entries separated by '|' (input.md §3.5).</summary>
    public string ControllerPins { get; init; } = string.Empty;

    /// <summary>Game volume, 0–100 (0 mutes).</summary>
    public int Volume { get; init; } = 80;

    /// <summary>
    /// Let the game see Left+Right or Up+Down at once. Off by default: the most recent direction on each
    /// axis wins, like a real pad (docs/design/input.md §6).
    /// </summary>
    public bool AllowOppositeDirections { get; init; }
}
