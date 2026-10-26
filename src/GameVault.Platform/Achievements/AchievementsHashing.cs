using System.Security.Cryptography;

namespace GameVault.Platform.Achievements;

/// <summary>RetroAchievements' game hashes (rcheevos <c>rc_hash_nes</c>).</summary>
public static class AchievementsHashing
{
    /// <summary>
    /// NES and Famicom Disk System: the MD5 of the file without its 16-byte iNES ("NES\x1A") or fwNES
    /// ("FDS\x1A") header, as lowercase hex.
    /// </summary>
    public static string Nes(ReadOnlySpan<byte> file)
    {
        var headered = file.Length > 16 && (file.StartsWith("NES\u001A"u8) || file.StartsWith("FDS\u001A"u8));
        return Convert.ToHexStringLower(MD5.HashData(headered ? file[16..] : file));
    }
}
