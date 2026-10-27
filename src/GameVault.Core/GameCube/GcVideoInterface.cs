using GameVault.Core.State;

namespace GameVault.Core.GameCube;

/// <summary>
/// The GameCube's video interface (phase-18.md, milestone 1): its registers at $0C002000, the four display interrupts
/// (DI0–3, each at a line it names), the current position, and the picture: the external frame buffer (XFB) in main
/// RAM, YUV 4:2:2 (Y0 U Y1 V per two pixels), turned into 32-bit colour.
/// </summary>
/// <remarks>
/// The picture is 640 pixels wide and 480 lines tall (NTSC) or 574 (PAL), read from the top field's base (TFBL,
/// shifted by five when its POFF bit is set) with the stride the HSW register gives (or the width when it gives none),
/// as libogc lays out a frame. Interlaced fields are shown as one frame. Reference: YAGCD, "Video Interface".
/// </remarks>
public sealed class GcVideoInterface : IStateful
{
    public const int Width = 640;

    private static readonly StateTag Tag = StateTag.From("GCVI");

    private readonly ushort[] _regs = new ushort[0x40];
    private readonly uint[] _argb = new uint[Width * 576];
    private int _line;

    public int LinesPerField => Pal ? 313 : 263;

    public bool Pal => ((_regs[1] >> 8) & 3) == 1;

    public int Height => Pal ? 574 : 480;

    public ReadOnlySpan<uint> Picture => _argb.AsSpan(0, Width * Height);

    /// <summary>A display interrupt is pending and enabled (the processor interface's VI cause).</summary>
    public bool Interrupt
    {
        get
        {
            for (var i = 0; i < 4; i++)
            {
                var di = DisplayInterrupt(i);
                if ((di & 0x80000000) != 0 && (di & 0x10000000) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private uint DisplayInterrupt(int i) => ((uint)_regs[0x18 + i * 2] << 16) | _regs[0x19 + i * 2];

    public void Reset()
    {
        Array.Clear(_regs);
        Array.Clear(_argb);
        _line = 0;
    }

    /// <summary>The beam reaches <paramref name="line"/> (from 0): the display interrupts whose line it is fire.</summary>
    public void BeginLine(int line)
    {
        _line = line;
        for (var i = 0; i < 4; i++)
        {
            var di = DisplayInterrupt(i);
            var vct = (int)((di >> 16) & 0x3FF);
            if ((di & 0x10000000) != 0 && vct == line + 1)
            {
                di |= 0x80000000;
                _regs[0x18 + i * 2] = (ushort)(di >> 16);
            }
        }
    }

    public ushort Read16(int offset)
    {
        var index = (offset >> 1) & 0x3F;
        return index switch
        {
            0x16 => (ushort)(_line + 1), // DPV: the current line (from 1)
            0x17 => 1, // DPH
            _ => _regs[index],
        };
    }

    public void Write16(int offset, ushort value)
    {
        var index = (offset >> 1) & 0x3F;
        if (index == 1 && (value & 2) != 0)
        {
            // DCR's RST: the interface resets (everything but the configuration).
            Array.Clear(_regs, 2, _regs.Length - 2);
        }

        _regs[index] = value;
    }

    /// <summary>The top field's frame buffer in physical memory.</summary>
    public uint FrameBufferAddress
    {
        get
        {
            var tfbl = ((uint)_regs[0x0E] << 16) | _regs[0x0F];
            var address = tfbl & 0x00FFFFFF;
            return (tfbl & 0x10000000) != 0 ? address << 5 : address;
        }
    }

    /// <summary>The XFB's stride in bytes: HSW's width in units of 16 pixels, or the picture's width.</summary>
    private int Stride
    {
        get
        {
            var units = _regs[0x24] & 0xFF;
            return units == 0 ? Width * 2 : units * 32;
        }
    }

    /// <summary>Draws the picture from the XFB (called at the end of each field). Nothing shows while DCR's enable is off.</summary>
    public void Render(ReadOnlySpan<byte> ram)
    {
        if ((_regs[1] & 1) == 0)
        {
            Array.Clear(_argb);
            return;
        }

        var baseAddress = (int)(FrameBufferAddress & 0x01FFFFFF);
        var stride = Stride;
        for (var y = 0; y < Height; y++)
        {
            var row = baseAddress + y * stride;
            for (var x = 0; x < Width; x += 2)
            {
                var at = row + x * 2;
                if (at < 0 || at + 3 >= ram.Length)
                {
                    _argb[y * Width + x] = _argb[y * Width + x + 1] = 0xFF000000;
                    continue;
                }

                int y0 = ram[at], u = ram[at + 1], y1 = ram[at + 2], v = ram[at + 3];
                _argb[y * Width + x] = YuvToArgb(y0, u, v);
                _argb[y * Width + x + 1] = YuvToArgb(y1, u, v);
            }
        }
    }

    /// <summary>BT.601 studio-range YCbCr to RGB.</summary>
    public static uint YuvToArgb(int y, int u, int v)
    {
        var c = (y - 16) * 1.164;
        var d = u - 128;
        var e = v - 128;
        var r = (int)Math.Round(Math.Clamp(c + 1.596 * e, 0, 255));
        var g = (int)Math.Round(Math.Clamp(c - 0.392 * d - 0.813 * e, 0, 255));
        var b = (int)Math.Round(Math.Clamp(c + 2.017 * d, 0, 255));
        return 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var r in _regs)
        {
            writer.WriteUInt16(r);
        }

        writer.WriteInt32(_line);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _regs.Length; i++)
        {
            _regs[i] = reader.ReadUInt16();
        }

        _line = Math.Max(0, reader.ReadInt32());
        reader.EndChunk();
    }
}
