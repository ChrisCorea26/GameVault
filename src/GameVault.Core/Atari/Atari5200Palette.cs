namespace GameVault.Core.Atari;

/// <summary>
/// The 5200's colours, indexed by a GTIA colour value (hue in the top four bits, luminance in the low four). GTIA's NTSC
/// colours are the TIA's sixteen hues of eight luminances, so this shares the 2600's table; GTIA's 16-luminance mode
/// shows its odd luminances as the even ones below them.
/// </summary>
public static class Atari5200Palette
{
    public static uint[] Argb { get; } = Build();

    private static uint[] Build()
    {
        var argb = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            argb[i] = Atari2600Palette.Argb[i >> 1];
        }

        return argb;
    }
}
