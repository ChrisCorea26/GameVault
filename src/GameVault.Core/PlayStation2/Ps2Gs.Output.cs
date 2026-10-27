namespace GameVault.Core.PlayStation2;

/// <summary>
/// The PCRTC's output: the two read circuits (DISPFB1/2 with DISPLAY1/2), merged by PMODE, over BGCOLOR.
/// </summary>
/// <remarks>
/// <para>
/// The picture is the display area in framebuffer pixels: width = (DW + 1) / (MAGH + 1) and height =
/// (DH + 1) / (MAGV + 1), taken from the widest and tallest enabled circuit and capped at
/// <see cref="MaxFrameWidth"/> × <see cref="MaxFrameHeight"/> (wider pictures are sampled down). A typical NTSC
/// interlaced game (DW 2559, MAGH 3, DH 447) gives 640×448, MAGH 4 gives 512×448, and PAL's DH 511 gives
/// 640×512. Each circuit is placed at its DX/DY offset from the leftmost/topmost enabled circuit, in the same units.
/// </para>
/// <para>
/// In interlaced frame mode (SMODE2.INT = 1, FFMD = 1) each field reads every line of the buffer, so half as many
/// buffer lines fill the picture and each is shown twice; field mode (each field reads every other line, the two
/// fields together the whole buffer) and progressive output read one buffer line per picture line (as Play!'s
/// GSHandler::GetDisplayRect and PCSX2's GS halve the display height only in interlaced frame mode). The merge uses ALP (PMODE.MMOD = 1) or circuit 1's pixel alpha, where $80 means fully circuit 1
/// (16-bit pixels: the alpha bit; 24-bit pixels: $80). Output is BGR555 with red in the low bits, indexing the
/// shared 32,768-colour palette.
/// </para>
/// </remarks>
public sealed partial class Ps2Gs
{
    public const int MaxFrameWidth = 640;
    public const int MaxFrameHeight = 512;

    private readonly struct Circuit
    {
        public readonly bool Enabled;
        public readonly int Fbp;
        public readonly int Fbw;
        public readonly int Psm;
        public readonly int Dbx;
        public readonly int Dby;
        public readonly int Dx;
        public readonly int Dy;
        public readonly int Width;
        public readonly int Height;

        public Circuit(bool enabled, ulong dispfb, ulong display)
        {
            Enabled = enabled;
            Fbp = (int)(dispfb & 0x1FF) * 32;
            Fbw = (int)((dispfb >> 9) & 0x3F);
            Psm = (int)((dispfb >> 15) & 0x1F);
            Dbx = (int)((dispfb >> 32) & 0x7FF);
            Dby = (int)((dispfb >> 43) & 0x7FF);
            var magh = (int)((display >> 23) & 0xF) + 1;
            var magv = (int)((display >> 27) & 3) + 1;
            Dx = (int)(display & 0xFFF) / magh;
            Dy = (int)((display >> 12) & 0x7FF) / magv;
            Width = ((int)((display >> 32) & 0xFFF) + 1) / magh;
            Height = ((int)((display >> 44) & 0x7FF) + 1) / magv;
        }
    }

    /// <summary>
    /// Renders the displayed picture into <paramref name="frame"/> (at least <see cref="MaxFrameWidth"/> ×
    /// <see cref="MaxFrameHeight"/> entries) as BGR555 and reports its size.
    /// </summary>
    public void RenderFrame(ushort[] frame, out int width, out int height)
    {
        Flush();
        var c1 = new Circuit((_pmode & 1) != 0, _dispfb1, _display1);
        var c2 = new Circuit((_pmode & 2) != 0, _dispfb2, _display2);
        var bg = (uint)(_bgcolor & 0xFFFFFF);

        int minDx, minDy, w, h;
        if (c1.Enabled || c2.Enabled)
        {
            minDx = Math.Min(c1.Enabled ? c1.Dx : int.MaxValue, c2.Enabled ? c2.Dx : int.MaxValue);
            minDy = Math.Min(c1.Enabled ? c1.Dy : int.MaxValue, c2.Enabled ? c2.Dy : int.MaxValue);
            w = Math.Max(c1.Enabled ? c1.Dx - minDx + c1.Width : 0, c2.Enabled ? c2.Dx - minDx + c2.Width : 0);
            h = Math.Max(c1.Enabled ? c1.Dy - minDy + c1.Height : 0, c2.Enabled ? c2.Dy - minDy + c2.Height : 0);
        }
        else
        {
            minDx = minDy = 0;
            w = 640;
            h = (_smode2 & 1) != 0 ? 448 : 224;
        }

        var sourceWidth = Math.Max(w, 1);
        width = Math.Clamp(w, 1, MaxFrameWidth);
        height = Math.Clamp(h, 1, MaxFrameHeight);
        var fieldMode = (_smode2 & 3) == 3; // interlaced frame mode: each buffer line shows twice

        var mmod = (_pmode & (1 << 5)) != 0;
        var slbg = (_pmode & (1 << 7)) != 0;
        var alp = (int)((_pmode >> 8) & 0xFF);

        for (var y = 0; y < height; y++)
        {
            var row = frame.AsSpan(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var sx = width == sourceWidth ? x : (int)((long)x * sourceWidth / width);
                var color = bg;
                var under = bg;
                if (c2.Enabled && !slbg && TryReadCircuit(c2, sx + minDx, y + minDy, fieldMode, out var p2))
                {
                    under = p2;
                    color = p2;
                }

                if (c1.Enabled && TryReadCircuit(c1, sx + minDx, y + minDy, fieldMode, out var p1))
                {
                    var alpha = mmod ? alp : Math.Min((int)(p1 >> 24) * 2, 255);
                    color = Mix(p1, under, alpha);
                }

                row[x] = (ushort)(((color >> 3) & 0x1F) | ((color >> 6) & 0x3E0) | ((color >> 9) & 0x7C00));
            }
        }
    }

    private static uint Mix(uint top, uint under, int alpha)
    {
        if (alpha >= 255)
        {
            return top;
        }

        uint result = 0;
        for (var shift = 0; shift < 24; shift += 8)
        {
            var t = (int)((top >> shift) & 0xFF);
            var u = (int)((under >> shift) & 0xFF);
            result |= (uint)(((t * alpha) + (u * (255 - alpha))) / 255) << shift;
        }

        return result;
    }

    /// <summary>Reads a circuit's pixel at picture position (x, y) as RGBA8888, or false outside its display area.</summary>
    private bool TryReadCircuit(in Circuit c, int x, int y, bool fieldMode, out uint color)
    {
        x -= c.Dx;
        y -= c.Dy;
        if ((uint)x >= (uint)c.Width || (uint)y >= (uint)c.Height)
        {
            color = 0;
            return false;
        }

        if (fieldMode)
        {
            y >>= 1;
        }

        var raw = ReadPixel(c.Psm, c.Fbp, c.Fbw, c.Dbx + x, c.Dby + y);
        color = c.Psm switch
        {
            PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S =>
                ((raw & 0x1F) << 3) | ((raw & 0x3E0) << 6) | ((raw & 0x7C00) << 9) | ((raw & 0x8000) != 0 ? 0x8000_0000u : 0),
            PsmCt24 or PsmZ24 => raw | 0x8000_0000u,
            _ => raw,
        };
        return true;
    }
}
