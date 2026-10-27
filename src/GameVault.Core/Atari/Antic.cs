using GameVault.Core.State;

namespace GameVault.Core.Atari;

/// <summary>The memory ANTIC reads by DMA: the display list, the screen, the character sets and the player graphics.</summary>
public interface IAnticBus
{
    byte DmaRead(ushort address);
}

/// <summary>
/// ANTIC, the Atari 5200's (and 400/800's) display processor (phase-19.md, milestone 2): it runs the display list,
/// fetches the screen and character data by DMA (stealing the CPU's cycles), feeds GTIA the playfield and the player
/// and missile graphics, holds the CPU on WSYNC, and raises the display-list and vertical-blank NMIs.
/// </summary>
/// <remarks>
/// Timing follows the Altirra Hardware Reference Manual: 114 machine cycles a line, 262 lines a frame (NTSC), the
/// display list running from line 8 to 247, vertical blank from line 248, NMIs at cycle 7, WSYNC releasing the CPU at
/// cycle 105. The playfield is built a line at a time when the line starts; DMA's stolen cycles sit near where the
/// hardware takes them (missiles at 0, the display list at 1, players at 2–5, the LMS address at 6–7, refresh from 25,
/// the playfield across the visible line).
/// </remarks>
public sealed class Antic : IStateful
{
    public const int CyclesPerLine = 114;
    public const int NtscLines = 262;
    public const int FirstDisplayLine = 8;
    public const int VblankLine = 248;

    /// <summary>The colour clocks in a line (two a machine cycle).</summary>
    public const int Clocks = 228;

    private const int WsyncRelease = 105;

    private static readonly StateTag Tag = StateTag.From("ANTC");

    // Scan lines per mode line, by mode (0 and 1 are the blank and jump instructions).
    private static readonly byte[] ModeLines = [1, 1, 8, 10, 8, 16, 8, 16, 8, 4, 4, 2, 1, 2, 1, 1];

    // The playfield window's colour clocks by width (none, narrow, normal, wide).
    private static readonly int[] WindowStart = [0, 64, 48, 32];
    private static readonly int[] WindowEnd = [0, 192, 208, 224];

    private readonly IAnticBus _bus;
    private readonly Gtia _gtia;
    private readonly bool[] _busy = new bool[CyclesPerLine];
    private readonly byte[] _data = new byte[48];

    private byte _dmactl;
    private byte _chactl;
    private byte _hscrol;
    private byte _vscrol;
    private byte _pmbase;
    private byte _chbase;
    private byte _nmien;
    private byte _nmist;
    private ushort _dlist;
    private ushort _memScan;
    private int _line;
    private int _cycle;
    private bool _wsync;
    private int _ir;
    private int _row;
    private int _endRow;
    private bool _needInstruction = true;
    private bool _prevVs;
    private bool _jvbWait;
    private bool _dli;
    private int _dataCount;

    public Antic(IAnticBus bus, Gtia gtia)
    {
        _bus = bus;
        _gtia = gtia;
    }

    /// <summary>Per colour clock of the current line: 0 the background, 1–4 PF0–PF3 (PF2 across a hi-res line's window).</summary>
    public byte[] Playfield { get; } = new byte[Clocks];

    /// <summary>A hi-res line's two half-clock pixels per colour clock (bit 1 the left one, bit 0 the right).</summary>
    public byte[] Hires { get; } = new byte[Clocks];

    /// <summary>The current line is mode 2, 3 or F, whose pixels are half a colour clock wide.</summary>
    public bool HiresLine { get; private set; }

    public int Line => _line;

    public int Cycle => _cycle;

    /// <summary>The CPU can't use the bus this cycle: ANTIC's DMA has it, or WSYNC holds the CPU until the line's end.</summary>
    public bool Halted => _wsync || _busy[_cycle];

    /// <summary>A frame finished (the beam went back to line 0); the console clears it.</summary>
    public bool FrameDone { get; set; }

    /// <summary>An NMI is waiting for the CPU; the console takes it.</summary>
    public bool NmiRequest { get; set; }

    public void Power()
    {
        _dmactl = _chactl = _hscrol = _vscrol = _pmbase = _chbase = _nmien = 0;
        _nmist = 0x1F;
        _dlist = _memScan = 0;
        _line = _cycle = 0;
        _wsync = false;
        _ir = _row = _endRow = 0;
        _needInstruction = true;
        _prevVs = _jvbWait = _dli = false;
        _dataCount = 0;
        FrameDone = NmiRequest = false;
        StartLine();
    }

    /// <summary>One machine cycle.</summary>
    public void Tick()
    {
        if (_cycle == 7)
        {
            if (_line == VblankLine)
            {
                _nmist = (byte)((_nmist | 0x40) & ~0x80);
                NmiRequest |= (_nmien & 0x40) != 0;
            }
            else if (_dli)
            {
                _nmist = (byte)((_nmist | 0x80) & ~0x40);
                NmiRequest |= (_nmien & 0x80) != 0;
            }
        }
        else if (_cycle == WsyncRelease - 1)
        {
            _wsync = false;
        }

        if (++_cycle == CyclesPerLine)
        {
            _gtia.EndLine();
            _cycle = 0;
            if (++_line == NtscLines)
            {
                _line = 0;
                FrameDone = true;
            }

            StartLine();
        }
    }

    public byte Read(int address) => (address & 0x0F) switch
    {
        0x0B => (byte)(_line >> 1), // VCOUNT
        0x0C or 0x0D => 0xFF, // PENH, PENV: no light pen on the 5200
        0x0F => _nmist,
        _ => 0xFF,
    };

    public void Write(int address, byte value)
    {
        switch (address & 0x0F)
        {
            case 0x00:
                _dmactl = value;
                break;
            case 0x01:
                _chactl = value;
                break;
            case 0x02:
                _dlist = (ushort)((_dlist & 0xFF00) | value);
                break;
            case 0x03:
                _dlist = (ushort)((_dlist & 0x00FF) | (value << 8));
                break;
            case 0x04:
                _hscrol = (byte)(value & 0x0F);
                break;
            case 0x05:
                _vscrol = (byte)(value & 0x0F);
                break;
            case 0x07:
                _pmbase = value;
                break;
            case 0x09:
                _chbase = value;
                break;
            case 0x0A:
                _wsync = true;
                break;
            case 0x0E:
                _nmien = value;
                break;
            case 0x0F:
                _nmist = 0x1F; // NMIRES
                break;
        }
    }

    // ---- A line ----

    private void StartLine()
    {
        Array.Clear(_busy);
        _dli = false;
        HiresLine = false;
        Array.Clear(Playfield);
        if (_line == VblankLine)
        {
            _jvbWait = false;
        }

        var display = _line >= FirstDisplayLine && _line < VblankLine;
        if (display)
        {
            PlayerMissileDma();
            DisplayList();
        }

        for (var i = 0; i < 9; i++)
        {
            Steal(25 + i * 4); // memory refresh
        }

        _gtia.BeginLine(display ? _line - FirstDisplayLine : -1, this);
    }

    private void Steal(int cycle)
    {
        while (cycle < CyclesPerLine - 1 && _busy[cycle])
        {
            cycle++;
        }

        _busy[Math.Min(cycle, CyclesPerLine - 1)] = true;
    }

    private void PlayerMissileDma()
    {
        if ((_dmactl & 0x0C) == 0)
        {
            return;
        }

        var single = (_dmactl & 0x10) != 0;
        var odd = (_line & 1) != 0;
        int missiles, players, step;
        if (single)
        {
            var b = (_pmbase & 0xF8) << 8;
            missiles = b + 0x300 + _line;
            players = b + 0x400 + _line;
            step = 0x100;
        }
        else
        {
            var b = (_pmbase & 0xFC) << 8;
            missiles = b + 0x180 + (_line >> 1);
            players = b + 0x200 + (_line >> 1);
            step = 0x80;
        }

        // Player DMA brings the missiles' with it.
        _busy[0] = true;
        _gtia.DmaMissiles(_bus.DmaRead((ushort)missiles), single, odd);
        if ((_dmactl & 0x08) != 0)
        {
            for (var n = 0; n < 4; n++)
            {
                _busy[2 + n] = true;
                _gtia.DmaPlayer(n, _bus.DmaRead((ushort)(players + n * step)), single, odd);
            }
        }
    }

    private byte DlRead()
    {
        var value = _bus.DmaRead(_dlist);
        _dlist = (ushort)((_dlist & 0xFC00) | ((_dlist + 1) & 0x03FF)); // the counter wraps inside its 1K
        return value;
    }

    private void DisplayList()
    {
        if (_line == FirstDisplayLine)
        {
            _needInstruction = true;
            _prevVs = false;
        }

        if (_jvbWait)
        {
            // Waiting for vertical blank after a JVB: a blank line each line, with its DLI each line.
            _dli = (_ir & 0x80) != 0;
            return;
        }

        var firstRow = false;
        if (_needInstruction)
        {
            if ((_dmactl & 0x20) == 0)
            {
                return; // no display-list DMA: blank lines
            }

            Instruction();
            firstRow = true;
        }

        var mode = _ir & 0x0F;
        if (mode >= 2)
        {
            Row(mode, firstRow);
        }

        if (_row == _endRow)
        {
            _dli = (_ir & 0x80) != 0;
            _needInstruction = true;
        }
        else
        {
            _row = (_row + 1) & 0x0F;
        }
    }

    private void Instruction()
    {
        _busy[1] = true;
        _ir = DlRead();
        _needInstruction = false;
        var mode = _ir & 0x0F;
        if (mode == 0)
        {
            _row = 0;
            _endRow = (_ir >> 4) & 7;
            _prevVs = false;
            return;
        }

        if (mode == 1)
        {
            _busy[6] = _busy[7] = true;
            var low = DlRead();
            _dlist = (ushort)(low | (DlRead() << 8));
            _jvbWait = (_ir & 0x40) != 0;
            _row = _endRow = 0;
            _prevVs = false;
            return;
        }

        if ((_ir & 0x40) != 0)
        {
            _busy[6] = _busy[7] = true;
            var low = DlRead();
            _memScan = (ushort)(low | (DlRead() << 8));
        }

        var vs = (_ir & 0x20) != 0;
        _row = vs && !_prevVs ? _vscrol : 0;
        _endRow = !vs && _prevVs ? _vscrol : ModeLines[mode] - 1;
        _prevVs = vs;
    }

    /// <summary>One scan line of a mode line: fetches its screen data on the first, then draws the playfield codes.</summary>
    private void Row(int mode, bool firstRow)
    {
        var width = _dmactl & 3;
        if (width == 0)
        {
            return;
        }

        var hs = (_ir & 0x10) != 0;
        var fetchWidth = hs && width < 3 ? width + 1 : width;
        var bytesPerClock = mode switch
        {
            2 or 3 or 4 or 5 or 0x0D or 0x0E or 0x0F => 4, // 40 bytes on a normal line
            6 or 7 or 0x0A or 0x0B or 0x0C => 8, // 20
            _ => 16, // 8 and 9: 10
        };
        var count = (WindowEnd[fetchWidth] - WindowStart[fetchWidth]) / bytesPerClock;
        var dmaStart = WindowStart[fetchWidth] / 2 + 2;
        var cyclesPerByte = bytesPerClock / 2;
        var character = mode <= 7;
        if (firstRow)
        {
            for (var i = 0; i < count; i++)
            {
                _data[i] = _bus.DmaRead(_memScan);
                _memScan = (ushort)((_memScan & 0xF000) | ((_memScan + 1) & 0x0FFF)); // wraps inside its 4K
                Steal(dmaStart + i * cyclesPerByte);
            }

            _dataCount = count;
        }

        count = Math.Min(count, _dataCount);
        if (character)
        {
            for (var i = 0; i < count; i++)
            {
                Steal(dmaStart + i * cyclesPerByte + 1); // the character data, every row
            }
        }

        var start = WindowStart[fetchWidth] + (hs ? _hscrol : 0);
        var windowStart = WindowStart[width];
        var windowEnd = WindowEnd[width];
        HiresLine = mode is 2 or 3 or 0x0F;
        if (HiresLine)
        {
            for (var x = windowStart; x < windowEnd; x++)
            {
                Playfield[x] = 3;
                Hires[x] = 0;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var x0 = start + i * bytesPerClock;
            var b = _data[i];
            switch (mode)
            {
                case 2:
                case 3:
                case 0x0F:
                    var bits = mode == 0x0F ? b : HiresGlyph(mode, b);
                    for (var k = 0; k < 4; k++)
                    {
                        var x = x0 + k;
                        if (x >= windowStart && x < windowEnd)
                        {
                            Hires[x] = (byte)((bits >> (6 - 2 * k)) & 3);
                        }
                    }

                    break;
                case 4:
                case 5:
                {
                    var glyph = Glyph40(b, mode == 5 ? _row >> 1 : _row);
                    for (var k = 0; k < 4; k++)
                    {
                        var v = (glyph >> (6 - 2 * k)) & 3;
                        Put(x0 + k, 1, v == 3 && (b & 0x80) != 0 ? 4 : v, windowStart, windowEnd);
                    }

                    break;
                }

                case 6:
                case 7:
                {
                    var r = Reflect(mode == 7 ? _row >> 1 : _row);
                    var glyph = _bus.DmaRead((ushort)(((_chbase & 0xFE) << 8) | ((b & 0x3F) << 3) | r));
                    var code = (b >> 6) + 1;
                    for (var k = 0; k < 8; k++)
                    {
                        Put(x0 + k, 1, ((glyph >> (7 - k)) & 1) != 0 ? code : 0, windowStart, windowEnd);
                    }

                    break;
                }

                case 8:
                case 0x0A:
                case 0x0D:
                case 0x0E:
                {
                    var size = bytesPerClock / 4;
                    for (var k = 0; k < 4; k++)
                    {
                        Put(x0 + k * size, size, (b >> (6 - 2 * k)) & 3, windowStart, windowEnd);
                    }

                    break;
                }

                default: // 9, B and C: one bit a pixel, PF0 or the background
                {
                    var size = bytesPerClock / 8;
                    for (var k = 0; k < 8; k++)
                    {
                        Put(x0 + k * size, size, (b >> (7 - k)) & 1, windowStart, windowEnd);
                    }

                    break;
                }
            }
        }
    }

    private void Put(int x, int size, int code, int windowStart, int windowEnd)
    {
        for (var i = 0; i < size; i++, x++)
        {
            if (x >= windowStart && x < windowEnd)
            {
                Playfield[x] = (byte)code;
            }
        }
    }

    private int Reflect(int row) => (_chactl & 4) != 0 ? 7 - (row & 7) : row & 7;

    private byte Glyph40(byte name, int row) =>
        _bus.DmaRead((ushort)(((_chbase & 0xFC) << 8) | ((name & 0x7F) << 3) | Reflect(row)));

    /// <summary>
    /// A mode 2 or 3 character's row: mode 3's lower-case characters ($60–$7F) start two lines down and wrap their top two
    /// rows into lines 8 and 9; bit 7 blanks and/or inverts the character as CHACTL says.
    /// </summary>
    private int HiresGlyph(int mode, byte name)
    {
        int glyph;
        if (mode == 3)
        {
            var descender = (name & 0x60) == 0x60;
            var row = _row % 10;
            glyph = descender
                ? row < 2 ? 0 : Glyph40(name, row & 7)
                : row < 8 ? Glyph40(name, row) : 0;
        }
        else
        {
            glyph = Glyph40(name, _row);
        }

        if ((name & 0x80) != 0)
        {
            if ((_chactl & 1) != 0)
            {
                glyph = 0;
            }

            if ((_chactl & 2) != 0)
            {
                glyph ^= 0xFF;
            }
        }

        return glyph;
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteByte(_dmactl);
        writer.WriteByte(_chactl);
        writer.WriteByte(_hscrol);
        writer.WriteByte(_vscrol);
        writer.WriteByte(_pmbase);
        writer.WriteByte(_chbase);
        writer.WriteByte(_nmien);
        writer.WriteByte(_nmist);
        writer.WriteUInt16(_dlist);
        writer.WriteUInt16(_memScan);
        writer.WriteInt32(_line);
        writer.WriteInt32(_cycle);
        writer.WriteBool(_wsync);
        writer.WriteInt32(_ir);
        writer.WriteInt32(_row);
        writer.WriteInt32(_endRow);
        writer.WriteBool(_needInstruction);
        writer.WriteBool(_prevVs);
        writer.WriteBool(_jvbWait);
        writer.WriteBool(_dli);
        writer.WriteInt32(_dataCount);
        writer.WriteBytes(_data);
        writer.WriteBool(NmiRequest);
        writer.WriteBool(HiresLine);
        writer.WriteBytes(Playfield);
        writer.WriteBytes(Hires);
        for (var i = 0; i < CyclesPerLine; i++)
        {
            writer.WriteBool(_busy[i]);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _dmactl = reader.ReadByte();
        _chactl = reader.ReadByte();
        _hscrol = (byte)(reader.ReadByte() & 0x0F);
        _vscrol = (byte)(reader.ReadByte() & 0x0F);
        _pmbase = reader.ReadByte();
        _chbase = reader.ReadByte();
        _nmien = reader.ReadByte();
        _nmist = reader.ReadByte();
        _dlist = reader.ReadUInt16();
        _memScan = reader.ReadUInt16();
        _line = Math.Clamp(reader.ReadInt32(), 0, NtscLines - 1);
        _cycle = Math.Clamp(reader.ReadInt32(), 0, CyclesPerLine - 1);
        _wsync = reader.ReadBool();
        _ir = reader.ReadInt32() & 0xFF;
        _row = reader.ReadInt32() & 0x0F;
        _endRow = reader.ReadInt32() & 0x0F;
        _needInstruction = reader.ReadBool();
        _prevVs = reader.ReadBool();
        _jvbWait = reader.ReadBool();
        _dli = reader.ReadBool();
        _dataCount = Math.Clamp(reader.ReadInt32(), 0, _data.Length);
        reader.ReadBytes(_data);
        NmiRequest = reader.ReadBool();
        HiresLine = reader.ReadBool();
        reader.ReadBytes(Playfield);
        reader.ReadBytes(Hires);
        for (var i = 0; i < CyclesPerLine; i++)
        {
            _busy[i] = reader.ReadBool();
        }

        reader.EndChunk();
    }
}
