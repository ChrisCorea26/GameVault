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

    public string CrashLogFile => Path.Combine(Root, "logs", "crash.log");
}
