using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;

namespace GameVault.Library.Discs;

/// <summary>
/// PlayStation 2 discs and programs (SPEC §7.11, Phase 17): opening the image formats as <see cref="IPs2Disc"/>,
/// telling PS2 discs from PlayStation discs, and the references the library and the emulator pass around. As for the
/// PlayStation, a disc game's data is a small reference holding its path; an ELF is passed as its bytes.
/// </summary>
public static class Ps2Discs
{
    private static ReadOnlySpan<byte> Marker => "GVPS2-DISC\n"u8;

    /// <summary>The data that stands for the PS2 disc (or .m3u playlist of discs) at <paramref name="path"/>.</summary>
    public static byte[] Reference(string path) => [.. Marker, .. Encoding.UTF8.GetBytes(Path.GetFullPath(path))];

    /// <summary>The path a reference holds; false for other data (an ELF).</summary>
    public static bool TryGetPath(ReadOnlySpan<byte> data, out string path)
    {
        if (data.StartsWith(Marker))
        {
            path = Encoding.UTF8.GetString(data[Marker.Length..]);
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>The discs of a game in order: the playlist's for an .m3u, else the one disc.</summary>
    public static IReadOnlyList<string> DiscPaths(string path) => PlayStationGames.DiscPaths(path);

    /// <summary>
    /// A PlayStation 2 program: a little-endian MIPS ELF (ELF magic, EI_DATA = 1, e_machine = 8), as ps2sdk builds them.
    /// </summary>
    public static bool IsPs2Elf(ReadOnlySpan<byte> data) =>
        data.Length >= 52 && data[..4].SequenceEqual("\u007FELF"u8) && data[5] == 1 && (data[18] | data[19] << 8) == 8;

    /// <summary>
    /// Opens one disc as the PS2's drive reads it: an .iso (2,048-byte sectors as a DVD, or raw 2,352-byte sectors as
    /// a CD), a raw .bin, a .cue/.bin or CD .chd (as a CD), or a DVD .chd. Dispose the result to close the files.
    /// </summary>
    /// <exception cref="InvalidDataException">The image is malformed, missing, or in a format GameVault doesn't read.</exception>
    public static IPs2DiscImage Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' doesn't exist.");
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".iso" or ".bin":
                using (var stream = DiscFiles.OpenRead(path))
                {
                    if (!IsRawImage(stream))
                    {
                        return Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                            ? throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't a raw disc image.")
                            : IsoPs2Disc.Open(path);
                    }
                }

                return new CdPs2Disc(IsoCdImage.Open(path));
            case ".chd" when ChdDvd.IsDvd(path):
                return ChdDvd.Open(path);
            case ".cue" or ".chd":
                return new CdPs2Disc(DiscImages.Open(path));
            case ".m3u":
                return Open(DiscPaths(path)[0]);
            default:
                throw new InvalidDataException($"'{Path.GetFileName(path)}' isn't a disc image (.iso, .cue, .chd).");
        }
    }

    /// <summary>
    /// True when an image holds raw 2,352-byte CD sectors (its size is a multiple of 2,352 and its first sector, or
    /// sector 16, starts with the sync pattern) rather than 2,048-byte user data.
    /// </summary>
    public static bool IsRawImage(FileStream stream)
    {
        if (stream.Length == 0 || stream.Length % ICdImage.SectorSize != 0)
        {
            return false;
        }

        Span<byte> sync = stackalloc byte[12];
        foreach (var sector in (ReadOnlySpan<long>)[0, 16])
        {
            if ((sector + 1) * ICdImage.SectorSize <= stream.Length)
            {
                DiscFiles.ReadAt(stream, sector * ICdImage.SectorSize, sync);
                if (sync.SequenceEqual(CdSector.Sync))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Where a dual-layer DVD's second layer starts, or 0 for a single layer. The first layer's primary volume
    /// descriptor records only that layer's size; on a dual-layer PS2 DVD the second layer begins right after it with
    /// its own volume descriptor at its sector 16 (the layout <c>sceCdReadDvdDualInfo</c> reports).
    /// </summary>
    public static long FindLayer1(IPs2Disc disc)
    {
        var volume = Iso9660.VolumeSpaceSize(disc);
        if (volume <= 16 || volume + 16 >= disc.SectorCount)
        {
            return 0;
        }

        Span<byte> sector = stackalloc byte[IPs2Disc.SectorSize];
        disc.ReadSector(volume + 16, sector);
        return Iso9660.IsVolumeDescriptor(sector, type: 1) ? volume : 0;
    }

    /// <summary>
    /// Which console a disc image is for: <see cref="GameSystem.PlayStation2"/> when its SYSTEM.CNF has BOOT2 (and for
    /// a DVD CHD); otherwise <see cref="GameSystem.PlayStation"/> for a CD .cue/.chd/.m3u (as before Phase 17), and for
    /// an .iso when SYSTEM.CNF has BOOT or the disc has PSX.EXE; null for an .iso that's neither.
    /// </summary>
    /// <exception cref="InvalidDataException">The image can't be read.</exception>
    public static GameSystem? SystemOf(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".m3u")
        {
            return SystemOf(DiscPaths(path)[0]) ?? GameSystem.PlayStation;
        }

        var disc = Open(path);
        try
        {
            var cnf = Ps2SystemCnf.Read(disc);
            if (cnf is { IsPlayStation2: true })
            {
                return GameSystem.PlayStation2;
            }

            if (extension is not (".iso" or ".bin"))
            {
                return disc.Type == Ps2DiscType.Cd ? GameSystem.PlayStation : GameSystem.PlayStation2;
            }

            return cnf?.Boot is not null || Iso9660.FindFile(disc, "PSX.EXE") is not null ? GameSystem.PlayStation : null;
        }
        finally
        {
            disc.Dispose();
        }
    }

    /// <summary>
    /// The hashes Redump lists for a disc: for a DVD, the whole image's 2,048-byte sectors; for a CD, track 1's raw
    /// sectors (as for the PlayStation). MD5 is included for DVDs, which Redump's PS2 DAT identifies by it too.
    /// </summary>
    public static (string Crc32, string Sha1, string? Md5) Hash(IPs2Disc disc)
    {
        if (disc.Cd is { } cd)
        {
            var (crc32, sha1) = PlayStationGames.HashFirstTrack(cd);
            return (crc32, sha1, null);
        }

        var crc = new Crc32();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        const int batch = 32;
        var buffer = new byte[IPs2Disc.SectorSize * batch];
        for (long lba = 0; lba < disc.SectorCount; lba += batch)
        {
            var count = (int)Math.Min(batch, disc.SectorCount - lba);
            for (var i = 0; i < count; i++)
            {
                disc.ReadSector(lba + i, buffer.AsSpan(i * IPs2Disc.SectorSize, IPs2Disc.SectorSize));
            }

            var span = buffer.AsSpan(0, count * IPs2Disc.SectorSize);
            crc.Append(span);
            sha.AppendData(span);
            md5.AppendData(span);
        }

        return (crc.GetCurrentHashAsUInt32().ToString("x8"), Convert.ToHexStringLower(sha.GetHashAndReset()), Convert.ToHexStringLower(md5.GetHashAndReset()));
    }

    /// <summary>Hashes the first disc of a reference's path (see <see cref="Hash(IPs2Disc)"/>).</summary>
    public static (string Crc32, string Sha1) HashReference(string path)
    {
        var disc = Open(DiscPaths(path)[0]);
        try
        {
            var (crc, sha1, _) = Hash(disc);
            return (crc, sha1);
        }
        finally
        {
            disc.Dispose();
        }
    }
}
