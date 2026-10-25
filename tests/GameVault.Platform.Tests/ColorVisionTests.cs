using GameVault.Core.Ppu;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

public class ColorVisionTests
{
    private static double SimulatedDistance(uint a, uint b, ColorVision vision)
    {
        static (double, double, double) Rgb(uint c) => (((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0);
        var (ar, ag, ab) = Rgb(a);
        var (br, bg, bb) = Rgb(b);
        var sa = ColorVisionPalettes.Simulate(ar, ag, ab, vision);
        var sb = ColorVisionPalettes.Simulate(br, bg, bb, vision);
        return Math.Sqrt(Math.Pow(sa.R - sb.R, 2) + Math.Pow(sa.G - sb.G, 2) + Math.Pow(sa.B - sb.B, 2));
    }

    [Theory]
    [InlineData(ColorVision.Deuteranopia)]
    [InlineData(ColorVision.Protanopia)]
    public void Red_and_green_are_easier_to_tell_apart_after_correction(ColorVision vision)
    {
        var palette = NtscPalette.Argb;
        var red = palette[0x16];
        var green = palette[0x1A];
        var corrected = ColorVisionPalettes.Create(palette, vision);

        SimulatedDistance(corrected[0x16], corrected[0x1A], vision)
            .Should().BeGreaterThan(SimulatedDistance(red, green, vision));
    }

    [Fact]
    public void Standard_is_unchanged_and_every_palette_stays_opaque()
    {
        var palette = NtscPalette.Argb;

        ColorVisionPalettes.Create(palette, ColorVision.Standard).Should().Equal(palette.ToArray());
        foreach (var vision in Enum.GetValues<ColorVision>())
        {
            ColorVisionPalettes.Create(palette, vision).Should().OnlyContain(c => (c >> 24) == 0xFF);
        }
    }
}
