using System.Buffers.Binary;
using System.Text;
using GameVault.Core.PlayStation2;

namespace GameVault.Library.Discs;

/// <summary>
/// Reads files from a disc's ISO 9660 file system through its 2,048-byte sectors (ECMA-119): the primary volume
/// descriptor at sector 16, the root directory record at its offset 156, then directory records by name. Used to find
/// SYSTEM.CNF and the boot executable on PlayStation 2 discs (and PlayStation discs read through
/// <see cref="CdPs2Disc"/>). Names compare case-insensitively without their ";1" version.
/// </summary>
public static class Iso9660
{
    private const int SectorSize = IPs2Disc.SectorSize;

    /// <summary>True when sector 16 holds a primary volume descriptor ("CD001").</summary>
    public static bool HasVolume(IPs2Disc disc)
    {
        Span<byte> sector = stackalloc byte[SectorSize];
        disc.ReadSector(16, sector);
        return IsVolumeDescriptor(sector, type: 1);
    }

    /// <summary>The volume space size (sectors) the primary volume descriptor at <paramref name="lba"/> records; 0 without one.</summary>
    public static long VolumeSpaceSize(IPs2Disc disc, long lba = 16)
    {
        Span<byte> sector = stackalloc byte[SectorSize];
        disc.ReadSector(lba, sector);
        return IsVolumeDescriptor(sector, type: 1) ? BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]) : 0;
    }

    internal static bool IsVolumeDescriptor(ReadOnlySpan<byte> sector, int type) => sector[0] == type && sector[1..6].SequenceEqual("CD001"u8);

    /// <summary>
    /// Finds a file by path (directories separated by backslashes or slashes; a device prefix such as
    /// <c>cdrom0:</c> and a ";1" version are ignored): its first sector and size in bytes, or null when missing.
    /// </summary>
    public static (long Lba, long Size)? FindFile(IPs2Disc disc, string path)
    {
        var sector = new byte[SectorSize];
        disc.ReadSector(16, sector);
        if (!IsVolumeDescriptor(sector, type: 1))
        {
            return null;
        }

        // The root directory's record sits at offset 156 of the primary volume descriptor.
        long lba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 2));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(156 + 10));
        var parts = Normalize(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        foreach (var part in parts)
        {
            (long Lba, long Size)? found = null;
            for (long offset = 0; offset < size && found is null; offset += SectorSize)
            {
                disc.ReadSector(lba + offset / SectorSize, sector);
                for (var i = 0; i < SectorSize;)
                {
                    var length = sector[i];
                    if (length == 0 || i + 33 > SectorSize)
                    {
                        break; // records don't cross sectors: the rest of this one is padding
                    }

                    var nameLength = Math.Min(sector[i + 32], SectorSize - i - 33);
                    var name = Encoding.ASCII.GetString(sector, i + 33, nameLength);
                    var semicolon = name.IndexOf(';');
                    if (semicolon >= 0)
                    {
                        name = name[..semicolon];
                    }

                    if (string.Equals(name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        found = (BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i + 2)), BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i + 10)));
                        break;
                    }

                    i += length;
                }
            }

            if (found is not { } entry)
            {
                return null;
            }

            (lba, size) = entry;
        }

        return (lba, size);
    }

    /// <summary>A file's bytes (at most <paramref name="maxBytes"/> of them), or null when it's missing.</summary>
    public static byte[]? ReadFile(IPs2Disc disc, string path, long maxBytes = int.MaxValue)
    {
        if (FindFile(disc, path) is not { } file)
        {
            return null;
        }

        var length = (int)Math.Min(Math.Min(file.Size, maxBytes), Array.MaxLength);
        var data = new byte[length];
        var sector = new byte[SectorSize];
        for (var offset = 0; offset < length; offset += SectorSize)
        {
            disc.ReadSector(file.Lba + offset / SectorSize, sector);
            sector.AsSpan(0, Math.Min(SectorSize, length - offset)).CopyTo(data.AsSpan(offset));
        }

        return data;
    }

    /// <summary>The path without its device prefix (<c>cdrom0:</c>), leading separators or ";1" version, with backslashes.</summary>
    public static string Normalize(string path)
    {
        var name = path.Trim();
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            name = name[(colon + 1)..];
        }

        var semicolon = name.IndexOf(';');
        if (semicolon >= 0)
        {
            name = name[..semicolon];
        }

        return name.Replace('/', '\\').TrimStart('\\');
    }
}
