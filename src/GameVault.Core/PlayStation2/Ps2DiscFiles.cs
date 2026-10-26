using System.Buffers.Binary;
using System.Text;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// Reads files from a PlayStation 2 disc's ISO 9660 file system through <see cref="IPs2Disc"/> (ECMA-119: the primary
/// volume descriptor at sector 16, the root directory record at its offset 156, then directory records by name), and
/// its SYSTEM.CNF's <c>BOOT2</c> line. The machine uses it to fast-boot a disc (load its boot executable when the BIOS
/// starts its EE loader). Names compare case-insensitively without their ";1" version; no file I/O.
/// </summary>
public static class Ps2DiscFiles
{
    private const int SectorSize = IPs2Disc.SectorSize;

    /// <summary>
    /// Finds a file by path (directories separated by backslashes or slashes; a device prefix such as <c>cdrom0:</c> and a
    /// ";1" version are ignored): its first sector and size in bytes, or null when it's missing or there's no volume.
    /// </summary>
    public static (long Lba, long Size)? FindFile(IPs2Disc disc, string path)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(path);
        var sector = new byte[SectorSize];
        disc.ReadSector(16, sector);
        if (sector[0] != 1 || !sector.AsSpan(1, 5).SequenceEqual("CD001"u8))
        {
            return null;
        }

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
                disc.ReadSector(lba + (offset / SectorSize), sector);
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

    /// <summary>A file's bytes, or null when it's missing or larger than <paramref name="maxBytes"/>.</summary>
    public static byte[]? ReadFile(IPs2Disc disc, string path, int maxBytes = 32 * 1024 * 1024)
    {
        if (FindFile(disc, path) is not { } file || file.Size > maxBytes)
        {
            return null;
        }

        var data = new byte[file.Size];
        var sector = new byte[SectorSize];
        for (var offset = 0; offset < data.Length; offset += SectorSize)
        {
            disc.ReadSector(file.Lba + (offset / SectorSize), sector);
            sector.AsSpan(0, Math.Min(SectorSize, data.Length - offset)).CopyTo(data.AsSpan(offset));
        }

        return data;
    }

    /// <summary>
    /// The boot executable SYSTEM.CNF names on its <c>BOOT2</c> line, as written (such as <c>cdrom0:\SLUS_200.62;1</c>);
    /// null without SYSTEM.CNF or a BOOT2 line (a PlayStation disc's <c>BOOT</c> doesn't count).
    /// </summary>
    public static string? ReadBoot2(IPs2Disc disc)
    {
        if (ReadFile(disc, "SYSTEM.CNF", 64 * 1024) is not { } data)
        {
            return null;
        }

        foreach (var raw in Encoding.ASCII.GetString(data).Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\0');
            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(equals + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
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
