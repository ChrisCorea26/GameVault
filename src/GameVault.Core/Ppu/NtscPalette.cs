namespace GameVault.Core.Ppu;

/// <summary>
/// Generates the NES palette by simulating the 2C02's composite video signal and decoding it as an NTSC
/// TV would (NESdev Wiki: "NTSC video"). This avoids shipping a third-party palette file.
/// </summary>
/// <remarks>
/// Each color is a square wave between two voltage levels whose phase sets the hue. We average the
/// signal over the 12 subcarrier phases to get Y, I and Q, then convert to sRGB. Emphasis bits attenuate
/// the wave during their phases.
/// </remarks>
public static class NtscPalette
{
    /// <summary>Entries in the full palette: 64 colors × 8 emphasis combinations.</summary>
    public const int Size = 512;

    // Voltage levels relative to sync, from the NESdev Wiki "NTSC video" page.
    private static readonly float[] LowLevels = [0.350f, 0.518f, 0.962f, 1.550f];
    private static readonly float[] HighLevels = [1.094f, 1.506f, 1.962f, 1.962f];
    private const float Black = 0.518f;
    private const float White = 1.962f;
    private const float Attenuation = 0.746f;

    /// <summary>Hue rotation in subcarrier phases; tuned so color $x6 is red and $x2 is blue.</summary>
    private const float HuePhaseOffset = 3.9f;

    private const float Saturation = 1.0f;
    private const float Gamma = 2.2f / 1.8f;

    private static readonly Lazy<uint[]> Default = new(() => Generate());

    /// <summary>The default palette as 0xAARRGGBB, indexed by (emphasis &lt;&lt; 6) | color.</summary>
    public static ReadOnlySpan<uint> Argb => Default.Value;

    public static uint[] Generate()
    {
        var palette = new uint[Size];
        for (var entry = 0; entry < Size; entry++)
        {
            palette[entry] = Decode(entry);
        }

        return palette;
    }

    private static uint Decode(int entry)
    {
        double y = 0, i = 0, q = 0;
        for (var phase = 0; phase < 12; phase++)
        {
            var signal = (Signal(entry, phase) - Black) / (White - Black);
            var angle = Math.PI * (phase + HuePhaseOffset) / 6.0;
            y += signal;
            i += signal * Math.Cos(angle);
            q += signal * Math.Sin(angle);
        }

        y /= 12;
        i = i / 12 * Saturation;
        q = q / 12 * Saturation;

        // FCC YIQ -> RGB
        var r = y + 0.946882 * i + 0.623557 * q;
        var g = y - 0.274788 * i - 0.635691 * q;
        var b = y - 1.108545 * i + 1.709007 * q;
        return 0xFF000000u | (ToByte(r) << 16) | (ToByte(g) << 8) | ToByte(b);
    }

    private static float Signal(int entry, int phase)
    {
        var color = entry & 0x0F;
        var level = (entry >> 4) & 0x03;
        var emphasis = entry >> 6;

        if (color > 13)
        {
            level = 1; // $xE and $xF are black: forced to level 1
        }

        var low = LowLevels[level];
        var high = HighLevels[level];
        if (color == 0)
        {
            low = high; // $x0: grays, high level only
        }
        else if (color > 12)
        {
            high = low; // $xD-$xF: low level only
        }

        var signal = InColorPhase(color, phase) ? high : low;

        if (((emphasis & 1) != 0 && InColorPhase(0, phase))
            || ((emphasis & 2) != 0 && InColorPhase(4, phase))
            || ((emphasis & 4) != 0 && InColorPhase(8, phase)))
        {
            signal *= Attenuation;
        }

        return signal;
    }

    private static bool InColorPhase(int color, int phase) => (color + phase) % 12 < 6;

    private static uint ToByte(double value)
    {
        value = Math.Clamp(value, 0, 1);
        value = Math.Pow(value, Gamma);
        return (uint)Math.Round(value * 255);
    }
}
