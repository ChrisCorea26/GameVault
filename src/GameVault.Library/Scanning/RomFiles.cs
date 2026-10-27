using System.IO.Compression;
using GameVault.Core.Systems;

namespace GameVault.Library.Scanning;

/// <summary>
/// A game file's bytes and the system it's for. For PlayStation and PlayStation 2 discs the bytes are a reference to the
/// disc image (<see cref="GameVault.Library.Discs.PlayStationGames"/>, <see cref="GameVault.Library.Discs.Ps2Discs"/>).
/// </summary>
public sealed record RomFile(byte[] Data, GameSystem System, string Extension = "");

/// <summary>The game file types GameVault plays, by system (SPEC §7.11).</summary>
public static class RomFiles
{
    /// <summary>Largest file read into memory: the biggest N64 cartridges are 64 MB.</summary>
    public const long MaxRomBytes = 64 * 1024 * 1024;

    /// <summary>Every extension GameVault opens directly (besides .zip).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".nes", ".fds", ".sms", ".sg", ".sc", ".dsk", ".do", ".po", ".nib", ".woz", ".md", ".gen", ".smd", ".bin", ".sfc", ".smc",
            ".cue", ".chd", ".m3u", ".exe", ".z64", ".v64", ".n64", ".iso", ".elf", ".a26"];

    /// <summary>
    /// The system a file is for, from its extension; null for other files (and .zip, whose content decides). Disc images
    /// are told apart by their content when they're read (<see cref="Read"/>): a .cue, .chd or .m3u may be a PlayStation 2
    /// disc, a .cue or .chd a Sega CD disc, and an .iso a PlayStation disc.
    /// </summary>
    public static GameSystem? SystemOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".nes" or ".fds" => GameSystem.Nes,
        ".sms" => GameSystem.MasterSystem,
        ".sg" or ".sc" => GameSystem.Sg1000,
        ".dsk" or ".do" or ".po" or ".nib" or ".woz" => GameSystem.AppleII,
        ".md" or ".gen" or ".smd" or ".bin" => GameSystem.Genesis, // a .bin counts only with a Genesis header (see Read)
        ".sfc" or ".smc" => GameSystem.Snes,
        ".cue" or ".chd" or ".m3u" or ".exe" => GameSystem.PlayStation, // an .exe counts only as a PS-EXE (see Read)
        ".z64" or ".v64" or ".n64" => GameSystem.Nintendo64,
        ".iso" or ".elf" => GameSystem.PlayStation2, // an .elf counts only as a PS2 ELF (see Read)
        ".a26" => GameSystem.Atari2600,
        _ => null,
    };

    /// <summary>PlayStation and PlayStation 2 disc images, which are opened where they are rather than read into memory.</summary>
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
            // A disc stays on disk: its data is a reference the console opens (the .cue, .chd, .iso or .m3u must be
            // readable). SYSTEM.CNF tells a PlayStation 2 disc (BOOT2) from a PlayStation disc.
            GameVault.Library.Discs.DiscImages.Files(path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (GameVault.Library.Discs.SegaCdDiscs.IsSegaCd(path))
            {
                return new RomFile(GameVault.Library.Discs.SegaCdDiscs.Reference(path), GameSystem.SegaCd, extension);
            }

            return GameVault.Library.Discs.Ps2Discs.SystemOf(path) switch
            {
                GameSystem.PlayStation2 => new RomFile(GameVault.Library.Discs.Ps2Discs.Reference(path), GameSystem.PlayStation2, extension),
                GameSystem.PlayStation => new RomFile(GameVault.Library.Discs.PlayStationGames.Reference(path), GameSystem.PlayStation, extension),
                _ => null,
            };
        }

        return SystemOf(path) is { } system && new FileInfo(path).Length <= MaxRomBytes
            ? Checked(new RomFile(File.ReadAllBytes(path), system, Path.GetExtension(path).ToLowerInvariant()))
            : null;
    }

    /// <summary>
    /// ".bin" is used by many systems: only a raw Genesis ROM (with "SEGA" in its header) is taken; a PlayStation
    /// track is reached through its .cue. An ".exe" must be a PS-EXE, and an ".elf" a PlayStation 2 ELF.
    /// </summary>
    private static RomFile? Checked(RomFile file) => file.Extension switch
    {
        ".bin" when !GameVault.Core.Genesis.GenesisCartridge.LooksLikeGenesis(file.Data) => null,
        ".exe" when !GameVault.Core.PlayStation.PsExe.IsPsExe(file.Data) => null,
        ".elf" when !GameVault.Library.Discs.Ps2Discs.IsPs2Elf(file.Data) => null,
        ".z64" or ".v64" or ".n64" when !GameVault.Core.Nintendo64.N64Cartridge.IsN64Rom(file.Data) => null,
        _ => file,
    };
}
