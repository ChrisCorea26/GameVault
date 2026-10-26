using System.Buffers.Binary;
using GameVault.Core.Nintendo64;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// A developer check of the RDP alone against PeterLemon's RDP demos (public domain): the demo's command list is found
/// in its ROM image, run with the ROM copied to RDRAM (the demos' code and data sit at the same offsets in RDRAM as in
/// the ROM), and the 320×240 framebuffer is compared with the demo's reference picture. Runs only when
/// <c>GAMEVAULT_PETERLEMON_N64</c> points to a checkout of PeterLemon/N64; the full-console tests replace it.
/// </summary>
public sealed class N64RdpDemoTests(ITestOutputHelper output)
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("GAMEVAULT_PETERLEMON_N64");

    [Fact]
    public void Static_demos_are_compared_with_their_reference_pictures()
    {
        if (Root is null)
        {
            return;
        }

        var outDir = Environment.GetEnvironmentVariable("GAMEVAULT_PETERLEMON_N64_OUT");
        foreach (var rom in Directory.GetFiles(Path.Combine(Root, "RDP"), "*.N64", SearchOption.AllDirectories).Order())
        {
            var png = Path.ChangeExtension(rom, ".png");
            if (!File.Exists(png))
            {
                continue;
            }

            var image = File.ReadAllBytes(rom);
            if (!TryFindCommandList(image, out var start, out var end))
            {
                output.WriteLine($"{Path.GetFileName(rom)}: no static command list");
                continue;
            }

            var host = new Host();
            image.AsSpan(0, Math.Min(image.Length, host.Rdram.Length)).CopyTo(host.Rdram);
            var rdp = new N64Rdp(host);
            rdp.WriteRegister(N64Rdp.RegisterBase, start);
            rdp.WriteRegister(N64Rdp.RegisterBase + 4, end);

            var (fbAddress, fbSize, fbWidth) = ColorImage(image, start, end);
            var (width, height, rgb) = PngReader.Decode(File.ReadAllBytes(png));
            var ours = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    uint r, g, b;
                    if (fbSize == 2)
                    {
                        int c = BinaryPrimitives.ReadUInt16BigEndian(host.Rdram.AsSpan((int)fbAddress + (y * fbWidth + x) * 2));
                        r = (uint)((c >> 11) & 31) << 3;
                        g = (uint)((c >> 6) & 31) << 3;
                        b = (uint)((c >> 1) & 31) << 3;
                    }
                    else
                    {
                        var c = BinaryPrimitives.ReadUInt32BigEndian(host.Rdram.AsSpan((int)fbAddress + (y * fbWidth + x) * 4));
                        r = c >> 24;
                        g = (c >> 16) & 0xFF;
                        b = (c >> 8) & 0xFF;
                    }

                    ours[y * width + x] = 0xFF000000 | r << 16 | g << 8 | b;
                }
            }

            // Some reference pictures were captured a row or two lower; compare at the best vertical offset.
            var differences = int.MaxValue;
            var bestOffset = 0;
            var shift = fbSize == 2 ? 3 : 0;
            for (var dy = -2; dy <= 2; dy++)
            {
                var count = 0;
                for (var y = Math.Max(0, -dy); y < height && y + dy < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var c = ours[y * width + x];
                        var i = ((y + dy) * width + x) * 3;
                        if (((c >> 16) & 0xFF) >> shift != (uint)rgb[i] >> shift || ((c >> 8) & 0xFF) >> shift != (uint)rgb[i + 1] >> shift ||
                            (c & 0xFF) >> shift != (uint)rgb[i + 2] >> shift)
                        {
                            count++;
                        }
                    }
                }

                if (count < differences)
                {
                    differences = count;
                    bestOffset = dy;
                }
            }

            output.WriteLine($"{Path.GetRelativePath(Root, rom)}: {differences} pixels differ (reference {bestOffset} rows lower)");
            if (outDir is not null)
            {
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(rom) + ".png"), PngWriter.Encode(ours, width, height));
            }
        }
    }

    /// <summary>The command list starts with Set Scissor and ends with Sync Full; every command in between must be known.</summary>
    private static bool TryFindCommandList(byte[] rom, out uint start, out uint end)
    {
        for (var at = 0x1000; at + 8 <= rom.Length; at += 8)
        {
            if (rom[at] != 0x2D)
            {
                continue;
            }

            var p = at;
            while (p + 8 <= rom.Length && p - at < 0x40000)
            {
                var id = rom[p] & 0x3F;
                if (rom[p] >> 6 != 0 || !(id is 0 or (>= 8 and <= 0xF) or >= 0x24 and not 0x31))
                {
                    break;
                }

                p += 8 * Length(id);
                if (id == 0x29)
                {
                    start = (uint)at;
                    end = (uint)p;
                    return true;
                }
            }
        }

        start = end = 0;
        return false;
    }

    private static int Length(int id) => id switch
    {
        >= 8 and <= 0xF => 4 + ((id & 4) != 0 ? 8 : 0) + ((id & 2) != 0 ? 8 : 0) + ((id & 1) != 0 ? 2 : 0),
        0x24 or 0x25 => 2,
        _ => 1,
    };

    /// <summary>The last Set Color Image of the list: the picture the demo shows.</summary>
    private static (uint Address, int Size, int Width) ColorImage(byte[] rom, uint start, uint end)
    {
        var image = (0x100000u, 2, 320);
        for (var p = (int)start; p < end; p += 8 * Length(rom[p] & 0x3F))
        {
            if ((rom[p] & 0x3F) == 0x3F)
            {
                var w = BinaryPrimitives.ReadUInt64BigEndian(rom.AsSpan(p));
                image = ((uint)w & 0xFFFFFF, (int)(w >> 51) & 3, ((int)(w >> 32) & 0x3FF) + 1);
            }
        }

        return image;
    }

    private sealed class Host : IN64RcpHost
    {
        public byte[] Rdram { get; } = new byte[8 * 1024 * 1024];

        public byte[] DmemBytes { get; } = new byte[4096];

        Span<byte> IN64RcpHost.Rdram => Rdram;

        public Span<byte> Dmem => DmemBytes;

        public void RaiseInterrupt(N64Interrupt source)
        {
        }
    }
}
