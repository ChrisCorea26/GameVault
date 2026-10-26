using System.Text;
using GameVault.Core.PlayStation2;

namespace GameVault.Library.Discs;

/// <summary>The video mode a disc's SYSTEM.CNF asks for (its VMODE line).</summary>
public enum Ps2VideoMode
{
    Ntsc,
    Pal,
}

/// <summary>
/// A disc's SYSTEM.CNF (ps2tek and psx-spx: "SYSTEM.CNF"): <c>BOOT2 = cdrom0:\SLUS_200.62;1</c> on PlayStation 2
/// discs (<c>BOOT = cdrom:\…</c> on PlayStation discs), then <c>VER</c> and <c>VMODE = NTSC</c> or <c>PAL</c>. A disc is a
/// PlayStation 2 disc when it has a BOOT2 line.
/// </summary>
/// <param name="Boot2">The BOOT2 path as written (such as <c>cdrom0:\SLUS_200.62;1</c>); null on PlayStation discs.</param>
/// <param name="Boot">The PlayStation's BOOT path, when there's one.</param>
/// <param name="Version">The VER value (such as <c>1.00</c>).</param>
/// <param name="VideoMode">The VMODE value, when it's NTSC or PAL.</param>
public sealed record Ps2SystemCnf(string? Boot2, string? Boot, string? Version, Ps2VideoMode? VideoMode)
{
    public bool IsPlayStation2 => Boot2 is not null;

    /// <summary>
    /// The boot executable's name: the BOOT2 path without <c>cdrom0:</c>, leading backslashes and the ";1" version, as
    /// rcheevos' <c>rc_hash_ps2</c> takes it (<c>SLUS_200.62</c>); null without BOOT2.
    /// </summary>
    public string? BootFile => Boot2 is null ? null : Iso9660.Normalize(Boot2);

    /// <summary>The disc's serial, the boot executable's file name (<c>SLUS_200.62</c>); null without BOOT2.</summary>
    public string? Serial => BootFile is { } file ? file[(file.LastIndexOf('\\') + 1)..] : null;

    /// <summary>Parses SYSTEM.CNF's text (KEY = value lines; keys compare case-insensitively).</summary>
    public static Ps2SystemCnf Parse(string text)
    {
        string? boot2 = null, boot = null, version = null;
        Ps2VideoMode? mode = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\0');
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Equals("BOOT2", StringComparison.OrdinalIgnoreCase))
            {
                boot2 ??= value;
            }
            else if (key.Equals("BOOT", StringComparison.OrdinalIgnoreCase))
            {
                // The path ends at whitespace: PlayStation discs may pass arguments after it.
                boot ??= value.Split(' ', '\t')[0];
            }
            else if (key.Equals("VER", StringComparison.OrdinalIgnoreCase))
            {
                version ??= value;
            }
            else if (key.Equals("VMODE", StringComparison.OrdinalIgnoreCase))
            {
                mode ??= value.ToUpperInvariant() switch
                {
                    "NTSC" => Ps2VideoMode.Ntsc,
                    "PAL" => Ps2VideoMode.Pal,
                    _ => null,
                };
            }
        }

        return new Ps2SystemCnf(boot2, boot, version, mode);
    }

    /// <summary>Reads and parses the disc's SYSTEM.CNF from its root directory; null when there's none.</summary>
    public static Ps2SystemCnf? Read(IPs2Disc disc) =>
        Iso9660.ReadFile(disc, "SYSTEM.CNF", maxBytes: 64 * 1024) is { } data ? Parse(Encoding.ASCII.GetString(data)) : null;
}
