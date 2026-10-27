using GameVault.Core.PlayStation;
using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

/// <summary>
/// The PlayStation GPU renderer (docs/plans/gpu-renderers.md) against the software GPU, on Direct3D's software
/// rasteriser (WARP), so CI's Windows job checks it. Skipped elsewhere.
/// </summary>
public sealed class D3D11PsGpuRendererTests
{
    private static uint Xy(int x, int y) => (uint)(y << 16 | (x & 0xFFFF));

    private static uint Rgb(int r, int g, int b) => (uint)(b << 16 | g << 8 | r);

    /// <summary>A GPU showing 320×240 from VRAM's corner, drawing anywhere on it.</summary>
    private static PsGpu Gpu()
    {
        var gpu = new PsGpu();
        gpu.Power();
        gpu.WriteGp1(0x00000000); // reset
        gpu.WriteGp1(0x03000000); // display on
        gpu.WriteGp1(0x08000001); // 320 wide
        gpu.WriteGp1(0x05000000); // display area at 0, 0
        gpu.WriteGp0(0xE3000000); // draw area: from 0, 0
        gpu.WriteGp0(0xE4000000 | (239u << 10) | 319u); // to 319, 239
        gpu.WriteGp0(0xE5000000); // no offset
        return gpu;
    }

    private static void Send(PsGpu gpu, params uint[] words)
    {
        foreach (var word in words)
        {
            gpu.WriteGp0(word);
        }
    }

    /// <summary>The software GPU's picture as ARGB (15-bit colours widened as the renderer stores them).</summary>
    private static uint[] Software(PsGpu gpu)
    {
        var frame = new ushort[gpu.DisplayWidth * gpu.DisplayHeight];
        gpu.CopyDisplay(frame);
        static uint Widen(int c) => (uint)Math.Round(c * 255.0 / 31);
        return [.. frame.Select(p => 0xFF000000 | Widen(p & 31) << 16 | Widen((p >> 5) & 31) << 8 | Widen((p >> 10) & 31))];
    }

    /// <summary>The share of pixels whose colour differs by more than <paramref name="tolerance"/> in any channel.</summary>
    private static double Differing(ReadOnlySpan<uint> expected, ReadOnlySpan<uint> actual, int scale, int width, int tolerance = 24)
    {
        var height = expected.Length / width;
        var different = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var e = expected[y * width + x];
                var a = actual[(y * scale) * width * scale + x * scale];
                for (var shift = 0; shift < 24; shift += 8)
                {
                    if (Math.Abs((int)((e >> shift) & 0xFF) - (int)((a >> shift) & 0xFF)) > tolerance)
                    {
                        different++;
                        break;
                    }
                }
            }
        }

        return (double)different / expected.Length;
    }

    private static void Scene(PsGpu gpu)
    {
        // A grey background, then one of each primitive.
        Send(gpu, 0x02000000 | Rgb(64, 64, 64), Xy(0, 0), Xy(320, 240));
        Send(gpu, 0x20000000 | Rgb(255, 0, 0), Xy(10, 10), Xy(100, 20), Xy(30, 90)); // flat triangle
        Send(gpu, 0x38000000 | Rgb(255, 0, 0), Xy(120, 10), Rgb(0, 255, 0), Xy(200, 10), Rgb(0, 0, 255), Xy(120, 90), Rgb(255, 255, 255), Xy(200, 90)); // Gouraud quad
        Send(gpu, 0x40000000 | Rgb(255, 255, 0), Xy(10, 120), Xy(150, 200)); // line
        Send(gpu, 0x60000000 | Rgb(0, 128, 255), Xy(220, 20), Xy(60, 40)); // rectangle

        // Semi-transparent (B + F) triangle over the background.
        Send(gpu, 0xE1000000 | (1u << 5));
        Send(gpu, 0x22000000 | Rgb(0, 96, 0), Xy(220, 100), Xy(300, 100), Xy(260, 180));

        // A 15-bit texture uploaded to VRAM at 512, 0 (a 16×16 checkerboard), drawn raw as a rectangle.
        Send(gpu, 0xA0000000, Xy(512, 0), Xy(16, 16));
        for (var i = 0; i < 16 * 16 / 2; i++)
        {
            var x = (i * 2) % 16;
            var y = i * 2 / 16;
            ushort Texel(int tx) => ((tx / 4 + y / 4) & 1) == 0 ? (ushort)0x7C1F : (ushort)0x03E0;
            gpu.WriteGp0((uint)(Texel(x) | Texel(x + 1) << 16));
        }

        Send(gpu, 0xE1000000 | 8u | (2u << 7)); // page 512, 0; 15-bit
        Send(gpu, 0x65000000, Xy(20, 140), 0, Xy(64, 64)); // raw textured rectangle, wrapping the 16×16 texture

        // A 4-bit texture with its CLUT at 0, 480.
        Send(gpu, 0xA0000000, Xy(0, 480), Xy(16, 1));
        for (var i = 0; i < 8; i++)
        {
            gpu.WriteGp0((uint)((0x001F + i * 0x0421) | (0x7C00 - i * 0x0400) << 16));
        }

        Send(gpu, 0xA0000000, Xy(576, 0), Xy(4, 16)); // 16×16 texels of 4 bits, as 4×16 words
        for (var i = 0; i < 4 * 16 / 2; i++)
        {
            gpu.WriteGp0(0x76543210u ^ (uint)i);
        }

        Send(gpu, 0xE1000000 | 9u); // page 576, 0; 4-bit
        Send(gpu, 0x2C000000 | Rgb(128, 128, 128), // modulated textured quad
            Xy(100, 120), 480u << 22, Xy(180, 120), (9u << 16) | 15u, Xy(100, 200), (15u << 8), Xy(180, 200), (15u << 8) | 15u);

        // A VRAM copy of the flat triangle's corner.
        Send(gpu, 0x80000000, Xy(0, 0), Xy(260, 190), Xy(48, 48));
    }

    [Theory]
    [InlineData(1, 0.02)]
    [InlineData(2, 0.05)]
    public void The_gpu_renderer_draws_what_the_software_gpu_draws(int scale, double allowed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Direct3D only exists on Windows
        }

        var gpu = Gpu();
        using var renderer = new D3D11PsGpuRenderer(gpu, scale, forceWarp: true);
        gpu.Backend = renderer;

        Scene(gpu);

        var expected = Software(gpu);
        var picture = renderer.ReadDisplay(out var width, out var height);
        width.Should().Be(320 * scale);
        height.Should().Be(240 * scale);
        Differing(expected, picture, scale, 320).Should().BeLessThan(allowed);
    }

    [Fact]
    public void A_state_load_redraws_the_scaled_vram_from_the_emulated_one()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gpu = Gpu();
        Send(gpu, 0x02000000 | Rgb(200, 0, 0), Xy(0, 0), Xy(320, 240)); // red, before the renderer is attached
        var state = new GameVault.Core.State.StateWriter();
        gpu.SaveState(state);
        using var renderer = new D3D11PsGpuRenderer(gpu, 2, forceWarp: true);
        gpu.Backend = renderer;
        Send(gpu, 0x02000000 | Rgb(0, 200, 0), Xy(0, 0), Xy(320, 240));

        gpu.LoadState(new GameVault.Core.State.StateReader(state.ToArray()));

        var picture = renderer.ReadDisplay(out _, out _);
        ((picture[0] >> 16) & 0xFF).Should().BeGreaterThan(180, "red again");
        ((picture[0] >> 8) & 0xFF).Should().BeLessThan(20);
    }

    [Fact]
    public void Twenty_four_bit_video_is_left_to_the_software_picture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var gpu = Gpu();
        using var renderer = new D3D11PsGpuRenderer(gpu, 2, forceWarp: true);
        gpu.WriteGp1(0x08000011); // 320 wide, 24-bit

        renderer.ReadDisplay(out var width, out _).IsEmpty.Should().BeTrue();
        width.Should().Be(0);
    }
}
