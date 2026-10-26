using System.IO.Compression;
using GameVault.Core.Systems;

namespace GameVault.Library.Scanning;

/// <summary>A game file's bytes and the system it's for.</summary>
public sealed record RomFile(byte[] Data, GameSystem System, string Extension = "");

/// <summary>The game file types GameVault plays, by system (SPEC §7.11).</summary>
public static class RomFiles
{
    /// <summary>Largest file read into memory.</summary>
    public const long MaxRomBytes = 16 * 1024 * 1024;

    /// <summary>Every extension GameVault opens directly (besides .zip).</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".nes", ".fds", ".sms", ".sg", ".sc", ".dsk", ".do", ".po", ".nib", ".woz"];

    /// <summary>The system a file is for, from its extension; null for other files (and .zip, whose content decides).</summary>
    public static GameSystem? SystemOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".nes" or ".fds" => GameSystem.Nes,
        ".sms" => GameSystem.MasterSystem,
        ".sg" or ".sc" => GameSystem.Sg1000,
        ".dsk" or ".do" or ".po" or ".nib" or ".woz" => GameSystem.AppleII,
        _ => null,
    };

    public static bool IsGameFile(string path) => SystemOf(path) is not null;

    /// <summary>A game file, or the single game file inside a .zip. Null when it isn't one GameVault can play.</summary>
    public static RomFile? Read(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => IsGameFile(e.FullName)).ToList();
            if (entries.Count != 1 || entries[0].Length > MaxRomBytes)
            {
                return null;
            }

            using var stream = entries[0].Open();
            using var buffer = new MemoryStream((int)entries[0].Length);
            stream.CopyTo(buffer);
            return new RomFile(buffer.ToArray(), SystemOf(entries[0].FullName)!.Value, Path.GetExtension(entries[0].FullName).ToLowerInvariant());
        }

        return SystemOf(path) is { } system && new FileInfo(path).Length <= MaxRomBytes
            ? new RomFile(File.ReadAllBytes(path), system, Path.GetExtension(path).ToLowerInvariant())
            : null;
    }
}
