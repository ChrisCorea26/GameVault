using GameVault.Core.Ppu;
using GameVault.Core.State;

namespace GameVault.Core.Input;

/// <summary>
/// The Zapper light gun on port 2 (NESdev Wiki: "Zapper"). Reads return D3 = 0 when the photodiode sees
/// light and D4 = 1 while the trigger is pulled. The diode sees light when the pixel under the aim point is
/// bright and the beam drew it recently (it stays lit for about 20 scanlines). The aim point and trigger are
/// inputs set between frames, so the result is deterministic.
/// </summary>
public sealed class Zapper(Ppu2C02 ppu) : IInputDevice
{
    private static readonly StateTag Tag = StateTag.From("ZAPR");

    /// <summary>How long the sensor keeps reporting light after the beam passes, in scanlines.</summary>
    public const int SenseScanlines = 20;

    /// <summary>Minimum brightness (0-255 average of R, G, B) the diode reacts to.</summary>
    public const int BrightnessThreshold = 160;

    /// <summary>Aim point in NES pixels (0-255, 0-239), or null when pointing away from the screen.</summary>
    public (int X, int Y)? Aim { get; set; }

    public bool Trigger { get; set; }

    public void Write(byte value)
    {
    }

    public byte Read() => (byte)((SeesLight() ? 0 : 0x08) | (Trigger ? 0x10 : 0));

    public bool SeesLight()
    {
        if (Aim is not var (x, y) || (uint)x >= 256 || (uint)y >= 240)
        {
            return false;
        }

        var scanline = ppu.Scanline;
        var drawn = scanline > y || (scanline == y && ppu.Dot > x + 1);
        if (!drawn || scanline >= y + SenseScanlines || scanline >= 240)
        {
            return false;
        }

        // The lens sees a small area, not one pixel: average the aim pixel and its horizontal neighbors.
        var frame = ppu.FrameBuffer;
        var palette = NtscPalette.Argb;
        var total = 0;
        var count = 0;
        for (var dx = -1; dx <= 1; dx++)
        {
            var px = x + dx;
            if ((uint)px < 256)
            {
                var argb = palette[frame[(y << 8) | px] & 0x1FF];
                total += (int)(((argb >> 16) & 0xFF) + ((argb >> 8) & 0xFF) + (argb & 0xFF)) / 3;
                count++;
            }
        }

        return total / count >= BrightnessThreshold;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBool(Aim is not null);
        writer.WriteInt32(Aim?.X ?? 0);
        writer.WriteInt32(Aim?.Y ?? 0);
        writer.WriteBool(Trigger);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        var aimed = reader.ReadBool();
        var x = reader.ReadInt32();
        var y = reader.ReadInt32();
        Aim = aimed ? (x, y) : null;
        Trigger = reader.ReadBool();
        reader.EndChunk();
    }
}
