using GameVault.ViewModels.Services;

namespace GameVault.Platform.Video;

/// <summary>
/// Colorblind-friendly versions of a palette by daltonization: simulate how a color looks with the given
/// color vision deficiency (Viénot, Brettel &amp; Mollon 1999, in LMS space), then move the lost difference
/// into channels that person can see (Fidaner, Lin &amp; Ozguven). Generated at startup; nothing is shipped.
/// </summary>
public static class ColorVisionPalettes
{
    public static uint[] Create(ReadOnlySpan<uint> palette, ColorVision vision)
    {
        var result = new uint[palette.Length];
        for (var i = 0; i < palette.Length; i++)
        {
            result[i] = vision == ColorVision.Standard ? palette[i] : Daltonize(palette[i], vision);
        }

        return result;
    }

    public static uint Daltonize(uint argb, ColorVision vision)
    {
        var r = ((argb >> 16) & 0xFF) / 255.0;
        var g = ((argb >> 8) & 0xFF) / 255.0;
        var b = (argb & 0xFF) / 255.0;
        var (sr, sg, sb) = Simulate(r, g, b, vision);

        // Shift the error into visible channels.
        var er = r - sr;
        var eg = g - sg;
        var eb = b - sb;
        var (dr, dg, db) = vision == ColorVision.Tritanopia
            ? (er + 0.7 * eb, eg + 0.7 * eb, 0.0)
            : (0.0, 0.7 * er + eg, 0.7 * er + eb);

        return (argb & 0xFF000000u)
            | ((uint)Channel(r + dr) << 16)
            | ((uint)Channel(g + dg) << 8)
            | Channel(b + db);
    }

    /// <summary>How a color appears with the deficiency (used by the tests and the transform above).</summary>
    public static (double R, double G, double B) Simulate(double r, double g, double b, ColorVision vision)
    {
        var l = 17.8824 * r + 43.5161 * g + 4.11935 * b;
        var m = 3.45565 * r + 27.1554 * g + 3.86714 * b;
        var s = 0.0299566 * r + 0.184309 * g + 1.46709 * b;
        switch (vision)
        {
            case ColorVision.Protanopia:
                l = 2.02344 * m - 2.52581 * s;
                break;
            case ColorVision.Deuteranopia:
                m = 0.494207 * l + 1.24827 * s;
                break;
            case ColorVision.Tritanopia:
                s = -0.395913 * l + 0.801109 * m;
                break;
        }

        return (
            0.0809444479 * l - 0.130504409 * m + 0.116721066 * s,
            -0.0102485335 * l + 0.0540193266 * m - 0.113614708 * s,
            -0.000365296938 * l - 0.00412161469 * m + 0.693511405 * s);
    }

    private static uint Channel(double value) => (uint)Math.Clamp(Math.Round(value * 255), 0, 255);
}
