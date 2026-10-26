using System.IO.Hashing;
using System.Security.Cryptography;
using GameVault.Core.Systems;

namespace GameVault.Library.Hashing;

/// <summary>Headerless hashes of a ROM: what catalogs (No-Intro) list and what identifies a game.</summary>
public readonly record struct RomHashes(string Crc32, string Sha1)
{
    /// <summary>The key a game is stored under: "sha1:…".</summary>
    public string Key => "sha1:" + Sha1;
}

/// <summary>
/// Hashes a ROM image without its 16-byte iNES header (and 512-byte trainer, when present), as SPEC §2
/// requires. Famicom Disk System images drop their 16-byte fwNES header, as No-Intro's FDS DAT does. Files
/// without a header are hashed whole.
/// </summary>
public static class RomHasher
{
    public static RomHashes Hash(ReadOnlySpan<byte> image) => Hash(image, GameSystem.Nes);

    /// <summary>
    /// Hashes for <paramref name="system"/>. Sega cartridges have no header, but copier dumps can carry a 512-byte
    /// one, which No-Intro leaves out too. A PlayStation disc reference hashes the disc's first track. N64 ROMs are
    /// hashed big-endian (.z64), as No-Intro lists them, whatever byte order the file uses.
    /// </summary>
    public static RomHashes Hash(ReadOnlySpan<byte> image, GameSystem system)
    {
        if (system == GameSystem.PlayStation && GameVault.Library.Discs.PlayStationGames.TryGetPath(image, out var disc))
        {
            // A disc: its first track, as Redump lists it.
            var (discCrc, discSha1) = GameVault.Library.Discs.PlayStationGames.HashReference(disc);
            return new RomHashes(discCrc, discSha1);
        }

        var body = system switch
        {
            GameSystem.Nes => StripHeader(image),
            GameSystem.Sg1000 or GameSystem.MasterSystem when image.Length % 0x4000 == 512 => image[512..],
            GameSystem.Genesis when GameVault.Core.Genesis.GenesisCartridge.IsInterleaved(image) =>
                GameVault.Core.Genesis.GenesisCartridge.Deinterleave(image), // .smd: No-Intro hashes the plain ROM
            GameSystem.Snes when image.Length % 1024 == 512 => image[512..], // a copier header
            GameSystem.Nintendo64 => GameVault.Core.Nintendo64.N64Cartridge.ToBigEndian(image),
            _ => image,
        };
        var crc = Crc32.HashToUInt32(body);
        var sha1 = SHA1.HashData(body);
        return new RomHashes(crc.ToString("x8"), Convert.ToHexStringLower(sha1));
    }

    public static ReadOnlySpan<byte> StripHeader(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 16 && image[0] == 'N' && image[1] == 'E' && image[2] == 'S' && image[3] == 0x1A)
        {
            var skip = 16 + ((image[6] & 0x04) != 0 ? 512 : 0);
            return image.Length > skip ? image[skip..] : [];
        }

        if (image.Length >= 16 && image[0] == 'F' && image[1] == 'D' && image[2] == 'S' && image[3] == 0x1A)
        {
            return image[16..];
        }

        return image;
    }
}
