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

    /// <summary>Games whose stat overlay is on (SPEC §7.9), as game keys separated by '|'.</summary>
    public string StatOverlayGames { get; init; } = string.Empty;

    /// <summary>Start games in full screen (F11 or Alt+Enter switches while playing).</summary>
    public bool PlayFullScreen { get; init; }

    /// <summary>"Two players, one keyboard" preset (docs/design/input.md §4.3).</summary>
    public bool TwoPlayerKeyboard { get; init; }

    /// <summary>
    /// Frames per turbo press-and-release, for pads and keyboard turbo keys (input.md §6): 4 by default
    /// (15 presses per second).
    /// </summary>
    public int TurboPeriodFrames { get; init; } = 4;

    /// <summary>Pinned controllers as "key=port" entries separated by '|' (input.md §3.5).</summary>
    public string ControllerPins { get; init; } = string.Empty;

    /// <summary>Memory for rewind history in MB (SPEC §7.2: 256 MB by default).</summary>
    public int RewindMemoryMb { get; init; } = 256;

    /// <summary>Full path of the INL Retro-Prog program (inlretro.exe), or empty when no dumper is used (SPEC §8).</summary>
    public string DumperPath { get; init; } = string.Empty;

    /// <summary>
    /// Full path of the player's own Famicom Disk System BIOS (disksys.rom, 8 KB), or empty. GameVault never
    /// ships, downloads or links to it (SPEC §4.5).
    /// </summary>
    public string FdsBiosPath { get; init; } = string.Empty;

    /// <summary>
    /// The player's own Apple II system ROM (12 KB for the II/II+, 16 KB for the IIe), or empty. GameVault never ships,
    /// downloads or links to it (SPEC §7.11).
    /// </summary>
    public string AppleIIRomPath { get; init; } = string.Empty;

    /// <summary>The player's own Disk II boot ROM (256 bytes, slot 6), or empty.</summary>
    public string AppleIIDiskRomPath { get; init; } = string.Empty;

    /// <summary>
    /// The player's own PlayStation BIOS (512 KB, such as SCPH-1001), or empty. GameVault never ships, downloads or
    /// links to it (SPEC §7.11).
    /// </summary>
    public string PlayStationBiosPath { get; init; } = string.Empty;

    /// <summary>Skip the PlayStation BIOS's intro and start the disc's game directly. Off by default.</summary>
    public bool PlayStationFastBoot { get; init; }

    /// <summary>Game volume, 0–100 (0 mutes).</summary>
    public int Volume { get; init; } = 80;

    /// <summary>
    /// Let the game see Left+Right or Up+Down at once. Off by default: the most recent direction on each
    /// axis wins, like a real pad (docs/design/input.md §6).
    /// </summary>
    public bool AllowOppositeDirections { get; init; }

    /// <summary>
    /// RetroAchievements hardcore mode (SPEC §7.10): on by default, as RetroAchievements recommends. It turns off
    /// loading states, rewind, codes, slow motion, netplay and challenge attempts while signed in.
    /// </summary>
    public bool RetroAchievementsHardcore { get; init; } = true;
}
