using System.Security.Cryptography;

namespace GameVault.Platform.Achievements;

/// <summary>RetroAchievements' game hashes (rcheevos <c>rc_hash_nes</c>, <c>rc_hash_psx</c>, <c>rc_hash_ps2</c> and others).</summary>
public static class AchievementsHashing
{
    /// <summary>
    /// NES and Famicom Disk System: the MD5 of the file without its 16-byte iNES ("NES\x1A") or fwNES
    /// ("FDS\x1A") header, as lowercase hex.
    /// </summary>
    /// <summary>The hash RetroAchievements identifies the game by, for its system.</summary>
    public static string ForSystem(ReadOnlySpan<byte> file, GameVault.Core.Systems.GameSystem system) => system switch
    {
        GameVault.Core.Systems.GameSystem.Nes => Nes(file),

        // rc_hash_psx for discs; a PS-EXE file hashes whole.
        GameVault.Core.Systems.GameSystem.PlayStation when GameVault.Library.Discs.PlayStationGames.TryGetPath(file, out var disc) => PlayStationDisc(disc),

        // rc_hash_ps2 for discs; an ELF hashes whole (rc_hash_buffer).
        GameVault.Core.Systems.GameSystem.PlayStation2 when GameVault.Library.Discs.Ps2Discs.TryGetPath(file, out var ps2Disc) => PlayStation2Disc(ps2Disc),

        // rc_hash_snes: a 512-byte copier header (size 512 over a multiple of 8 KB) is skipped.
        GameVault.Core.Systems.GameSystem.Snes when file.Length % 0x2000 == 512 => Convert.ToHexStringLower(MD5.HashData(file[512..])),

        // rc_hash_n64: the ROM turned big-endian (.z64) first.
        GameVault.Core.Systems.GameSystem.Nintendo64 => Convert.ToHexStringLower(MD5.HashData(GameVault.Core.Nintendo64.N64Cartridge.ToBigEndian(file))),

        // SMS, SG-1000, Apple II, Genesis, SNES and the Atari 2600: the whole file (rc_hash_buffer).
        _ => Convert.ToHexStringLower(MD5.HashData(file)),
    };

    /// <summary>
    /// rcheevos' <c>rc_hash_psx</c>: the MD5 of the boot executable's name (from SYSTEM.CNF's BOOT line, without
    /// "cdrom:", leading backslashes and the ";1" version; PSX.EXE when there's no SYSTEM.CNF) followed by the
    /// executable's first bytes: its PS-EXE header's size field plus the 2 KB header. Empty when the disc has none.
    /// </summary>
    public static string PlayStationDisc(string path)
    {
        var disc = GameVault.Library.Discs.DiscImages.Open(GameVault.Library.Discs.PlayStationGames.DiscPaths(path)[0]);
        try
        {
            return PlayStationDisc(disc);
        }
        finally
        {
            (disc as IDisposable)?.Dispose();
        }
    }

    public static string PlayStationDisc(GameVault.Core.PlayStation.ICdImage disc)
    {
        if (GameVault.Core.PlayStation.PsDisc.BootPath(disc) is not { } boot)
        {
            return string.Empty;
        }

        var name = GameVault.Core.PlayStation.PsDisc.FileName(boot);
        var file = GameVault.Core.PlayStation.PsDisc.ReadFile(disc, name);
        if (file is null)
        {
            return string.Empty;
        }

        if (GameVault.Core.PlayStation.PsExe.IsPsExe(file))
        {
            var length = (int)Math.Min(int.MaxValue, BitConverter.ToUInt32(file, 28) + 2048L);
            file = GameVault.Core.PlayStation.PsDisc.ReadFile(disc, name, length) ?? file;
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(System.Text.Encoding.ASCII.GetBytes(name));
        md5.AppendData(file);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>RetroAchievements' console ids (rcheevos <c>rc_consoles.h</c>) for the systems GameVault runs.</summary>
    public static int ConsoleId(GameVault.Core.Systems.GameSystem system) => system switch
    {
        GameVault.Core.Systems.GameSystem.Nes => 7,
        GameVault.Core.Systems.GameSystem.MasterSystem => 11,
        GameVault.Core.Systems.GameSystem.Sg1000 => 33,
        GameVault.Core.Systems.GameSystem.AppleII => 38,
        GameVault.Core.Systems.GameSystem.Genesis => 1,
        GameVault.Core.Systems.GameSystem.Snes => 3,
        GameVault.Core.Systems.GameSystem.PlayStation => 12,
        GameVault.Core.Systems.GameSystem.Nintendo64 => 2,
        GameVault.Core.Systems.GameSystem.PlayStation2 => 21,
        GameVault.Core.Systems.GameSystem.Atari2600 => 25,
        _ => 0,
    };

    /// <summary>
    /// The last address RetroAchievements reads on the PlayStation 2. Its memory (rcheevos
    /// <c>rc_memory_regions_playstation2</c>) is the EE's 32 MB RDRAM at 0 (kernel RAM below $100000), then the 16 KB
    /// scratchpad (the EE's $70000000) at <see cref="PlayStation2ScratchpadAddress"/>.
    /// </summary>
    public const uint PlayStation2MemoryLimit = 0x0200_3FFF;

    /// <summary>Where RetroAchievements sees the PS2's scratchpad, after the 32 MB RDRAM.</summary>
    public const uint PlayStation2ScratchpadAddress = 0x0200_0000;

    /// <summary>
    /// rcheevos' <c>rc_hash_ps2</c>: the MD5 of the boot executable's name followed by its contents (at most 64 MB). The
    /// name is SYSTEM.CNF's BOOT2 value after "cdrom0:" and any backslashes, up to whitespace or the ";1" version (such
    /// as <c>SLUS_200.62</c>). Empty when the disc has no BOOT2 line or the file is missing.
    /// </summary>
    public static string PlayStation2Disc(string path)
    {
        using var disc = GameVault.Library.Discs.Ps2Discs.Open(GameVault.Library.Discs.Ps2Discs.DiscPaths(path)[0]);
        return PlayStation2Disc(disc);
    }

    public static string PlayStation2Disc(GameVault.Core.PlayStation2.IPs2Disc disc)
    {
        // rcheevos reads only SYSTEM.CNF's first sector and matches "BOOT2" at the start of a line.
        if (GameVault.Library.Discs.Iso9660.FindFile(disc, "SYSTEM.CNF") is not { } cnf)
        {
            return string.Empty;
        }

        var sector = new byte[GameVault.Core.PlayStation2.IPs2Disc.SectorSize];
        disc.ReadSector(cnf.Lba, sector);
        var text = System.Text.Encoding.ASCII.GetString(sector, 0, (int)Math.Min(cnf.Size, sector.Length - 1)).Split('\0')[0];
        string? name = null;
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("BOOT2", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[5..].TrimStart();
            if (!value.StartsWith('='))
            {
                continue;
            }

            value = value[1..].TrimStart();
            if (value.StartsWith("cdrom0:", StringComparison.Ordinal))
            {
                value = value["cdrom0:".Length..];
            }

            value = value.TrimStart('\\');
            var end = value.IndexOfAny([' ', '\t', '\r', '\n', '\v', '\f', ';']);
            name = end >= 0 ? value[..end] : value;
            break;
        }

        if (string.IsNullOrEmpty(name) || GameVault.Library.Discs.Iso9660.ReadFile(disc, name, maxBytes: 64 * 1024 * 1024) is not { } file)
        {
            return string.Empty;
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(System.Text.Encoding.ASCII.GetBytes(name));
        md5.AppendData(file);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    public static string Nes(ReadOnlySpan<byte> file)
    {
        var headered = file.Length > 16 && (file.StartsWith("NES\u001A"u8) || file.StartsWith("FDS\u001A"u8));
        return Convert.ToHexStringLower(MD5.HashData(headered ? file[16..] : file));
    }
}
