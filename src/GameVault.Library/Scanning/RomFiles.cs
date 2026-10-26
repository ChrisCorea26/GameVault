using System.IO.Compression;
using GameVault.Core.Systems;

namespace GameVault.Library.Scanning;

/// <summary>
/// A game file's bytes and the system it's for. For PlayStation discs the bytes are a reference to the disc image
/// (<see cref="GameVault.Library.Discs.PlayStationGames"/>).
/// </summary>
public sealed record RomFile(byte[] Data, GameSystem System, string Extension = "");

/// <summary>The game file types GameVault plays, by system (SPEC §7.11).</summary>
public static class RomFiles
{
    /// <summary>Largest file read into memory.</summary>
    public const long MaxRomBytes = 16 * 1024 * 1024;

    /// <summary>Every extension GameVault opens directly (besides .zip).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".nes", ".fds", ".sms", ".sg", ".sc", ".dsk", ".do", ".po", ".nib", ".woz", ".md", ".gen", ".smd", ".bin", ".sfc", ".smc",
            ".cue", ".chd", ".m3u", ".exe"];

    /// <summary>The system a file is for, from its extension; null for other files (and .zip, whose content decides).</summary>
    public static GameSystem? SystemOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".nes" or ".fds" => GameSystem.Nes,
        ".sms" => GameSystem.MasterSystem,
        ".sg" or ".sc" => GameSystem.Sg1000,
        ".dsk" or ".do" or ".po" or ".nib" or ".woz" => GameSystem.AppleII,
        ".md" or ".gen" or ".smd" or ".bin" => GameSystem.Genesis, // a .bin counts only with a Genesis header (see Read)
        ".sfc" or ".smc" => GameSystem.Snes,
        ".cue" or ".chd" or ".m3u" or ".exe" => GameSystem.PlayStation, // an .exe counts only as a PS-EXE (see Read)
        _ => null,
    };

    /// <summary>PlayStation disc images, which are opened where they are rather than read into memory.</summary>
    public static bool IsDiscImage(string path) => GameVault.Library.Discs.DiscImages.IsDiscImage(path);

    public static bool IsGameFile(string path) => SystemOf(path) is not null;

    /// <summary>A game file, or the single game file inside a .zip. Null when it isn't one GameVault can play.</summary>
    public static RomFile? Read(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => IsGameFile(e.FullName) && !IsDiscImage(e.FullName)).ToList();
            if (entries.Count != 1 || entries[0].Length > MaxRomBytes)
            {
                return null;
            }

            using var stream = entries[0].Open();
            using var buffer = new MemoryStream((int)entries[0].Length);
            stream.CopyTo(buffer);
            return Checked(new RomFile(buffer.ToArray(), SystemOf(entries[0].FullName)!.Value, Path.GetExtension(entries[0].FullName).ToLowerInvariant()));
        }

        if (IsDiscImage(path))
        {
            // A disc stays on disk: its data is a reference the console opens (the .cue, .chd or .m3u must be readable).
            GameVault.Library.Discs.DiscImages.Files(path);
            return new RomFile(GameVault.Library.Discs.PlayStationGames.Reference(path), GameSystem.PlayStation, Path.GetExtension(path).ToLowerInvariant());
        }

        return SystemOf(path) is { } system && new FileInfo(path).Length <= MaxRomBytes
            ? Checked(new RomFile(File.ReadAllBytes(path), system, Path.GetExtension(path).ToLowerInvariant()))
            : null;
    }

    /// <summary>
    /// ".bin" is used by many systems: only a raw Genesis ROM (with "SEGA" in its header) is taken; a PlayStation
    /// track is reached through its .cue. An ".exe" must be a PS-EXE.
    /// </summary>
    private static RomFile? Checked(RomFile file) => file.Extension switch
    {
        ".bin" when !GameVault.Core.Genesis.GenesisCartridge.LooksLikeGenesis(file.Data) => null,
        ".exe" when !GameVault.Core.PlayStation.PsExe.IsPsExe(file.Data) => null,
        _ => file,
    };
}
