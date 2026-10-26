using System.Security.Cryptography;

namespace GameVault.Platform.Achievements;

/// <summary>RetroAchievements' game hashes (rcheevos <c>rc_hash_nes</c>).</summary>
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

        // rc_hash_snes: a 512-byte copier header (size 512 over a multiple of 8 KB) is skipped.
        GameVault.Core.Systems.GameSystem.Snes when file.Length % 0x2000 == 512 => Convert.ToHexStringLower(MD5.HashData(file[512..])),

        // rc_hash_n64: the ROM turned big-endian (.z64) first.
        GameVault.Core.Systems.GameSystem.Nintendo64 => Convert.ToHexStringLower(MD5.HashData(GameVault.Core.Nintendo64.N64Cartridge.ToBigEndian(file))),

        // SMS, SG-1000, Apple II, Genesis and SNES: the whole file (rc_hash_buffer).
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

    public static string Nes(ReadOnlySpan<byte> file)
    {
        var headered = file.Length > 16 && (file.StartsWith("NES\u001A"u8) || file.StartsWith("FDS\u001A"u8));
        return Convert.ToHexStringLower(MD5.HashData(headered ? file[16..] : file));
    }
}
