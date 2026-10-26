using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The S-PPU (SPEC §7.11, Phase 14): background modes 0–7 (2/4/8-bit tiles, 8×8 or 16×16, mosaic, offset-per-tile,
/// the hi-res modes 5 and 6, mode 7 with its matrix, EXTBG and direct colour), 128 sprites with the 32-per-line and
/// 34-sliver limits, two windows with their logic, main and sub screens with colour math, the fixed colour,
/// brightness and forced blank. Each visible line is drawn when it starts.
/// </summary>
/// <remarks>
/// The frame holds 15-bit colours (BGR555) after brightness, which index <see cref="Palette"/> directly. It is 256
/// wide, or 512 when a hi-res line appears. References: fullsnes ("SNES PPU"); anomie's register docs; the bsnes
/// accuracy core's documented behaviour (scroll latches, mode 7 arithmetic, VRAM address remapping).
/// </remarks>
public sealed partial class SnesPpu : IStateful
{
    public const int MaxWidth = 512;
    public const int MaxHeight = 239;

    private static readonly StateTag Tag = StateTag.From("SPPU");
    private static readonly uint[] PaletteTable = BuildPalette();

    private readonly ushort[] _vram = new ushort[0x8000];
    private readonly ushort[] _cgram = new ushort[256];
    private readonly byte[] _oam = new byte[544];
    private readonly ushort[] _frame = new ushort[MaxWidth * MaxHeight];
    private readonly ushort[] _line = new ushort[MaxWidth];
    private readonly bool[] _hiresLine = new bool[MaxHeight];

    // Registers
    private byte _inidisp = 0x80;
    private byte _obsel;
    private ushort _oamAddressReload;
    private int _oamAddress;
    private byte _oamLatch;
    private bool _oamPriorityRotation;
    private byte _bgMode;
    private byte _mosaic;
    private readonly byte[] _bgScreen = new byte[4];
    private readonly byte[] _bgChar = new byte[4];
    private readonly ushort[] _hofs = new ushort[4];
    private readonly ushort[] _vofs = new ushort[4];
    private byte _scrollLatch;
    private byte _scrollLatchH;
    private byte _vmain;
    private ushort _vramAddress;
    private ushort _vramReadLatch;
    private byte _m7sel;
    private short _m7a, _m7b, _m7c, _m7d, _m7x, _m7y, _m7hofs, _m7vofs;
    private byte _m7latch;
    private byte _cgAddress;
    private bool _cgHigh;
    private byte _cgLatch;
    private bool _cgReadHigh;
    private byte _w12sel, _w34sel, _wobjsel, _wh0, _wh1, _wh2, _wh3, _wbglog, _wobjlog;
    private byte _tm, _ts, _tmw, _tsw, _cgwsel, _cgadsub;
    private ushort _fixedColor;
    private byte _setini;
    private ushort _hCounterLatch, _vCounterLatch;
    private bool _hCounterHigh, _vCounterHigh;
    private bool _counterLatched;
    private bool _rangeOver, _timeOver;
    private int _frameWidth = 256;
    private int _frameHeight = 224;
    private bool _anyHires;
    private bool _oddField;

    public SnesPpu(bool pal) => Pal = pal;

    public bool Pal { get; }

    /// <summary>Every BGR555 colour as ARGB (the frame holds BGR555 values).</summary>
    public static ReadOnlySpan<uint> Palette => PaletteTable;

    public ReadOnlySpan<ushort> Frame => _frame.AsSpan(0, _frameWidth * _frameHeight);

    public int FrameWidth => _frameWidth;

    public int FrameHeight => _frameHeight;

    /// <summary>239 visible lines instead of 224 (SETINI bit 2).</summary>
    public bool Overscan => (_setini & 0x04) != 0;

    public bool ForcedBlank => (_inidisp & 0x80) != 0;

    public ReadOnlySpan<ushort> Vram => _vram;

    public ReadOnlySpan<ushort> Cgram => _cgram;

    public ReadOnlySpan<byte> Oam => _oam;

    public void Power()
    {
        Array.Clear(_vram);
        Array.Clear(_cgram);
        Array.Clear(_oam);
        Array.Clear(_frame);
        _inidisp = 0x80;
        _obsel = 0;
        _oamAddressReload = 0;
        _oamAddress = 0;
        _bgMode = 0;
        _mosaic = 0;
        Array.Clear(_bgScreen);
        Array.Clear(_bgChar);
        Array.Clear(_hofs);
        Array.Clear(_vofs);
        _vmain = 0;
        _vramAddress = 0;
        _m7sel = 0;
        _m7a = _m7b = _m7c = _m7d = _m7x = _m7y = _m7hofs = _m7vofs = 0;
        _cgAddress = 0;
        _cgHigh = false;
        _w12sel = _w34sel = _wobjsel = _wh0 = _wh1 = _wh2 = _wh3 = _wbglog = _wobjlog = 0;
        _tm = _ts = _tmw = _tsw = _cgwsel = _cgadsub = 0;
        _fixedColor = 0;
        _setini = 0;
        _counterLatched = false;
        _rangeOver = _timeOver = false;
        _frameWidth = 256;
        _frameHeight = 224;
    }

    // ---- Frame timing (the console calls these) ----

    /// <summary>The start of a frame: the frame's height follows the overscan setting.</summary>
    public void BeginFrame()
    {
        _frameHeight = Overscan ? 239 : 224;
        _anyHires = false;
        _oddField = !_oddField;
        if (!ForcedBlank)
        {
            _rangeOver = _timeOver = false;
        }
    }

    /// <summary>
    /// Draws visible line <paramref name="y"/> (0-based; the PPU's line y + 1). Hi-res lines are 512 wide; once one
    /// appears, the whole frame is 512 wide and the 256-wide lines are doubled.
    /// </summary>
    public void RenderLine(int y)
    {
        if (y >= _frameHeight)
        {
            return;
        }

        var hires = RenderToLine(y);
        _hiresLine[y] = hires;
        if (hires && !_anyHires)
        {
            _anyHires = true;
        }
    }

    /// <summary>The OAM address reloads at the start of the vertical blank (unless the screen is off).</summary>
    public void BeginVBlank()
    {
        if (!ForcedBlank)
        {
            _oamAddress = (_oamAddressReload & 0x1FF) << 1;
        }
    }

    public void LatchCounters(int dot, int line)
    {
        _hCounterLatch = (ushort)dot;
        _vCounterLatch = (ushort)line;
        _counterLatched = true;
    }

    // ---- Registers ----

    public void Write(int address, byte value)
    {
        switch (address & 0xFF)
        {
            case 0x00:
                if ((_inidisp & 0x80) != 0 && (value & 0x80) == 0)
                {
                    // Leaving forced blank on the vertical blank's first line reloads the OAM address (simplified: always).
                }

                _inidisp = value;
                break;
            case 0x01: _obsel = value; break;
            case 0x02:
                _oamAddressReload = (ushort)((_oamAddressReload & 0x100) | value);
                _oamAddress = (_oamAddressReload & 0x1FF) << 1;
                break;
            case 0x03:
                _oamAddressReload = (ushort)((_oamAddressReload & 0xFF) | ((value & 1) << 8));
                _oamPriorityRotation = (value & 0x80) != 0;
                _oamAddress = (_oamAddressReload & 0x1FF) << 1;
                break;
            case 0x04: WriteOam(value); break;
            case 0x05: _bgMode = value; break;
            case 0x06: _mosaic = value; break;
            case 0x07: case 0x08: case 0x09: case 0x0A: _bgScreen[(address & 0xFF) - 7] = value; break;
            case 0x0B:
                _bgChar[0] = (byte)(value & 0x0F);
                _bgChar[1] = (byte)(value >> 4);
                break;
            case 0x0C:
                _bgChar[2] = (byte)(value & 0x0F);
                _bgChar[3] = (byte)(value >> 4);
                break;
            case 0x0D:
                _m7hofs = (short)(value << 8 | _m7latch);
                _m7latch = value;
                WriteHofs(0, value);
                break;
            case 0x0E:
                _m7vofs = (short)(value << 8 | _m7latch);
                _m7latch = value;
                WriteVofs(0, value);
                break;
            case 0x0F: WriteHofs(1, value); break;
            case 0x10: WriteVofs(1, value); break;
            case 0x11: WriteHofs(2, value); break;
            case 0x12: WriteVofs(2, value); break;
            case 0x13: WriteHofs(3, value); break;
            case 0x14: WriteVofs(3, value); break;
            case 0x15: _vmain = value; break;
            case 0x16:
                _vramAddress = (ushort)((_vramAddress & 0xFF00) | value);
                _vramReadLatch = _vram[RemapVram() & 0x7FFF];
                break;
            case 0x17:
                _vramAddress = (ushort)((_vramAddress & 0x00FF) | value << 8);
                _vramReadLatch = _vram[RemapVram() & 0x7FFF];
                break;
            case 0x18:
            {
                var at = RemapVram() & 0x7FFF;
                _vram[at] = (ushort)((_vram[at] & 0xFF00) | value);
                if ((_vmain & 0x80) == 0)
                {
                    IncrementVram();
                }

                break;
            }

            case 0x19:
            {
                var at = RemapVram() & 0x7FFF;
                _vram[at] = (ushort)((_vram[at] & 0x00FF) | value << 8);
                if ((_vmain & 0x80) != 0)
                {
                    IncrementVram();
                }

                break;
            }

            case 0x1A: _m7sel = value; break;
            case 0x1B: _m7a = M7Write(value); break;
            case 0x1C: _m7b = M7Write(value); break;
            case 0x1D: _m7c = M7Write(value); break;
            case 0x1E: _m7d = M7Write(value); break;
            case 0x1F: _m7x = M7Write(value); break;
            case 0x20: _m7y = M7Write(value); break;
            case 0x21:
                _cgAddress = value;
                _cgHigh = false;
                _cgReadHigh = false;
                break;
            case 0x22:
                if (!_cgHigh)
                {
                    _cgLatch = value;
                }
                else
                {
                    _cgram[_cgAddress] = (ushort)((value << 8 | _cgLatch) & 0x7FFF);
                    _cgAddress++;
                }

                _cgHigh = !_cgHigh;
                break;
            case 0x23: _w12sel = value; break;
            case 0x24: _w34sel = value; break;
            case 0x25: _wobjsel = value; break;
            case 0x26: _wh0 = value; break;
            case 0x27: _wh1 = value; break;
            case 0x28: _wh2 = value; break;
            case 0x29: _wh3 = value; break;
            case 0x2A: _wbglog = value; break;
            case 0x2B: _wobjlog = value; break;
            case 0x2C: _tm = value; break;
            case 0x2D: _ts = value; break;
            case 0x2E: _tmw = value; break;
            case 0x2F: _tsw = value; break;
            case 0x30: _cgwsel = value; break;
            case 0x31: _cgadsub = value; break;
            case 0x32:
            {
                // COLDATA: bits 5–7 pick which components the 5-bit intensity sets.
                var intensity = value & 0x1F;
                if ((value & 0x20) != 0)
                {
                    _fixedColor = (ushort)((_fixedColor & ~0x001F) | intensity);
                }

                if ((value & 0x40) != 0)
                {
                    _fixedColor = (ushort)((_fixedColor & ~0x03E0) | intensity << 5);
                }

                if ((value & 0x80) != 0)
                {
                    _fixedColor = (ushort)((_fixedColor & ~0x7C00) | intensity << 10);
                }

                break;
            }

            case 0x33: _setini = value; break;
        }
    }

    public byte Read(int address, byte openBus)
    {
        switch (address & 0xFF)
        {
            case 0x34: return (byte)(_m7a * (_m7b >> 8));
            case 0x35: return (byte)((_m7a * (_m7b >> 8)) >> 8);
            case 0x36: return (byte)((_m7a * (_m7b >> 8)) >> 16);
            case 0x37: return openBus; // SLHV: the console latches the counters
            case 0x38:
            {
                var value = ReadOam();
                return value;
            }

            case 0x39:
            {
                var value = (byte)_vramReadLatch;
                if ((_vmain & 0x80) == 0)
                {
                    _vramReadLatch = _vram[RemapVram() & 0x7FFF];
                    IncrementVram();
                }

                return value;
            }

            case 0x3A:
            {
                var value = (byte)(_vramReadLatch >> 8);
                if ((_vmain & 0x80) != 0)
                {
                    _vramReadLatch = _vram[RemapVram() & 0x7FFF];
                    IncrementVram();
                }

                return value;
            }

            case 0x3B:
            {
                var color = _cgram[_cgAddress];
                byte value;
                if (!_cgReadHigh)
                {
                    value = (byte)color;
                }
                else
                {
                    value = (byte)((color >> 8) | (openBus & 0x80));
                    _cgAddress++;
                }

                _cgReadHigh = !_cgReadHigh;
                return value;
            }

            case 0x3C:
            {
                var value = _hCounterHigh ? (byte)((_hCounterLatch >> 8) & 1 | (openBus & 0xFE)) : (byte)_hCounterLatch;
                _hCounterHigh = !_hCounterHigh;
                return value;
            }

            case 0x3D:
            {
                var value = _vCounterHigh ? (byte)((_vCounterLatch >> 8) & 1 | (openBus & 0xFE)) : (byte)_vCounterLatch;
                _vCounterHigh = !_vCounterHigh;
                return value;
            }

            case 0x3E:
                return (byte)((_timeOver ? 0x80 : 0) | (_rangeOver ? 0x40 : 0) | 0x01);
            case 0x3F:
            {
                var value = (byte)((_oddField ? 0x80 : 0) | (_counterLatched ? 0x40 : 0) | (Pal ? 0x10 : 0) | 0x03);
                _counterLatched = false;
                _hCounterHigh = _vCounterHigh = false;
                return value;
            }

            default:
                return openBus;
        }
    }

    private void WriteHofs(int bg, byte value)
    {
        _hofs[bg] = (ushort)((value << 8 | (_scrollLatch & ~7) | (_scrollLatchH & 7)) & 0x3FF);
        _scrollLatch = value;
        _scrollLatchH = value;
    }

    private void WriteVofs(int bg, byte value)
    {
        _vofs[bg] = (ushort)((value << 8 | _scrollLatch) & 0x3FF);
        _scrollLatch = value;
    }

    private short M7Write(byte value)
    {
        var result = (short)(value << 8 | _m7latch);
        _m7latch = value;
        return result;
    }

    private int RemapVram()
    {
        var address = _vramAddress;
        return ((_vmain >> 2) & 3) switch
        {
            1 => (address & 0xFF00) | ((address & 0x001F) << 3) | ((address >> 5) & 7),
            2 => (address & 0xFE00) | ((address & 0x003F) << 3) | ((address >> 6) & 7),
            3 => (address & 0xFC00) | ((address & 0x007F) << 3) | ((address >> 7) & 7),
            _ => address,
        };
    }

    private void IncrementVram() => _vramAddress += (_vmain & 3) switch
    {
        0 => (ushort)1,
        1 => (ushort)32,
        _ => (ushort)128,
    };

    private void WriteOam(byte value)
    {
        var address = _oamAddress & 0x3FF;
        if (address >= 0x200)
        {
            _oam[0x200 + (address & 0x1F)] = value;
        }
        else if ((address & 1) == 0)
        {
            _oamLatch = value;
        }
        else
        {
            _oam[address - 1] = _oamLatch;
            _oam[address] = value;
        }

        _oamAddress = (_oamAddress + 1) & 0x3FF;
    }

    private byte ReadOam()
    {
        var address = _oamAddress & 0x3FF;
        var value = address >= 0x200 ? _oam[0x200 + (address & 0x1F)] : _oam[address];
        _oamAddress = (_oamAddress + 1) & 0x3FF;
        return value;
    }

    private static uint[] BuildPalette()
    {
        var table = new uint[0x8000];
        for (var color = 0; color < table.Length; color++)
        {
            var r = color & 0x1F;
            var g = (color >> 5) & 0x1F;
            var b = (color >> 10) & 0x1F;
            table[color] = 0xFF000000u | (uint)(Expand(r) << 16) | (uint)(Expand(g) << 8) | (uint)Expand(b);
        }

        return table;

        static int Expand(int five) => (five << 3) | (five >> 2);
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var word in _vram)
        {
            writer.WriteUInt16(word);
        }

        foreach (var color in _cgram)
        {
            writer.WriteUInt16(color);
        }

        writer.WriteBytes(_oam);
        writer.WriteByte(_inidisp);
        writer.WriteByte(_obsel);
        writer.WriteUInt16(_oamAddressReload);
        writer.WriteInt32(_oamAddress);
        writer.WriteByte(_oamLatch);
        writer.WriteBool(_oamPriorityRotation);
        writer.WriteByte(_bgMode);
        writer.WriteByte(_mosaic);
        writer.WriteBytes(_bgScreen);
        writer.WriteBytes(_bgChar);
        for (var i = 0; i < 4; i++)
        {
            writer.WriteUInt16(_hofs[i]);
            writer.WriteUInt16(_vofs[i]);
        }

        writer.WriteByte(_scrollLatch);
        writer.WriteByte(_scrollLatchH);
        writer.WriteByte(_vmain);
        writer.WriteUInt16(_vramAddress);
        writer.WriteUInt16(_vramReadLatch);
        writer.WriteByte(_m7sel);
        foreach (var value in new[] { _m7a, _m7b, _m7c, _m7d, _m7x, _m7y, _m7hofs, _m7vofs })
        {
            writer.WriteUInt16((ushort)value);
        }

        writer.WriteByte(_m7latch);
        writer.WriteByte(_cgAddress);
        writer.WriteBool(_cgHigh);
        writer.WriteByte(_cgLatch);
        writer.WriteBool(_cgReadHigh);
        writer.WriteBytes([_w12sel, _w34sel, _wobjsel, _wh0, _wh1, _wh2, _wh3, _wbglog, _wobjlog, _tm, _ts, _tmw, _tsw, _cgwsel, _cgadsub, _setini]);
        writer.WriteUInt16(_fixedColor);
        writer.WriteUInt16(_hCounterLatch);
        writer.WriteUInt16(_vCounterLatch);
        writer.WriteBool(_hCounterHigh);
        writer.WriteBool(_vCounterHigh);
        writer.WriteBool(_counterLatched);
        writer.WriteBool(_rangeOver);
        writer.WriteBool(_timeOver);
        writer.WriteInt32(_frameWidth);
        writer.WriteInt32(_frameHeight);
        writer.WriteBool(_oddField);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _vram.Length; i++)
        {
            _vram[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _cgram.Length; i++)
        {
            _cgram[i] = reader.ReadUInt16();
        }

        reader.ReadBytes(_oam);
        _inidisp = reader.ReadByte();
        _obsel = reader.ReadByte();
        _oamAddressReload = reader.ReadUInt16();
        _oamAddress = reader.ReadInt32();
        _oamLatch = reader.ReadByte();
        _oamPriorityRotation = reader.ReadBool();
        _bgMode = reader.ReadByte();
        _mosaic = reader.ReadByte();
        reader.ReadBytes(_bgScreen);
        reader.ReadBytes(_bgChar);
        for (var i = 0; i < 4; i++)
        {
            _hofs[i] = reader.ReadUInt16();
            _vofs[i] = reader.ReadUInt16();
        }

        _scrollLatch = reader.ReadByte();
        _scrollLatchH = reader.ReadByte();
        _vmain = reader.ReadByte();
        _vramAddress = reader.ReadUInt16();
        _vramReadLatch = reader.ReadUInt16();
        _m7sel = reader.ReadByte();
        _m7a = (short)reader.ReadUInt16();
        _m7b = (short)reader.ReadUInt16();
        _m7c = (short)reader.ReadUInt16();
        _m7d = (short)reader.ReadUInt16();
        _m7x = (short)reader.ReadUInt16();
        _m7y = (short)reader.ReadUInt16();
        _m7hofs = (short)reader.ReadUInt16();
        _m7vofs = (short)reader.ReadUInt16();
        _m7latch = reader.ReadByte();
        _cgAddress = reader.ReadByte();
        _cgHigh = reader.ReadBool();
        _cgLatch = reader.ReadByte();
        _cgReadHigh = reader.ReadBool();
        Span<byte> windows = stackalloc byte[16];
        reader.ReadBytes(windows);
        (_w12sel, _w34sel, _wobjsel, _wh0, _wh1, _wh2, _wh3, _wbglog) = (windows[0], windows[1], windows[2], windows[3], windows[4], windows[5], windows[6], windows[7]);
        (_wobjlog, _tm, _ts, _tmw, _tsw, _cgwsel, _cgadsub, _setini) = (windows[8], windows[9], windows[10], windows[11], windows[12], windows[13], windows[14], windows[15]);
        _fixedColor = reader.ReadUInt16();
        _hCounterLatch = reader.ReadUInt16();
        _vCounterLatch = reader.ReadUInt16();
        _hCounterHigh = reader.ReadBool();
        _vCounterHigh = reader.ReadBool();
        _counterLatched = reader.ReadBool();
        _rangeOver = reader.ReadBool();
        _timeOver = reader.ReadBool();
        _frameWidth = reader.ReadInt32();
        _frameHeight = reader.ReadInt32();
        _oddField = reader.ReadBool();
        reader.EndChunk();
    }
}
