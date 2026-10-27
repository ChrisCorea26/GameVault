namespace GameVault.ViewModels.Services;

/// <summary>User settings, persisted as JSON in <c>%LOCALAPPDATA%\GameVault\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int MaxRunAheadFrames = 3;

    /// <summary>Hide the top and bottom of the picture, like most TVs did (SPEC §5.2). How much is <see cref="OverscanCropLines"/>.</summary>
    public bool CropOverscan { get; init; } = true;

    /// <summary>The largest crop Settings → Video offers, in lines at the top and at the bottom.</summary>
    public const int MaxOverscanCropLines = 16;

    /// <summary>
    /// Lines hidden at the top and at the bottom while <see cref="CropOverscan"/> is on: 8 by default, like most TVs
    /// (SPEC §5.2); Settings → Video offers 4 to 16.
    /// </summary>
    public int OverscanCropLines { get; init; } = 8;

    /// <summary>The lines to hide at the top and at the bottom: 0 when cropping is off.</summary>
    public int CropLines() => CropOverscan ? Math.Clamp(OverscanCropLines, 0, MaxOverscanCropLines) : 0;

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

    /// <summary>True once the bundled homebrew folder (SPEC §6.4) was added to the library, so removing it sticks.</summary>
    public bool BundledHomebrewAdded { get; init; }

    /// <summary>The player's button remaps (SPEC §5.4, GV-001) as JSON; see <c>Input.InputRemapSet</c>. Empty for none.</summary>
    public string InputRemaps { get; init; } = string.Empty;

    /// <summary>Pinned controllers as "key=port" entries separated by '|' (input.md §3.5).</summary>
    public string ControllerPins { get; init; } = string.Empty;

    /// <summary>
    /// Keep battery saves next to the game file ("Game.sav" beside "Game.nes") instead of in GameVault's Saves folder
    /// (SPEC §4.5: only when the player opts in). Off by default. A save already in the Saves folder is still found.
    /// </summary>
    public bool SavesNextToRom { get; init; }

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

    /// <summary>
    /// Give N64 games the Expansion Pak's extra 4 MB (8 MB in all). On by default, as most homebrew and many games
    /// use it; a few older games expect 4 MB.
    /// </summary>
    public bool N64ExpansionPak { get; init; } = true;

    /// <summary>
    /// The player's own PlayStation 2 BIOS (4 MB, such as SCPH-39001), or empty. Discs need it; ELF programs run on
    /// GameVault's own kernel without it. GameVault never ships, downloads or links to it (SPEC §7.11).
    /// </summary>
    public string PlayStation2BiosPath { get; init; } = string.Empty;

    /// <summary>Skip the PlayStation 2 BIOS's startup screens and start the disc's game directly. Off by default.</summary>
    public bool PlayStation2FastBoot { get; init; }

    /// <summary>
    /// The folder holding the player's own Super Nintendo enhancement chip program ROMs (dsp1b.rom and the others), or
    /// empty. GameVault never ships, downloads or links to them (SPEC §7.11).
    /// </summary>
    public string SnesChipRomFolder { get; init; } = string.Empty;

    /// <summary>Game volume, 0–100 (0 mutes).</summary>
    public int Volume { get; init; } = 80;

    /// <summary>Play both stereo channels on both speakers (for one speaker or one ear). Off by default.</summary>
    public bool MonoAudio { get; init; }

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
