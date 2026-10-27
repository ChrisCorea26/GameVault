namespace GameVault.ViewModels.Services;

/// <summary>What a "Browse…" button asks the picker for.</summary>
/// <param name="FileTypes">Extensions to show, such as ".exe"; "*" (or an empty list) shows every file.</param>
/// <param name="CommitButtonText">The picker's OK button.</param>
public sealed record FilePathRequest(IReadOnlyList<string> FileTypes, string CommitButtonText = "Choose")
{
    /// <summary>Any file (BIOS and ROM dumps come as .bin, .rom, .rom0 or no extension at all).</summary>
    public static FilePathRequest AnyFile(string commitButtonText = "Choose") => new(["*"], commitButtonText);
}

/// <summary>
/// The "Browse…" button next to a box that takes a file path (Settings: BIOS files, ROMs, the dumper program).
/// Typing or pasting a path still works.
/// </summary>
public interface IFilePathPicker
{
    /// <summary>Asks the user for a file. Returns its full path, or null if they cancel.</summary>
    Task<string?> PickFileAsync(FilePathRequest request);
}
