using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// The GPU's rasterizer against VRAM dumps from a real console, from ps1-tests (Jakub Czekański, MIT; downloaded at
/// test time). Each test sends the same GP0 commands as the ps1-tests program and compares the region it draws,
/// pixel for pixel at 5 bits per channel: polygon coverage and seams, Gouraud and texture interpolation, dithering
/// and lines.
/// </summary>
public sealed class Ps1TestsGpuTests
{
    private readonly PsGpu _gpu;

    public Ps1TestsGpuTests()
    {
        _gpu = new PlayStationConsole().Gpu;
        _gpu.Power();
        _gpu.WriteGp0(0xE3000000);
        _gpu.WriteGp0(0xE4000000 | 511u << 10 | 1023); // the whole of VRAM, as the SDK's ResetGraph leaves it
        _gpu.WriteGp0(0xE5000000);
    }

    private static uint Xy(int x, int y) => (uint)((y & 0x7FF) << 16 | (x & 0x7FF));

    /// <summary>ps1-tests' clearScreenColor: four fills covering VRAM.</summary>
    private void Clear(uint color)
    {
        foreach (var (x, y, w, h) in new[] { (0, 0, 512, 256), (512, 0, 512, 256), (0, 256, 512, 256), (512, 256, 0x3F1, 256) })
        {
            _gpu.WriteGp0(0x02000000 | color);
            _gpu.WriteGp0(Xy(x, y));
            _gpu.WriteGp0((uint)(h << 16 | w));
        }
    }

    [TestRomFact("ps1-tests/gpu/triangle/vram.png")]
    public void Gouraud_triangles_match_the_hardware_including_dithering()
    {
        Clear(0xFFFFFF);
        _gpu.WriteGp0(0xE1000400);
        Triangle(160, 120, 240);
        Triangle(768, 256, 500);
        _gpu.WriteGp0(0xE1000600); // dithering on
        Triangle(160, 360, 240);

        AssertMatches("ps1-tests/gpu/triangle/vram.png", 0, 0, 1024, 512);
    }

    /// <summary>The program's equilateral triangle: red, green and blue corners.</summary>
    private void Triangle(int cx, int cy, int a)
    {
        var h = a * 0.866025f;
        var bottom = (int)(cy + h / 2);
        var top = (int)(cy - h / 2);
        _gpu.WriteGp0(0x300000FF);
        _gpu.WriteGp0(Xy(cx - a / 2, bottom));
        _gpu.WriteGp0(0x0000FF00);
        _gpu.WriteGp0(Xy(cx + a / 2, bottom));
        _gpu.WriteGp0(0x00FF0000);
        _gpu.WriteGp0(Xy(cx, top));
    }

    [TestRomFact("ps1-tests/gpu/quad/vram.png")]
    public void Semi_transparent_quads_meet_without_seams()
    {
        Clear(0xFFFFFF);
        int[] x = [48, 176, 64, 208], y = [48, 32, 144, 160];
        Quad(0x000000, x[0], y[0], x[1], y[1], x[2], y[2], x[3], y[3]);
        Quad(0x0000FF, 0, 0, x[0], y[0], 0, 240, x[2], y[2]);
        Quad(0x00FF00, 0, 0, 320, 0, x[0], 48, x[1], y[1]);
        Quad(0xFF0000, x[1], y[1], 320, 0, x[3], y[3], 320, 240);
        Quad(0xFF00FF, x[2], y[2], x[3], y[3], 0, 240, 320, 240);
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                const int S = 16;
                var c = row == 0 ? 0xA0u : 0xFFu;
                var color = c * (uint)(column & 1) | (c * (uint)((column >> 1) & 1)) << 8 | (c * (uint)((column >> 2) & 1)) << 16;
                int left = 64 + S * column, top = 192 + S * row;
                Quad(color, left, top, left + S, top, left, top + S, left + S, top + S);
            }
        }

        AssertMatches("ps1-tests/gpu/quad/vram.png", 0, 0, 320, 240);
    }

    private void Quad(uint color, params int[] xy)
    {
        _gpu.WriteGp0(0x2A000000 | color);
        for (var i = 0; i < 8; i += 2)
        {
            _gpu.WriteGp0(Xy(xy[i], xy[i + 1]));
        }
    }

    [TestRomFact("ps1-tests/gpu/lines/vram.png")]
    public void Lines_match_the_hardware()
    {
        Clear(0xFFFFFF);
        _gpu.WriteGp0(0xE1000400);
        for (var i = 0; i < 10; i++)
        {
            Line(0, 16, 16 + i * 8, null, 96, 16 + i * 8 + i);
            Line(0, 110 + i * 8, 16, null, 110 + i * 8 + i, 96);
        }

        for (var i = 0; i < 20; i++)
        {
            Line(0, 200, 16 + i * 4, null, 200 + i, 16 + i * 4 + 1);
        }

        FlatLines(16, 100);
        GouraudLines(16, 166);
        _gpu.WriteGp0(0xE1000600); // dithering on: it applies to flat lines too
        FlatLines(84, 100);
        GouraudLines(84, 166);
        Polyline(150, 100, transparent: false);
        Polyline(210, 100, transparent: true);

        // The program's Gouraud polylines (y ≥ 136) use a colour it never sets, and its circle needs the SDK's
        // sine table, so only the rest is compared.
        AssertMatches("ps1-tests/gpu/lines/vram.png", 0, 0, 320, 98);
        AssertMatches("ps1-tests/gpu/lines/vram.png", 0, 98, 148, 240);
        AssertMatches("ps1-tests/gpu/lines/vram.png", 148, 98, 300, 136);
    }

    private void Line(uint color0, int x0, int y0, uint? color1, int x1, int y1)
    {
        if (color1 is { } gouraud)
        {
            _gpu.WriteGp0(0x50000000 | color0);
            _gpu.WriteGp0(Xy(x0, y0));
            _gpu.WriteGp0(gouraud);
        }
        else
        {
            _gpu.WriteGp0(0x40000000 | color0);
            _gpu.WriteGp0(Xy(x0, y0));
        }

        _gpu.WriteGp0(Xy(x1, y1));
    }

    private void FlatLines(int x, int y)
    {
        for (var i = 0; i < 64; i++)
        {
            Line(0xAA, x, y + i, null, x + i, y + i);
        }
    }

    private void GouraudLines(int x, int y)
    {
        for (var i = 0; i < 64; i++)
        {
            Line(0, x, y + i, 0xFF, x + i, y + i);
        }
    }

    private void Polyline(int x, int y, bool transparent)
    {
        _gpu.WriteGp0((transparent ? 0x4A000000u : 0x48000000u) | 0xAA);
        _gpu.WriteGp0(Xy(x, y));
        _gpu.WriteGp0(Xy(x + 32, y));
        _gpu.WriteGp0(Xy(x + 32, y + 32));
        _gpu.WriteGp0(Xy(x, y));
        _gpu.WriteGp0(0x55555555);
    }

    [TestRomFact("ps1-tests/gpu/uv-interpolation/vram.png")]
    public void Texture_and_colour_interpolation_match_the_hardware()
    {
        Clear(0);
        _gpu.WriteGp0(0xE1000400);
        _gpu.WriteGp0(0xA0000000); // a red and a green texel at (512, 0)
        _gpu.WriteGp0(Xy(512, 0));
        _gpu.WriteGp0(0x00010002);
        _gpu.WriteGp0(0x03E0001F);

        for (var i = 0; i < 256; i++)
        {
            // A 15-bit textured quad i pixels wide and 1 high, with U from 0 to 1 (page at x = 512).
            _gpu.WriteGp0(0x2C808080);
            _gpu.WriteGp0(Xy(0, i));
            _gpu.WriteGp0(0x00000000);
            _gpu.WriteGp0(Xy(i, i));
            _gpu.WriteGp0(0x01080001);
            _gpu.WriteGp0(Xy(0, i + 1));
            _gpu.WriteGp0(0x00000000);
            _gpu.WriteGp0(Xy(i, i + 1));
            _gpu.WriteGp0(0x00000001);
        }

        ColorStrips(0, 256);
        _gpu.WriteGp0(0xE1000600);
        ColorStrips(256, 256);

        AssertMatches("ps1-tests/gpu/uv-interpolation/vram.png", 0, 0, 1024, 512);
    }

    private void ColorStrips(int x, int y)
    {
        for (var i = 0; i < 256; i++)
        {
            _gpu.WriteGp0(0x380000FF);
            _gpu.WriteGp0(Xy(x, y + i));
            _gpu.WriteGp0(0x0000FF00);
            _gpu.WriteGp0(Xy(x + i, y + i));
            _gpu.WriteGp0(0x000000FF);
            _gpu.WriteGp0(Xy(x, y + i + 1));
            _gpu.WriteGp0(0x0000FF00);
            _gpu.WriteGp0(Xy(x + i, y + i + 1));
        }
    }

    /// <summary>Compares VRAM with the hardware dump in [x0, x1) × [y0, y1), at 5 bits per channel.</summary>
    private void AssertMatches(string reference, int x0, int y0, int x1, int y1)
    {
        var (width, _, rgb) = PngReader.Decode(TestRomLocator.ReadCached(reference));
        var mismatches = new List<string>();
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = (y * width + x) * 3;
                var expected = (ushort)(rgb[o] >> 3 | (rgb[o + 1] >> 3) << 5 | (rgb[o + 2] >> 3) << 10);
                var actual = (ushort)(_gpu.Pixel(x, y) & 0x7FFF);
                if (expected != actual && count++ < 5)
                {
                    mismatches.Add($"({x}, {y}): expected {expected:X4}, drew {actual:X4}");
                }
            }
        }

        count.Should().Be(0, $"every pixel should match the hardware; first: {string.Join("; ", mismatches)}");
    }
}
