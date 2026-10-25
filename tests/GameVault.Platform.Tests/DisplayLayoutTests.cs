using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

public class DisplayLayoutTests
{
    [Fact]
    public void Sharp_pixels_uses_the_largest_whole_scale_and_centers()
    {
        var rect = DisplayLayout.Compute(1920, 1080, DisplayPreset.SharpPixels, cropOverscan: true, tvPixelShape: false);

        rect.Should().Be(new DisplayRect(448, 92, 1024, 896, 8, 224)); // 1080 / 224 = 4.8, so 4x
    }

    [Fact]
    public void Sharp_pixels_never_goes_below_1x()
    {
        var rect = DisplayLayout.Compute(100, 100, DisplayPreset.SharpPixels, cropOverscan: false, tvPixelShape: false);

        rect.Width.Should().Be(100);
        rect.Height.Should().Be(100);
        rect.SourceHeight.Should().Be(240);
    }

    [Fact]
    public void Tv_pixel_shape_widens_by_8_to_7()
    {
        var rect = DisplayLayout.Compute(1920, 1080, DisplayPreset.SharpPixels, cropOverscan: true, tvPixelShape: true);

        rect.Height.Should().Be(896);
        rect.Width.Should().Be(1170); // 256 * 8/7 * 4 = 1170.3
    }

    [Theory]
    [InlineData(DisplayPreset.Smooth)]
    [InlineData(DisplayPreset.LivingRoomCrt)]
    [InlineData(DisplayPreset.ArcadeMonitor)]
    public void Other_presets_fill_the_height(DisplayPreset preset)
    {
        var rect = DisplayLayout.Compute(1920, 1080, preset, cropOverscan: true, tvPixelShape: false);

        rect.Height.Should().Be(1080);
        rect.Width.Should().Be(1234); // 256 * 1080 / 224
        rect.X.Should().Be((1920 - 1234) / 2);
    }
}
