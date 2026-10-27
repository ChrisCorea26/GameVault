using GameVault.Core.State;

namespace GameVault.Core.Sega32X;

/// <summary>
/// The 32X's VDP (sega-cd-32x.md, milestone 2): two 128 KB frame buffers (one shown while the other is drawn, swapped
/// at vertical blank), a 256-colour palette of 15-bit colours, and three ways to read a line: packed pixels (a byte
/// each, through the palette), direct colour (a 15-bit word each) and run lengths (a colour and a count per word).
/// Each frame buffer starts with a table of 256 line addresses (in words).
/// </summary>
/// <remarks>
/// Registers (the SH-2s' $4100, the 68000's $A15180): $00 the mode (bits 0–1), PRI (bit 7: the 32X in front) and PAL
/// (bit 15, read-only); $02 the shift (bit 0: packed lines start a byte later); $04 the fill length, $06 the fill
/// address, $08 the fill data (writing it fills); $0A FS (the buffer shown), FEN, PEN, HBLK and VBLK. Bit 15 of a
/// colour (the palette's, or a direct colour) swaps that pixel's priority. The overwrite image ($20000 above the frame
/// buffer) writes only non-zero bytes. Behaviour follows Sega's 32X hardware manual, read for behaviour.
/// </remarks>
public sealed class Mars32XVdp : IStateful
{
    public const int Width = 320;

    private static readonly StateTag Tag = StateTag.From("32XV");

    private readonly ushort[][] _buffers = [new ushort[0x10000], new ushort[0x10000]];
    private readonly ushort[] _palette = new ushort[256];
    private ushort _mode;
    private ushort _shift;
    private ushort _fillLength;
    private ushort _fillAddress;
    private bool _displayBuffer;
    private bool _pendingBuffer;
    private bool _vblank;
    private bool _hblank;

    public bool Pal { get; set; }

    /// <summary>The frame buffer the CPUs draw into (the one not shown).</summary>
    public ushort[] DrawBuffer => _buffers[_displayBuffer ? 0 : 1];

    public ushort[] ShownBuffer => _buffers[_displayBuffer ? 1 : 0];

    public Span<ushort> Palette => _palette;

    public int Mode => _mode & 3;

    /// <summary>PRI: the 32X picture is in front of the Genesis'.</summary>
    public bool InFront => (_mode & 0x80) != 0;

    public void Reset()
    {
        foreach (var buffer in _buffers)
        {
            Array.Clear(buffer);
        }

        Array.Clear(_palette);
        _mode = _shift = _fillLength = _fillAddress = 0;
        _displayBuffer = _pendingBuffer = false;
        _vblank = _hblank = false;
    }

    /// <summary>Vertical blank starts or ends; the buffer chosen with FS takes effect when it starts.</summary>
    public void SetVBlank(bool vblank)
    {
        if (vblank && !_vblank)
        {
            _displayBuffer = _pendingBuffer;
        }

        _vblank = vblank;
    }

    public void SetHBlank(bool hblank) => _hblank = hblank;

    public ushort ReadRegister(int offset) => (offset & 0x0E) switch
    {
        0x00 => (ushort)((_mode & 0x00C3) | (Pal ? 0 : 0x8000)),
        0x02 => _shift,
        0x04 => _fillLength,
        0x06 => _fillAddress,
        0x0A => (ushort)((_vblank ? 0x8000 : 0) | (_hblank ? 0x4000 : 0) | (_vblank || _hblank ? 0x2000 : 0) | (_displayBuffer ? 1 : 0)),
        _ => 0,
    };

    public void WriteRegister(int offset, ushort value)
    {
        switch (offset & 0x0E)
        {
            case 0x00:
                _mode = (ushort)(value & 0x00C3);
                break;
            case 0x02:
                _shift = (ushort)(value & 1);
                break;
            case 0x04:
                _fillLength = (ushort)(value & 0xFF);
                break;
            case 0x06:
                _fillAddress = value;
                break;
            case 0x08:
                Fill(value);
                break;
            case 0x0A:
                _pendingBuffer = (value & 1) != 0;
                if (_vblank)
                {
                    _displayBuffer = _pendingBuffer; // during blanking the swap is immediate
                }

                break;
        }
    }

    /// <summary>Auto fill: FILL LENGTH + 1 words from FILL ADDRESS, wrapping within its 256-word block.</summary>
    private void Fill(ushort data)
    {
        var buffer = DrawBuffer;
        var address = _fillAddress;
        for (var i = 0; i <= _fillLength; i++)
        {
            buffer[address] = data;
            address = (ushort)((address & 0xFF00) | ((address + 1) & 0xFF));
        }

        _fillAddress = address;
    }

    public ushort ReadFrame(int byteOffset) => DrawBuffer[(byteOffset >> 1) & 0xFFFF];

    public void WriteFrameWord(int byteOffset, ushort value) => DrawBuffer[(byteOffset >> 1) & 0xFFFF] = value;

    public void WriteFrameByte(int byteOffset, byte value)
    {
        ref var word = ref DrawBuffer[(byteOffset >> 1) & 0xFFFF];
        word = (byteOffset & 1) == 0 ? (ushort)((word & 0x00FF) | (value << 8)) : (ushort)((word & 0xFF00) | value);
    }

    /// <summary>The overwrite image: a zero byte leaves the frame buffer as it is.</summary>
    public void OverwriteByte(int byteOffset, byte value)
    {
        if (value != 0)
        {
            WriteFrameByte(byteOffset, value);
        }
    }

    public void OverwriteWord(int byteOffset, ushort value)
    {
        if ((value >> 8) != 0)
        {
            WriteFrameByte(byteOffset & ~1, (byte)(value >> 8));
        }

        if ((value & 0xFF) != 0)
        {
            WriteFrameByte(byteOffset | 1, (byte)value);
        }
    }

    /// <summary>
    /// Line <paramref name="y"/> of the shown buffer as 15-bit colours with bit 15 the pixel's priority swap; false
    /// when the VDP shows nothing (mode 0).
    /// </summary>
    public bool RenderLine(int y, Span<ushort> line)
    {
        var mode = Mode;
        if (mode == 0)
        {
            return false;
        }

        var buffer = ShownBuffer;
        var address = buffer[y & 0xFF];
        switch (mode)
        {
            case 1:
            {
                var byteAddress = (address << 1) + _shift;
                for (var x = 0; x < Width; x++, byteAddress++)
                {
                    var word = buffer[(byteAddress >> 1) & 0xFFFF];
                    var index = (byteAddress & 1) == 0 ? word >> 8 : word & 0xFF;
                    line[x] = _palette[index];
                }

                break;
            }

            case 2:
                for (var x = 0; x < Width; x++)
                {
                    line[x] = buffer[(address + x) & 0xFFFF];
                }

                break;
            default:
            {
                var x = 0;
                var at = address;
                while (x < Width)
                {
                    var word = buffer[at];
                    at = (ushort)(at + 1);
                    var colour = _palette[word >> 8];
                    for (var run = (word & 0xFF) + 1; run > 0 && x < Width; run--)
                    {
                        line[x++] = colour;
                    }
                }

                break;
            }
        }

        return true;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var buffer in _buffers)
        {
            foreach (var word in buffer)
            {
                writer.WriteUInt16(word);
            }
        }

        foreach (var colour in _palette)
        {
            writer.WriteUInt16(colour);
        }

        writer.WriteUInt16(_mode);
        writer.WriteUInt16(_shift);
        writer.WriteUInt16(_fillLength);
        writer.WriteUInt16(_fillAddress);
        writer.WriteBool(_displayBuffer);
        writer.WriteBool(_pendingBuffer);
        writer.WriteBool(_vblank);
        writer.WriteBool(_hblank);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        foreach (var buffer in _buffers)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = reader.ReadUInt16();
            }
        }

        for (var i = 0; i < _palette.Length; i++)
        {
            _palette[i] = reader.ReadUInt16();
        }

        _mode = reader.ReadUInt16();
        _shift = reader.ReadUInt16();
        _fillLength = reader.ReadUInt16();
        _fillAddress = reader.ReadUInt16();
        _displayBuffer = reader.ReadBool();
        _pendingBuffer = reader.ReadBool();
        _vblank = reader.ReadBool();
        _hblank = reader.ReadBool();
        reader.EndChunk();
    }
}
