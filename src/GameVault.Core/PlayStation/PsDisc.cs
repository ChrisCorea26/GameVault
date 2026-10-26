using System.Text;

namespace GameVault.Core.PlayStation;

/// <summary>
/// Reads a PlayStation disc's ISO-9660 file system: finds SYSTEM.CNF, the boot executable it names and the licence
/// region (psx-spx: "CDROM File Formats"). Used for fast boot, game identification and RetroAchievements.
/// </summary>
public static class PsDisc
{
    /// <summary>The boot executable's path as SYSTEM.CNF names it (such as <c>cdrom:\SLUS_007.12;1</c>), or <c>PSX.EXE</c> when there's no SYSTEM.CNF.</summary>
    public static string? BootPath(ICdImage disc)
    {
        var cnf = ReadFile(disc, "SYSTEM.CNF");
        if (cnf is null)
        {
            return FindFile(disc, "PSX.EXE") is not null ? @"cdrom:\PSX.EXE;1" : null;
        }

        foreach (var line in Encoding.ASCII.GetString(cnf).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("BOOT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals < 0 || trimmed[..equals].Trim().Length != 4)
            {
                continue;
            }

            return trimmed[(equals + 1)..].Trim().Split(' ', '\t')[0];
        }

        return null;
    }

    /// <summary>The boot executable's bytes, or null when the disc has none.</summary>
    public static byte[]? BootExecutable(ICdImage disc)
    {
        var path = BootPath(disc);
        return path is null ? null : ReadFile(disc, FileName(path));
    }

    /// <summary>The path without the device prefix, leading backslashes and the ";1" version: <c>SLUS_007.12</c>.</summary>
    public static string FileName(string bootPath)
    {
        var name = bootPath;
        var colon = name.IndexOf(':');
        if (colon >= 0)
        {
            name = name[(colon + 1)..];
        }

        name = name.TrimStart('\\', '/');
        var semicolon = name.IndexOf(';');
        return semicolon >= 0 ? name[..semicolon] : name;
    }

    /// <summary>
    /// Reads a file by path (directories separated by backslashes), case-insensitively; null when missing.
    /// <paramref name="length"/> reads that many bytes from the file's first sector instead of the file's size.
    /// </summary>
    public static byte[]? ReadFile(ICdImage disc, string path, int? length = null)
    {
        if (FindFile(disc, path) is not { } found)
        {
            return null;
        }

        var entry = length is { } count ? (found.Lba, Size: count) : found;
        var data = new byte[entry.Size];
        var sector = new byte[ICdImage.SectorSize];
        for (var offset = 0; offset < entry.Size; offset += 2048)
        {
            disc.ReadSector(entry.Lba + offset / 2048, sector);
            var start = sector[15] == 1 ? 16 : 24;
            Array.Copy(sector, start, data, offset, Math.Min(2048, entry.Size - offset));
        }

        return data;
    }

    private static (int Lba, int Size)? FindFile(ICdImage disc, string path)
    {
        var sector = new byte[ICdImage.SectorSize];
        var first = disc.Tracks[0].Start;
        disc.ReadSector(first + 16, sector);
        var data = sector[15] == 1 ? 16 : 24;
        if (Encoding.ASCII.GetString(sector, data + 1, 5) != "CD001")
        {
            return null;
        }

        // The root directory's record sits at offset 156 of the primary volume descriptor.
        var lba = BitConverter.ToInt32(sector, data + 156 + 2) + first;
        var size = BitConverter.ToInt32(sector, data + 156 + 10);
        var parts = path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        for (var p = 0; p < parts.Length; p++)
        {
            var found = false;
            for (var offset = 0; offset < size && !found; offset += 2048)
            {
                disc.ReadSector(lba + offset / 2048, sector);
                var start = sector[15] == 1 ? 16 : 24;
                for (var i = 0; i < 2048;)
                {
                    var length = sector[start + i];
                    if (length == 0)
                    {
                        break;
                    }

                    var nameLength = sector[start + i + 32];
                    var name = Encoding.ASCII.GetString(sector, start + i + 33, nameLength);
                    var semicolon = name.IndexOf(';');
                    if (semicolon >= 0)
                    {
                        name = name[..semicolon];
                    }

                    if (string.Equals(name, parts[p], StringComparison.OrdinalIgnoreCase))
                    {
                        lba = BitConverter.ToInt32(sector, start + i + 2) + first;
                        size = BitConverter.ToInt32(sector, start + i + 10);
                        found = true;
                        break;
                    }

                    i += length;
                }
            }

            if (!found)
            {
                return null;
            }
        }

        return (lba, size);
    }
}
