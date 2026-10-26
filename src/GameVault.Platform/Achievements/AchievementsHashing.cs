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

        // rc_hash_snes: a 512-byte copier header (size 512 over a multiple of 8 KB) is skipped.
        GameVault.Core.Systems.GameSystem.Snes when file.Length % 0x2000 == 512 => Convert.ToHexStringLower(MD5.HashData(file[512..])),

        // SMS, SG-1000, Apple II, Genesis and SNES: the whole file (rc_hash_buffer).
        _ => Convert.ToHexStringLower(MD5.HashData(file)),
    };

    public static string Nes(ReadOnlySpan<byte> file)
    {
        var headered = file.Length > 16 && (file.StartsWith("NES\u001A"u8) || file.StartsWith("FDS\u001A"u8));
        return Convert.ToHexStringLower(MD5.HashData(headered ? file[16..] : file));
    }
}
