using GameVault.Core.Ppu;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>
/// Runs the real shaders on Direct3D's software rasterizer (WARP), so the presets are checked on any Windows
/// machine, including CI. Skipped elsewhere.
/// </summary>
public class D3D11FrameRendererTests
{
    [Theory]
    [InlineData(DisplayPreset.SharpPixels)]
    [InlineData(DisplayPreset.Smooth)]
    [InlineData(DisplayPreset.LivingRoomCrt)]
    [InlineData(DisplayPreset.ArcadeMonitor)]
    public void Every_preset_draws_the_frame_inside_black_bars(DisplayPreset preset)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Direct3D only exists on Windows
        }

        using var renderer = new D3D11FrameRenderer(1280, 720, forceWarp: true);
        var frame = new ushort[256 * 240];
        Array.Fill(frame, (ushort)0x30); // white
        renderer.UploadFrame(frame, NtscPalette.Argb);

        renderer.RenderWithoutPresent(preset, cropLines: 8, tvPixelShape: false);

        var center = renderer.ReadPixel(640, 360);
        var corner = renderer.ReadPixel(2, 2);
        Brightness(center).Should().BeGreaterThan(120, $"the picture shows in the middle (got {center:X8})");
        (corner & 0x00FFFFFF).Should().Be(0, "the bars beside the picture are black");
    }

    private static int Brightness(uint argb) => (int)(((argb >> 16) & 0xFF) + ((argb >> 8) & 0xFF) + (argb & 0xFF)) / 3;
}
