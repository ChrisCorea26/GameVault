using System.Text;

namespace GameVault.Library.Bios;

/// <summary>A PlayStation 2 BIOS's version as its ROMVER file records it (such as <c>0160EC20020207</c>).</summary>
/// <param name="Version">The version, such as "1.60".</param>
/// <param name="Region">The region the letter stands for (Japan, USA, Europe, Asia, China, Russia), or the letter.</param>
/// <param name="Date">The build date, such as "2002-02-07".</param>
public sealed record Ps2BiosVersion(string Version, string Region, string Date)
{
    public override string ToString() => $"version {Version}, {Region}, {Date}";
}

/// <summary>What Settings shows about the chosen PlayStation 2 BIOS.</summary>
/// <param name="IsValid">True for a readable 4 MB file.</param>
/// <param name="Message">A sentence for the player: the version found, or what's wrong.</param>
public sealed record Ps2BiosCheck(bool IsValid, string Message, Ps2BiosVersion? Version = null);

/// <summary>
/// The player's own PlayStation 2 BIOS (GameVault never ships it): 4 MB, a ROMDIR file system whose ROMVER file
/// holds the version (ps2tek: "BIOS File Structure").
/// </summary>
public static class Ps2Bios
{
    public const int Size = 4 * 1024 * 1024;

    /// <summary>
    /// The version in ROMVER: ROMDIR starts at the first "RESET" entry; each 16-byte entry is a name (10 bytes), the
    /// size of its extended info (2) and the file's size (4); files follow one another from the start of the BIOS,
    /// each padded to 16 bytes. ROMVER is "VVVVRTYYYYMMDD" (version, region, console type, date). Null when it's not there.
    /// </summary>
    public static Ps2BiosVersion? ReadVersion(ReadOnlySpan<byte> bios)
    {
        var start = bios.IndexOf("RESET\0"u8);
        if (start < 0)
        {
            return null;
        }

        long offset = 0;
        for (var entry = start; entry + 16 <= bios.Length; entry += 16)
        {
            var name = bios.Slice(entry, 10);
            if (name[0] == 0)
            {
                break;
            }

            var size = BitConverter.ToUInt32(bios.Slice(entry + 12, 4));
            if (name.StartsWith("ROMVER\0"u8))
            {
                if (offset + 14 > bios.Length || size < 14)
                {
                    return null;
                }

                var text = Encoding.ASCII.GetString(bios.Slice((int)offset, 14));
                if (!text.All(c => char.IsAsciiLetterOrDigit(c)))
                {
                    return null;
                }

                var region = text[4] switch
                {
                    'J' => "Japan",
                    'A' => "USA",
                    'E' => "Europe",
                    'H' => "Asia",
                    'C' => "China",
                    'R' => "Russia",
                    var other => other.ToString(),
                };
                return new Ps2BiosVersion($"{text[0..2].TrimStart('0').PadLeft(1, '0')}.{text[2..4]}", region, $"{text[6..10]}-{text[10..12]}-{text[12..14]}");
            }

            offset += (size + 15) & ~15L;
        }

        return null;
    }

    /// <summary>Checks the BIOS at <paramref name="path"/> (empty: nothing chosen yet).</summary>
    public static Ps2BiosCheck Check(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
        {
            return new Ps2BiosCheck(false, "Discs need your own PlayStation 2 BIOS. ELF programs run without one.");
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new Ps2BiosCheck(false, "GameVault can't find this file.");
            }

            if (info.Length != Size)
            {
                return new Ps2BiosCheck(false, "This file isn't a PlayStation 2 BIOS: a BIOS is 4 MB.");
            }

            var version = ReadVersion(File.ReadAllBytes(path));
            return version is null
                ? new Ps2BiosCheck(true, "A 4 MB BIOS file (its version couldn't be read).")
                : new Ps2BiosCheck(true, $"PlayStation 2 BIOS {version}.", version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Ps2BiosCheck(false, $"GameVault can't read this file: {ex.Message}");
        }
    }
}
