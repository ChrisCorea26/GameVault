using GameVault.Core.State;

namespace GameVault.Core.Sega;

/// <summary>Which video chip a Sega console has.</summary>
public enum VdpModel
{
    /// <summary>TI TMS9918A (SG-1000, SC-3000): Graphics I/II, Text and Multicolor.</summary>
    Tms9918,

    /// <summary>Sega 315-5124 (Master System): the TMS modes plus Mode 4, 192 lines.</summary>
    Sms,

    /// <summary>Sega 315-5246 (Master System II): also the 224-line mode.</summary>
    Sms2,
}

/// <summary>
/// The SG-1000's TMS9918A and the Master System's VDP (SPEC §7.11, Phase 11). Rendered a line at a time:
/// <see cref="StartLine"/> runs each line's counters and interrupts, then draws it with the registers as they
/// are at that moment, so mid-frame changes (raster effects) land on the right line.
/// </summary>
/// <remarks>
/// References: Charles MacDonald, "Sega Master System VDP documentation" (msvdp-20021112), and the SMS Power!
/// development wiki; Texas Instruments TMS9918A Data Manual.
/// </remarks>
public sealed class SegaVdp : IStateful
{
    public const int Width = 256;
    public const int Height = 240;

    /// <summary>Frame buffer indices 0–63 are SMS colours (--BBGGRR), 64–79 the TMS9918 colours.</summary>
    public const int TmsColorBase = 64;

    private static readonly StateTag Tag = StateTag.From("VDP ");

    private readonly byte[] _vram = new byte[0x4000];
    private readonly byte[] _cram = new byte[32];
    private readonly byte[] _registers = new byte[16];
    private readonly ushort[] _frame = new ushort[Width * Height];
    private readonly byte[] _spriteLine = new byte[Width];
    private readonly byte[] _bgLine = new byte[Width];
    private readonly bool[] _bgPriority = new bool[Width];

    private ushort _address;
    private int _code;
    private bool _latched;
    private byte _readBuffer;
    private byte _status;
    private bool _lineInterruptPending;
    private int _lineCounter;
    private byte _vscrollLatch;
    private int _line;

    public SegaVdp(VdpModel model, bool pal = false)
    {
        Model = model;
        Pal = pal;
    }

    public VdpModel Model { get; }

    public bool Pal { get; }

    /// <summary>262 lines (NTSC) or 313 (PAL).</summary>
    public int LinesPerFrame => Pal ? 313 : 262;

    /// <summary>The finished frame (256×240 palette indices; see <see cref="Palette"/>).</summary>
    public ReadOnlySpan<ushort> FrameBuffer => _frame;

    /// <summary>ARGB for frame buffer indices.</summary>
    public static ReadOnlySpan<uint> Palette => PaletteTable;

    private static readonly uint[] PaletteTable = BuildPalette();

    /// <summary>The /INT line: a frame or line interrupt is pending and enabled.</summary>
    public bool Irq =>
        ((_status & 0x80) != 0 && (_registers[1] & 0x20) != 0)
        || (_lineInterruptPending && IsMode4 && (_registers[0] & 0x10) != 0);

    /// <summary>The line being drawn (0 is the first active line).</summary>
    public int Line => _line;

    public long FrameCount { get; private set; }

    private bool IsMode4 => Model != VdpModel.Tms9918 && (_registers[0] & 0x04) != 0;

    /// <summary>Active lines: 192, or 224 in the SMS2's extended mode.</summary>
    public int ActiveHeight =>
        Model == VdpModel.Sms2 && IsMode4 && (_registers[0] & 0x02) != 0 && (_registers[1] & 0x10) != 0 ? 224 : 192;

    public byte ReadRegister(int index) => _registers[index & 0x0F];

    public byte ReadVram(int address) => _vram[address & 0x3FFF];

    public void Power()
    {
        Array.Clear(_vram);
        Array.Clear(_cram);
        Array.Clear(_registers);
        Array.Clear(_frame);
        _address = 0;
        _code = 0;
        _latched = false;
        _readBuffer = 0;
        _status = 0;
        _lineInterruptPending = false;
        _lineCounter = 0;
        _vscrollLatch = 0;
        _line = 0;
        FrameCount = 0;
    }

    // ---------------------------------------------------------------- ports

    public byte ReadData()
    {
        _latched = false;
        var value = _readBuffer;
        _readBuffer = _vram[_address & 0x3FFF];
        _address = (ushort)((_address + 1) & 0x3FFF);
        return value;
    }

    public void WriteData(byte value)
    {
        _latched = false;
        if (_code == 3 && Model != VdpModel.Tms9918)
        {
            _cram[_address & 0x1F] = (byte)(value & 0x3F);
        }
        else
        {
            _vram[_address & 0x3FFF] = value;
        }

        _readBuffer = value;
        _address = (ushort)((_address + 1) & 0x3FFF);
    }

    /// <summary>Status: frame interrupt (7), sprite overflow (6), collision (5); reading clears them.</summary>
    public byte ReadControl()
    {
        var value = Model == VdpModel.Tms9918 ? _status : (byte)(_status | 0x1F);
        _status &= 0x1F;
        _lineInterruptPending = false;
        _latched = false;
        return value;
    }

    public void WriteControl(byte value)
    {
        if (!_latched)
        {
            _address = (ushort)((_address & 0x3F00) | value);
            _latched = true;
            return;
        }

        _latched = false;
        _address = (ushort)((_address & 0x00FF) | (value & 0x3F) << 8);
        _code = value >> 6;
        switch (_code)
        {
            case 0:
                _readBuffer = _vram[_address];
                _address = (ushort)((_address + 1) & 0x3FFF);
                break;
            case 2:
                var register = Model == VdpModel.Tms9918 ? value & 0x07 : value & 0x0F;
                _registers[register] = (byte)_address;
                break;
        }
    }

    /// <summary>The V counter as games read it (port $7E), with the jump back the real chip makes.</summary>
    public byte VCounter
    {
        get
        {
            var line = _line;
            if (!Pal)
            {
                return (byte)(ActiveHeight == 224 ? (line <= 0xEA ? line : line - 6) : (line <= 0xDA ? line : line - 6));
            }

            return ActiveHeight == 224
                ? (byte)(line <= 0xFF ? line : line <= 0x102 ? line - 0x100 : line - 0x39)
                : (byte)(line <= 0xF2 ? line : line - 0x39);
        }
    }

    /// <summary>The H counter for <paramref name="cycleInLine"/> (0–227 Z80 cycles), as latched by the light gun port.</summary>
    public static byte HCounter(int cycleInLine)
    {
        var h = cycleInLine * 342 / 228 / 2;
        return (byte)(h <= 0x93 ? h : h + 0x55);
    }

    // ---------------------------------------------------------------- timing

    /// <summary>
    /// Starts line <paramref name="line"/> (0 is the first active line): draws it if active, runs the line
    /// counter, and sets the frame interrupt after the last active line.
    /// </summary>
    public void StartLine(int line)
    {
        _line = line;
        var active = ActiveHeight;
        if (line == 0)
        {
            _vscrollLatch = _registers[9];
        }

        if (line < active)
        {
            DrawLine(line);
        }

        if (line <= active)
        {
            if (--_lineCounter < 0)
            {
                _lineCounter = _registers[10];
                _lineInterruptPending = true;
            }
        }
        else
        {
            _lineCounter = _registers[10];
        }

        if (line == active + 1)
        {
            _status |= 0x80;
            FillBorders(active);
            FrameCount++;
        }
    }

    // ---------------------------------------------------------------- drawing

    private int TopBorder(int active) => (Height - active) / 2;

    private ushort Backdrop => IsMode4
        ? _cram[16 + (_registers[7] & 0x0F)]
        : (ushort)(TmsColorBase + (_registers[7] & 0x0F));

    private void FillBorders(int active)
    {
        var top = TopBorder(active);
        var backdrop = Backdrop;
        _frame.AsSpan(0, top * Width).Fill(backdrop);
        _frame.AsSpan((top + active) * Width).Fill(backdrop);
    }

    private void DrawLine(int line)
    {
        var row = _frame.AsSpan((TopBorder(ActiveHeight) + line) * Width, Width);
        if ((_registers[1] & 0x40) == 0)
        {
            row.Fill(Backdrop); // display off
            return;
        }

        if (IsMode4)
        {
            DrawMode4(line, row);
        }
        else
        {
            DrawTms(line, row);
        }
    }

    private void DrawMode4(int line, Span<ushort> row)
    {
        var active = ActiveHeight;
        var nameBase = active == 224 ? ((_registers[2] & 0x0C) << 10) | 0x0700 : (_registers[2] & 0x0E) << 10;
        var wrap = active == 224 ? 256 : 224;
        var hscroll = (_registers[0] & 0x40) != 0 && line < 16 ? 0 : _registers[8];
        var lockRight = (_registers[0] & 0x80) != 0;

        for (var x = 0; x < Width; x++)
        {
            var sourceX = (x - hscroll) & 0xFF;
            var sourceY = lockRight && x >= 192 ? line : (line + _vscrollLatch) % wrap;
            var entryAddress = nameBase + ((sourceY >> 3) * 32 + (sourceX >> 3)) * 2;
            var entry = _vram[entryAddress & 0x3FFF] | _vram[(entryAddress + 1) & 0x3FFF] << 8;
            var tile = entry & 0x1FF;
            var py = (entry & 0x400) != 0 ? 7 - (sourceY & 7) : sourceY & 7;
            var px = sourceX & 7;
            var bit = (entry & 0x200) != 0 ? px : 7 - px;
            var color = PlanarPixel(tile * 32 + py * 4, bit);
            _bgLine[x] = (byte)(color | ((entry & 0x800) != 0 ? 16 : 0));
            _bgPriority[x] = (entry & 0x1000) != 0 && color != 0;
        }

        DrawMode4Sprites(line);

        var blankLeft = (_registers[0] & 0x20) != 0;
        var backdrop = Backdrop;
        for (var x = 0; x < Width; x++)
        {
            if (blankLeft && x < 8)
            {
                row[x] = backdrop;
                continue;
            }

            var sprite = _spriteLine[x];
            row[x] = sprite != 0 && !_bgPriority[x] ? _cram[16 + sprite] : _cram[_bgLine[x]];
        }
    }

    private int PlanarPixel(int address, int bit) =>
        ((_vram[address & 0x3FFF] >> bit) & 1)
        | ((_vram[(address + 1) & 0x3FFF] >> bit) & 1) << 1
        | ((_vram[(address + 2) & 0x3FFF] >> bit) & 1) << 2
        | ((_vram[(address + 3) & 0x3FFF] >> bit) & 1) << 3;

    private void DrawMode4Sprites(int line)
    {
        Array.Clear(_spriteLine);
        var sat = (_registers[5] & 0x7E) << 7;
        var tall = (_registers[1] & 0x02) != 0;
        var zoom = (_registers[1] & 0x01) != 0;
        var height = (tall ? 16 : 8) * (zoom ? 2 : 1);
        var count = 0;
        for (var i = 0; i < 64; i++)
        {
            int y = _vram[sat + i];
            if (y == 0xD0 && ActiveHeight == 192)
            {
                break;
            }

            y++;
            if (y > 240)
            {
                y -= 256;
            }

            if (line < y || line >= y + height)
            {
                continue;
            }

            if (++count > 8)
            {
                _status |= 0x40; // sprite overflow
                break;
            }

            var x = _vram[sat + 0x80 + i * 2] - ((_registers[0] & 0x08) != 0 ? 8 : 0);
            int tile = _vram[sat + 0x81 + i * 2];
            if ((_registers[6] & 0x04) != 0)
            {
                tile |= 0x100;
            }

            if (tall)
            {
                tile &= ~1;
            }

            var spriteRow = (line - y) >> (zoom ? 1 : 0);
            var address = tile * 32 + spriteRow * 4;
            for (var px = 0; px < 8; px++)
            {
                var color = PlanarPixel(address, 7 - px);
                if (color == 0)
                {
                    continue;
                }

                for (var repeat = 0; repeat < (zoom ? 2 : 1); repeat++)
                {
                    var screenX = x + px * (zoom ? 2 : 1) + repeat;
                    if (screenX is < 0 or >= Width)
                    {
                        continue;
                    }

                    if (_spriteLine[screenX] != 0)
                    {
                        _status |= 0x20; // collision
                    }
                    else
                    {
                        _spriteLine[screenX] = (byte)color;
                    }
                }
            }
        }
    }

    private void DrawTms(int line, Span<ushort> row)
    {
        var m1 = (_registers[1] & 0x10) != 0; // Text
        var m2 = (_registers[1] & 0x08) != 0; // Multicolor
        var m3 = (_registers[0] & 0x02) != 0; // Graphics II
        var backdrop = _registers[7] & 0x0F;
        var nameBase = (_registers[2] & 0x0F) << 10;

        if (m1)
        {
            // Text: 40 columns of 6 pixels, between 8-pixel borders in the background colour.
            var fg = _registers[7] >> 4;
            var patternBase = (_registers[4] & 0x07) << 11;
            for (var x = 0; x < Width; x++)
            {
                var color = backdrop;
                var tx = x - 8;
                if (tx is >= 0 and < 240)
                {
                    var name = _vram[(nameBase + (line >> 3) * 40 + tx / 6) & 0x3FFF];
                    var pattern = _vram[(patternBase + name * 8 + (line & 7)) & 0x3FFF];
                    if ((pattern & (0x80 >> (tx % 6))) != 0)
                    {
                        color = fg;
                    }
                }

                row[x] = TmsColor(color, backdrop);
            }

            return; // no sprites in Text mode
        }

        for (var x = 0; x < Width; x++)
        {
            var name = _vram[(nameBase + (line >> 3) * 32 + (x >> 3)) & 0x3FFF];
            int color;
            if (m2)
            {
                var patternBase = (_registers[4] & 0x07) << 11;
                var pattern = _vram[(patternBase + name * 8 + ((line >> 3) & 3) * 2 + ((line & 7) >> 2)) & 0x3FFF];
                color = (x & 7) < 4 ? pattern >> 4 : pattern & 0x0F;
            }
            else
            {
                int patternAddress;
                int colorByte;
                if (m3)
                {
                    var index = name + (line >> 6) * 256;
                    patternAddress = ((_registers[4] & 0x04) << 11) + (index & ((_registers[4] & 0x03) << 8 | 0xFF)) * 8 + (line & 7);
                    colorByte = _vram[(((_registers[3] & 0x80) << 6) + (index & ((_registers[3] & 0x7F) << 3 | 0x07)) * 8 + (line & 7)) & 0x3FFF];
                }
                else
                {
                    patternAddress = ((_registers[4] & 0x07) << 11) + name * 8 + (line & 7);
                    colorByte = _vram[((_registers[3] << 6) + (name >> 3)) & 0x3FFF];
                }

                var on = (_vram[patternAddress & 0x3FFF] & (0x80 >> (x & 7))) != 0;
                color = on ? colorByte >> 4 : colorByte & 0x0F;
            }

            row[x] = TmsColor(color, backdrop);
        }

        DrawTmsSprites(line, row, backdrop);
    }

    private static ushort TmsColor(int color, int backdrop) => (ushort)(TmsColorBase + (color == 0 ? backdrop : color));

    private void DrawTmsSprites(int line, Span<ushort> row, int backdrop)
    {
        Array.Clear(_spriteLine);
        var sat = (_registers[5] & 0x7F) << 7;
        var patternBase = (_registers[6] & 0x07) << 11;
        var large = (_registers[1] & 0x02) != 0;
        var magnify = (_registers[1] & 0x01) != 0;
        var size = (large ? 16 : 8) * (magnify ? 2 : 1);
        var count = 0;
        for (var i = 0; i < 32; i++)
        {
            int y = _vram[(sat + i * 4) & 0x3FFF];
            if (y == 0xD0)
            {
                break;
            }

            y++;
            if (y > 0xE0)
            {
                y -= 256;
            }

            if (line < y || line >= y + size)
            {
                continue;
            }

            if (++count > 4)
            {
                if ((_status & 0x40) == 0)
                {
                    _status = (byte)((_status & 0xE0) | 0x40 | i); // fifth sprite and its number
                }

                break;
            }

            int x = _vram[(sat + i * 4 + 1) & 0x3FFF];
            int name = _vram[(sat + i * 4 + 2) & 0x3FFF];
            var attributes = _vram[(sat + i * 4 + 3) & 0x3FFF];
            if ((attributes & 0x80) != 0)
            {
                x -= 32; // early clock
            }

            if (large)
            {
                name &= 0xFC;
            }

            var spriteRow = (line - y) >> (magnify ? 1 : 0);
            var width = large ? 16 : 8;
            for (var px = 0; px < width; px++)
            {
                var address = patternBase + name * 8 + spriteRow + (px >= 8 ? 16 : 0);
                if ((_vram[address & 0x3FFF] & (0x80 >> (px & 7))) == 0)
                {
                    continue;
                }

                for (var repeat = 0; repeat < (magnify ? 2 : 1); repeat++)
                {
                    var screenX = x + px * (magnify ? 2 : 1) + repeat;
                    if (screenX is < 0 or >= Width)
                    {
                        continue;
                    }

                    if (_spriteLine[screenX] != 0)
                    {
                        _status |= 0x20;
                        continue;
                    }

                    _spriteLine[screenX] = 1;
                    var color = attributes & 0x0F;
                    if (color != 0)
                    {
                        row[screenX] = TmsColor(color, backdrop);
                    }
                }
            }
        }
    }

    private static uint[] BuildPalette()
    {
        var palette = new uint[TmsColorBase + 16];
        for (var c = 0; c < 64; c++)
        {
            var r = (c & 3) * 85;
            var g = ((c >> 2) & 3) * 85;
            var b = ((c >> 4) & 3) * 85;
            palette[c] = 0xFF000000u | (uint)(r << 16 | g << 8 | b);
        }

        uint[] tms =
        [
            0x000000, 0x000000, 0x21C842, 0x5EDC78, 0x5455ED, 0x7D76FC, 0xD4524D, 0x42EBF5,
            0xFC5554, 0xFF7978, 0xD4C154, 0xE6CE80, 0x21B03B, 0xC95BBA, 0xCCCCCC, 0xFFFFFF,
        ];
        for (var i = 0; i < 16; i++)
        {
            palette[TmsColorBase + i] = 0xFF000000u | tms[i];
        }

        return palette;
    }

    // ---------------------------------------------------------------- state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_vram);
        writer.WriteBytes(_cram);
        writer.WriteBytes(_registers);
        writer.WriteUInt16(_address);
        writer.WriteByte((byte)_code);
        writer.WriteBool(_latched);
        writer.WriteByte(_readBuffer);
        writer.WriteByte(_status);
        writer.WriteBool(_lineInterruptPending);
        writer.WriteInt32(_lineCounter);
        writer.WriteByte(_vscrollLatch);
        writer.WriteInt32(_line);
        writer.WriteInt64(FrameCount);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_vram);
        reader.ReadBytes(_cram);
        reader.ReadBytes(_registers);
        _address = reader.ReadUInt16();
        _code = reader.ReadByte();
        _latched = reader.ReadBool();
        _readBuffer = reader.ReadByte();
        _status = reader.ReadByte();
        _lineInterruptPending = reader.ReadBool();
        _lineCounter = reader.ReadInt32();
        _vscrollLatch = reader.ReadByte();
        _line = reader.ReadInt32();
        FrameCount = reader.ReadInt64();
        reader.EndChunk();
    }
}
