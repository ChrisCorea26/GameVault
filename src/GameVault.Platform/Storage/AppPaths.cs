namespace GameVault.Platform.Storage;

/// <summary>
/// Where GameVault keeps its data. The app is unpackaged, so it uses <c>%LOCALAPPDATA%\GameVault\</c>
/// instead of <c>ApplicationData</c>, which requires package identity (SPEC §9.1).
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameVault"))
    {
    }

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Battery saves (<c>.sav</c>). Never inside the user's ROM folders unless they opt in (SPEC §4.5).</summary>
    public string SavesDirectory => Path.Combine(Root, "Saves");

    public string StatesDirectory => Path.Combine(Root, "States");

    public string LibraryDatabase => Path.Combine(Root, "library.db");

    /// <summary>Captured passwords (SPEC §7.3).</summary>
    public string PasswordsDirectory => Path.Combine(Root, "Passwords");

    /// <summary>Game journals: notes, screenshots, maps (SPEC §7.4).</summary>
    public string JournalDirectory => Path.Combine(Root, "Journal");

    /// <summary>The player's Game Genie and RAM codes, one file per game (SPEC §7.8).</summary>
    public string CheatsDirectory => Path.Combine(Root, "Cheats");

    /// <summary>Code collections the player imported (Settings → Codes).</summary>
    public string CodeCollectionsDirectory => Path.Combine(Root, "Cheats", "Collections");

    /// <summary>Pages fetched from GameGenie.com when the player asked for a game's codes.</summary>
    public string OnlineCodesCacheDirectory => Path.Combine(Root, "Cheats", "GameGenie.com");

    /// <summary>Recent play sessions, for the "Previously on…" recap (SPEC §7.9).</summary>
    public string SessionsDirectory => Path.Combine(Root, "Sessions");

    /// <summary>Rooms visited, for automatic maps (SPEC §7.9).</summary>
    public string MapsDirectory => Path.Combine(Root, "Maps");

    /// <summary>Practice personal bests (SPEC §7.6).</summary>
    public string PracticeDirectory => Path.Combine(Root, "Practice");

    /// <summary>
    /// BIOS files the player chose in Settings (Choose…), copied here into one folder per system
    /// (<c>bios\fds</c>, <c>bios\ps1</c>, <c>bios\ps2</c>). GameVault never includes a BIOS.
    /// </summary>
    public string BiosDirectory => Path.Combine(Root, "bios");

    public string CrashLogFile => Path.Combine(Root, "logs", "crash.log");
}
