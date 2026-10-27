using GameVault.Core.Cpu.W65816;
using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The SA-1 (outstanding-work plan §5): a second 65C816 at 10.74 MHz on the cartridge, with 2 KB of I-RAM, the
/// battery-backed BW-RAM (also seen as a 2- or 4-bit bitmap), the Super MMC's ROM banking, a multiplier, divider and
/// sum-of-products unit, variable-length bit reads of ROM, an H/V or linear timer, DMA, and character conversion of
/// bitmaps into SNES tiles. Used by Super Mario RPG, Kirby Super Star, Kirby's Dream Land 3 and about 30 others.
/// </summary>
/// <remarks>
/// References: the SNESdev Wiki "SA-1" and "SA-1 registers" pages; the register descriptions of the SNES
/// development manual's SA-1 chapter, as summarised there. The I-RAM and BW-RAM write protection is not enforced
/// (games enable writes before using them), and DMA completes at once.
/// </remarks>
public sealed class Sa1 : ISnesCoprocessor, IW65816Bus
{
    public const int IRamSize = 0x800;

    /// <summary>Master clocks per SA-1 bus cycle (10.74 MHz); BW-RAM takes two.</summary>
    private const int CycleClocks = 2;

    private static readonly StateTag Tag = StateTag.From("SA-1");

    private readonly byte[] _rom;
    private readonly byte[] _iram = new byte[IRamSize];
    private readonly byte[] _bwram;
    private readonly int _bwramMask;
    private readonly int _lines;
    private readonly W65816Cpu _cpu;

    /// <summary>The last value written to each register, $2200–$22FF.</summary>
    private readonly byte[] _w = new byte[0x100];

    private long _clock;
    private byte _openBus;

    // Interrupt flags: to the SNES CPU, and to the SA-1.
    private bool _cpuIrqFlag;
    private bool _charDmaIrqFlag;
    private bool _sa1IrqFlag;
    private bool _timerIrqFlag;
    private bool _dmaIrqFlag;
    private bool _sa1NmiFlag;

    // The timer, in master clocks (H) and lines (V), and the counters latched by reading $2302.
    private int _hCounter;
    private int _vCounter;
    private ushort _hLatch;
    private ushort _vLatch;

    // Arithmetic.
    private ulong _result;
    private bool _overflow;

    // Variable-length bit reads.
    private int _vbrAddress;
    private int _vbrBit;

    // Character conversion.
    private bool _charConversion1;
    private int _charLine;

    public Sa1(byte[] rom, int bwramSize, bool pal)
    {
        _rom = rom;
        var size = 0x800;
        while (size < bwramSize && size < 0x40000)
        {
            size <<= 1;
        }

        _bwram = new byte[size];
        _bwramMask = size - 1;
        _lines = pal ? 312 : 262;
        _cpu = new W65816Cpu(this);
    }

    public string Name => "SA-1";

    /// <summary>The battery-backed BW-RAM (the save file).</summary>
    public Span<byte> BwRam => _bwram;

    public ReadOnlySpan<byte> IRam => _iram;

    internal W65816Cpu Cpu => _cpu;

    /// <summary>The SA-1's interrupt to the SNES CPU, and its character conversion's.</summary>
    public bool Irq => (_cpuIrqFlag && (_w[0x01] & 0x80) != 0) || (_charDmaIrqFlag && (_w[0x01] & 0x20) != 0);

    private bool Halted => (_w[0x00] & 0x60) != 0; // RESB holds it in reset; RDYB makes it wait

    public void Power()
    {
        Array.Clear(_iram);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_w);
        _w[0x00] = 0x20; // the SA-1 starts held in reset until the game sets its vectors
        for (var i = 0; i < 4; i++)
        {
            _w[0x20 + i] = (byte)i; // CXB–FXB: the first four megabytes in order
        }

        _cpuIrqFlag = _charDmaIrqFlag = _sa1IrqFlag = _timerIrqFlag = _dmaIrqFlag = _sa1NmiFlag = false;
        _hCounter = _vCounter = 0;
        _hLatch = _vLatch = 0;
        _result = 0;
        _overflow = false;
        _vbrAddress = _vbrBit = 0;
        _charConversion1 = false;
        _charLine = 0;
        _openBus = 0;
        _cpu.P = 0;
        _cpu.Irq = false;
        _cpu.Nmi = false;
        _cpu.Reset();
        _clock = 0;
    }

    // ---- The SNES CPU's side ----

    public bool Claims(uint address)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        if ((bank & 0x40) == 0)
        {
            return offset >= 0x8000 || offset is >= 0x2200 and < 0x2400 or >= 0x3000 and < 0x3800 or >= 0x6000;
        }

        return bank is >= 0x40 and < 0x50 or >= 0xC0;
    }

    public byte Read(uint address, byte openBus)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        if (bank >= 0xC0)
        {
            return _rom[HiRomIndex(bank, offset)];
        }

        if ((bank & 0x40) != 0)
        {
            if (bank >= 0x50)
            {
                return openBus;
            }

            var at = ((bank & 0x0F) << 16) | offset;
            return _charConversion1 ? CharConversion1Read(at) : _bwram[at & _bwramMask];
        }

        switch (offset)
        {
            case >= 0x8000:
                // The SNES's NMI and IRQ vectors can come from SNV and SIV instead of ROM.
                if (bank == 0x00 && offset >= 0xFFE0)
                {
                    if ((_w[0x09] & 0x10) != 0 && offset is 0xFFEA or 0xFFEB)
                    {
                        return _w[0x0C + (offset & 1)];
                    }

                    if ((_w[0x09] & 0x40) != 0 && offset is 0xFFEE or 0xFFEF)
                    {
                        return _w[0x0E + (offset & 1)];
                    }
                }

                return _rom[LoRomIndex(bank, offset)];
            case >= 0x6000:
            {
                var at = (_w[0x24] & 0x1F) * 0x2000 + (offset & 0x1FFF);
                return _charConversion1 ? CharConversion1Read(at) : _bwram[at & _bwramMask];
            }

            case >= 0x3000 and < 0x3800: return _iram[offset & 0x7FF];
            case 0x2300: return StatusForCpu();
            case >= 0x2300 and < 0x2310: return ReadSharedRegister(offset, openBus);
            default: return openBus;
        }
    }

    public void Write(uint address, byte value)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        if (bank >= 0xC0)
        {
            return;
        }

        if ((bank & 0x40) != 0)
        {
            if (bank < 0x50)
            {
                _bwram[(((bank & 0x0F) << 16) | offset) & _bwramMask] = value;
            }

            return;
        }

        switch (offset)
        {
            case >= 0x8000: return;
            case >= 0x6000: _bwram[((_w[0x24] & 0x1F) * 0x2000 + (offset & 0x1FFF)) & _bwramMask] = value; return;
            case >= 0x3000 and < 0x3800: _iram[offset & 0x7FF] = value; return;
            case >= 0x2200 and < 0x2300: WriteRegister(offset, value); return;
        }
    }

    public void CatchUp(long clock)
    {
        while (_clock < clock)
        {
            if (Halted)
            {
                Tick((int)(clock - _clock));
                return;
            }

            _cpu.Irq = (_sa1IrqFlag && (_w[0x0A] & 0x80) != 0)
                || (_timerIrqFlag && (_w[0x0A] & 0x40) != 0)
                || (_dmaIrqFlag && (_w[0x0A] & 0x20) != 0);
            _cpu.Step();
        }
    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    // ---- The SA-1's own bus ----

    byte IW65816Bus.Read(uint address)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        byte value;
        if (bank >= 0xC0)
        {
            value = _rom[HiRomIndex(bank, offset)];
        }
        else if (bank is >= 0x40 and < 0x50)
        {
            Tick(CycleClocks);
            value = _bwram[(((bank & 0x0F) << 16) | offset) & _bwramMask];
        }
        else if (bank is >= 0x60 and < 0x70)
        {
            Tick(CycleClocks);
            value = BitmapRead(((bank & 0x0F) << 16) | offset);
        }
        else if ((bank & 0x40) != 0)
        {
            value = _openBus;
        }
        else
        {
            switch (offset)
            {
                case >= 0x8000:
                    value = bank == 0 && offset >= 0xFFE0 && Vector(offset) is var vector and >= 0
                        ? (byte)vector
                        : _rom[LoRomIndex(bank, offset)];
                    break;
                case >= 0x6000:
                    Tick(CycleClocks);
                    value = (_w[0x25] & 0x80) != 0
                        ? BitmapRead((_w[0x25] & 0x7F) * 0x2000 + (offset & 0x1FFF))
                        : _bwram[((_w[0x25] & 0x1F) * 0x2000 + (offset & 0x1FFF)) & _bwramMask];
                    break;
                case < 0x0800: value = _iram[offset]; break;
                case >= 0x3000 and < 0x3800: value = _iram[offset & 0x7FF]; break;
                case 0x2301: value = StatusForSa1(); break;
                case >= 0x2300 and < 0x2310: value = ReadSharedRegister(offset, _openBus); break;
                default: value = _openBus; break;
            }
        }

        Tick(CycleClocks);
        _openBus = value;
        return value;
    }

    void IW65816Bus.Write(uint address, byte value)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        Tick(CycleClocks);
        _openBus = value;
        if (bank >= 0xC0)
        {
            return;
        }

        if (bank is >= 0x40 and < 0x50)
        {
            Tick(CycleClocks);
            _bwram[(((bank & 0x0F) << 16) | offset) & _bwramMask] = value;
            return;
        }

        if (bank is >= 0x60 and < 0x70)
        {
            Tick(CycleClocks);
            BitmapWrite(((bank & 0x0F) << 16) | offset, value);
            return;
        }

        if ((bank & 0x40) != 0)
        {
            return;
        }

        switch (offset)
        {
            case >= 0x8000: return;
            case >= 0x6000:
                Tick(CycleClocks);
                if ((_w[0x25] & 0x80) != 0)
                {
                    BitmapWrite((_w[0x25] & 0x7F) * 0x2000 + (offset & 0x1FFF), value);
                }
                else
                {
                    _bwram[((_w[0x25] & 0x1F) * 0x2000 + (offset & 0x1FFF)) & _bwramMask] = value;
                }

                return;
            case < 0x0800: _iram[offset] = value; return;
            case >= 0x3000 and < 0x3800: _iram[offset & 0x7FF] = value; return;
            case >= 0x2200 and < 0x2300: WriteRegister(offset, value); return;
        }
    }

    void IW65816Bus.Idle() => Tick(CycleClocks);

    /// <summary>The SA-1's reset, NMI and IRQ vectors come from CRV, CNV and CIV; -1 for the rest of the page.</summary>
    private int Vector(int offset) => offset switch
    {
        0xFFFC or 0xFFFD => _w[0x03 + (offset & 1)],
        0xFFEA or 0xFFEB or 0xFFFA or 0xFFFB => _w[0x05 + (offset & 1)],
        0xFFEE or 0xFFEF or 0xFFFE or 0xFFFF => _w[0x07 + (offset & 1)],
        _ => -1,
    };

    // ---- ROM: the Super MMC ----

    /// <summary>$00–$3F and $80–$BF, $8000–$FFFF: each 32-bank quarter shows a megabyte chosen by CXB–FXB (bit 7 set) or its own.</summary>
    private int LoRomIndex(int bank, int offset)
    {
        var quarter = ((bank >> 6) & 2) | ((bank >> 5) & 1);
        var register = _w[0x20 + quarter];
        var block = (register & 0x80) != 0 ? register & 7 : quarter;
        return (block * 0x100000 + (bank & 0x1F) * 0x8000 + (offset & 0x7FFF)) % _rom.Length;
    }

    /// <summary>$C0–$FF: each 16 banks show the megabyte chosen by CXB–FXB.</summary>
    private int HiRomIndex(int bank, int offset) => ((_w[0x20 + ((bank >> 4) & 3)] & 7) * 0x100000 + ((bank & 0x0F) << 16 | offset)) % _rom.Length;

    // ---- BW-RAM as a bitmap ($60–$6F): two 4-bit or four 2-bit pixels a byte ----

    private bool TwoBitBitmap => (_w[0x3F] & 0x80) != 0;

    private byte BitmapRead(int address)
    {
        if (TwoBitBitmap)
        {
            return (byte)((_bwram[(address >> 2) & _bwramMask] >> ((address & 3) * 2)) & 3);
        }

        return (byte)((_bwram[(address >> 1) & _bwramMask] >> ((address & 1) * 4)) & 0xF);
    }

    private void BitmapWrite(int address, byte value)
    {
        if (TwoBitBitmap)
        {
            var shift = (address & 3) * 2;
            ref var b = ref _bwram[(address >> 2) & _bwramMask];
            b = (byte)((b & ~(3 << shift)) | ((value & 3) << shift));
        }
        else
        {
            var shift = (address & 1) * 4;
            ref var b = ref _bwram[(address >> 1) & _bwramMask];
            b = (byte)((b & ~(0xF << shift)) | ((value & 0xF) << shift));
        }
    }

    // ---- Registers ----

    private byte StatusForCpu() => (byte)(
        (_cpuIrqFlag ? 0x80 : 0) | (_w[0x09] & 0x40) | (_charDmaIrqFlag ? 0x20 : 0) | (_w[0x09] & 0x10) | (_w[0x09] & 0x0F));

    private byte StatusForSa1() => (byte)(
        (_sa1IrqFlag ? 0x80 : 0) | (_timerIrqFlag ? 0x40 : 0) | (_dmaIrqFlag ? 0x20 : 0) | (_sa1NmiFlag ? 0x10 : 0) | (_w[0x00] & 0x0F));

    private byte ReadSharedRegister(int offset, byte openBus)
    {
        switch (offset)
        {
            case 0x2302:
                _hLatch = (ushort)(_hCounter >> 2);
                _vLatch = (ushort)_vCounter;
                return (byte)_hLatch;
            case 0x2303: return (byte)(_hLatch >> 8);
            case 0x2304: return (byte)_vLatch;
            case 0x2305: return (byte)(_vLatch >> 8);
            case >= 0x2306 and <= 0x230A: return (byte)(_result >> ((offset - 0x2306) * 8));
            case 0x230B: return (byte)(_overflow ? 0x80 : 0);
            case 0x230C: return (byte)VariableBits();
            case 0x230D:
            {
                var value = (byte)(VariableBits() >> 8);
                if ((_w[0x58] & 0x80) != 0)
                {
                    AdvanceVariableBits();
                }

                return value;
            }

            default: return openBus;
        }
    }

    private void WriteRegister(int offset, byte value)
    {
        var r = offset & 0xFF;
        var old = _w[r];
        _w[r] = value;
        switch (r)
        {
            case 0x00: // CCNT
                if ((old & 0x20) != 0 && (value & 0x20) == 0)
                {
                    _cpu.Reset(); // released from reset: start at CRV
                    _cpu.Waiting = false;
                }

                if ((value & 0x80) != 0)
                {
                    _sa1IrqFlag = true;
                }

                if ((value & 0x10) != 0)
                {
                    _sa1NmiFlag = true;
                    if ((_w[0x0A] & 0x10) != 0)
                    {
                        _cpu.Nmi = true;
                    }
                }

                break;
            case 0x02: // SIC
                _cpuIrqFlag &= (value & 0x80) == 0;
                _charDmaIrqFlag &= (value & 0x20) == 0;
                break;
            case 0x09: // SCNT
                if ((value & 0x80) != 0)
                {
                    _cpuIrqFlag = true;
                }

                break;
            case 0x0B: // CIC
                _sa1IrqFlag &= (value & 0x80) == 0;
                _timerIrqFlag &= (value & 0x40) == 0;
                _dmaIrqFlag &= (value & 0x20) == 0;
                _sa1NmiFlag &= (value & 0x10) == 0;
                break;
            case 0x11: // CTR
                _hCounter = _vCounter = 0;
                break;
            case 0x30: // DCNT
                _charLine = 0;
                if ((value & 0x80) == 0)
                {
                    _charConversion1 = false;
                }

                break;
            case 0x31: // CDMA: bit 7 ends character conversion 1
                if ((value & 0x80) != 0)
                {
                    _charConversion1 = false;
                }

                break;
            case 0x36: // DDA middle byte: starts DMA to I-RAM, or character conversion 1
                if ((_w[0x30] & 0x80) != 0)
                {
                    if ((_w[0x30] & 0x20) != 0)
                    {
                        if ((_w[0x30] & 0x10) != 0)
                        {
                            _charConversion1 = true;
                            _charDmaIrqFlag = true;
                        }
                        else
                        {
                            _charLine = 0;
                        }
                    }
                    else if ((_w[0x30] & 0x04) == 0)
                    {
                        Dma();
                    }
                }

                break;
            case 0x37: // DDA high byte: starts DMA to BW-RAM
                if ((_w[0x30] & 0xA4) == 0x84)
                {
                    Dma();
                }

                break;
            case 0x47 or 0x4F: // the 8th pixel of a row of character conversion 2
                if ((_w[0x30] & 0xB0) == 0xA0)
                {
                    CharConversion2();
                }

                break;
            case 0x50: // MCNT
                if ((value & 0x02) != 0)
                {
                    _result = 0;
                    _overflow = false;
                }

                break;
            case 0x54: // MB high byte: runs the operation
                Arithmetic();
                break;
            case 0x58: // VBD
                if ((value & 0x80) == 0)
                {
                    AdvanceVariableBits();
                }

                break;
            case 0x5B: // VDA high byte
                _vbrAddress = _w[0x59] | (_w[0x5A] << 8) | (value << 16);
                _vbrBit = 0;
                break;
        }
    }

    // ---- Arithmetic ($2250–$2254, results at $2306–$230B) ----

    private void Arithmetic()
    {
        var a = (short)(_w[0x51] | (_w[0x52] << 8));
        var b = (ushort)(_w[0x53] | (_w[0x54] << 8));
        if ((_w[0x50] & 0x02) != 0)
        {
            // Sum of products: 40 bits, with a flag when it overflows.
            _result += (ulong)(long)(a * (short)b);
            _overflow = _result >= 1UL << 40;
            _result &= (1UL << 40) - 1;
        }
        else if ((_w[0x50] & 0x01) == 0)
        {
            _result = (uint)(a * (short)b);
        }
        else if (b == 0)
        {
            _result = 0;
        }
        else
        {
            // Signed by unsigned: the remainder is never negative.
            var remainder = a >= 0 ? a % b : (b - (-a % b)) % b;
            var quotient = (a - remainder) / b;
            _result = ((ulong)(ushort)remainder << 16) | (ushort)quotient;
        }

        _w[0x53] = _w[0x54] = 0; // MB is cleared; MA stays
    }

    // ---- Variable-length bit reads ($2258–$225B, $230C–$230D) ----

    private int VariableBits()
    {
        var data = RomByte(_vbrAddress) | (RomByte(_vbrAddress + 1) << 8) | (RomByte(_vbrAddress + 2) << 16);
        return (data >> _vbrBit) & 0xFFFF;
    }

    private void AdvanceVariableBits()
    {
        var length = _w[0x58] & 0x0F;
        _vbrBit += length == 0 ? 16 : length;
        _vbrAddress = (_vbrAddress + (_vbrBit >> 3)) & 0xFFFFFF;
        _vbrBit &= 7;
    }

    private byte RomByte(int address)
    {
        var bank = (address >> 16) & 0xFF;
        var offset = address & 0xFFFF;
        return bank >= 0xC0 ? _rom[HiRomIndex(bank, offset)] : _rom[LoRomIndex(bank, offset | 0x8000)];
    }

    // ---- DMA ----

    private int SourceAddress => _w[0x32] | (_w[0x33] << 8) | (_w[0x34] << 16);

    private int DestinationAddress => _w[0x35] | (_w[0x36] << 8) | (_w[0x37] << 16);

    /// <summary>Normal DMA: ROM, BW-RAM or I-RAM to I-RAM or BW-RAM, DTC bytes, then the SA-1's DMA interrupt.</summary>
    private void Dma()
    {
        var source = SourceAddress;
        var destination = DestinationAddress;
        var count = _w[0x38] | (_w[0x39] << 8);
        for (var i = 0; i < count; i++)
        {
            var s = source + i;
            var value = (_w[0x30] & 0x03) switch
            {
                0 => RomByte(s),
                1 => _bwram[s & _bwramMask],
                _ => _iram[s & 0x7FF],
            };
            if ((_w[0x30] & 0x04) == 0)
            {
                _iram[(destination + i) & 0x7FF] = value;
            }
            else
            {
                _bwram[(destination + i) & _bwramMask] = value;
            }
        }

        _dmaIrqFlag = true;
    }

    /// <summary>CDMA bits 0–1: 0 for 8 bits a pixel, 1 for 4, 2 for 2.</summary>
    private int ColorShift => Math.Min(_w[0x31] & 3, 2);

    /// <summary>
    /// Character conversion 1: while the SNES's DMA reads the bitmap in BW-RAM, each tile it reaches is converted into
    /// SNES bitplanes in I-RAM at DDA, and the read is answered from there.
    /// </summary>
    private byte CharConversion1Read(int address)
    {
        var shift = ColorShift;
        var tileBytesMask = (1 << (6 - shift)) - 1;
        var source = SourceAddress;
        var destination = DestinationAddress;
        if ((address & tileBytesMask) == 0)
        {
            var bytesPerRow = 2 << (2 - shift); // one tile row's bytes in the bitmap
            var tilesAcross = _w[0x31] >> 2 & 7;
            var bitmapLine = (8 << tilesAcross) >> shift;
            var tile = ((address - source) & _bwramMask) >> (6 - shift);
            var tileY = tile >> tilesAcross;
            var tileX = tile & ((1 << tilesAcross) - 1);
            var at = source + tileY * 8 * bitmapLine + tileX * bytesPerRow;
            Span<byte> planes = stackalloc byte[8];
            for (var y = 0; y < 8; y++)
            {
                ulong data = 0;
                for (var b = 0; b < bytesPerRow; b++)
                {
                    data |= (ulong)_bwram[(at + b) & _bwramMask] << (b * 8);
                }

                at += bitmapLine;
                planes.Clear();
                var depth = bytesPerRow;
                for (var x = 0; x < 8; x++)
                {
                    for (var p = 0; p < depth; p++)
                    {
                        planes[p] |= (byte)((data & 1) << (7 - x));
                        data >>= 1;
                    }
                }

                for (var p = 0; p < depth; p++)
                {
                    _iram[(destination + (y << 1) + ((p & 6) << 3) + (p & 1)) & 0x7FF] = planes[p];
                }
            }
        }

        return _iram[(destination + (address & tileBytesMask)) & 0x7FF];
    }

    /// <summary>Character conversion 2: a row of 8 pixels written to BRF becomes one row of a tile in I-RAM.</summary>
    private void CharConversion2()
    {
        var shift = ColorShift;
        var depth = 2 << (2 - shift);
        var file = (_charLine & 1) << 3;
        var at = DestinationAddress & 0x7FF;
        at &= ~((1 << (7 - shift)) - 1);
        at += (_charLine & 8) * depth;
        at += (_charLine & 7) * 2;
        for (var p = 0; p < depth; p++)
        {
            var output = 0;
            for (var x = 0; x < 8; x++)
            {
                output |= ((_w[0x40 + file + x] >> p) & 1) << (7 - x);
            }

            _iram[(at + ((p & 6) << 3) + (p & 1)) & 0x7FF] = (byte)output;
        }

        _charLine = (_charLine + 1) & 15;
    }

    // ---- The timer ----

    private void Tick(int clocks)
    {
        _clock += clocks;
        var control = _w[0x10];
        if ((control & 0x03) == 0)
        {
            // No interrupt to look for: just move the counters.
            AdvanceCounters(clocks);
            return;
        }

        var hTarget = (_w[0x12] | (_w[0x13] & 1) << 8) << 2;
        var vTarget = _w[0x14] | (_w[0x15] & 1) << 8;
        for (var c = 0; c < clocks; c += 2)
        {
            AdvanceCounters(2);
            var hit = (control & 0x03) switch
            {
                1 => _hCounter == hTarget,
                2 => _vCounter == vTarget && _hCounter == 0,
                _ => _vCounter == vTarget && _hCounter == hTarget,
            };
            if (hit)
            {
                _timerIrqFlag = true;
            }
        }
    }

    private void AdvanceCounters(int clocks)
    {
        _hCounter += clocks;
        if ((_w[0x10] & 0x80) != 0)
        {
            // Linear: an 18-bit count, H in the low 11 bits.
            _vCounter = (_vCounter + (_hCounter >> 11)) & 0x1FF;
            _hCounter &= 0x7FF;
        }
        else
        {
            while (_hCounter >= 1364)
            {
                _hCounter -= 1364;
                if (++_vCounter >= _lines)
                {
                    _vCounter = 0;
                }
            }
        }
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        _cpu.SaveState(writer);
        writer.WriteInt64(_clock);
        writer.WriteByte(_openBus);
        writer.WriteBytes(_iram);
        writer.WriteBytes(_bwram);
        writer.WriteBytes(_w);
        writer.WriteByte((byte)(
            (_cpuIrqFlag ? 1 : 0) | (_charDmaIrqFlag ? 2 : 0) | (_sa1IrqFlag ? 4 : 0) | (_timerIrqFlag ? 8 : 0)
            | (_dmaIrqFlag ? 16 : 0) | (_sa1NmiFlag ? 32 : 0) | (_overflow ? 64 : 0) | (_charConversion1 ? 128 : 0)));
        writer.WriteInt32(_hCounter);
        writer.WriteInt32(_vCounter);
        writer.WriteUInt16(_hLatch);
        writer.WriteUInt16(_vLatch);
        writer.WriteInt64((long)_result);
        writer.WriteInt32(_vbrAddress);
        writer.WriteInt32(_vbrBit);
        writer.WriteInt32(_charLine);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _cpu.LoadState(reader);
        _clock = reader.ReadInt64();
        _openBus = reader.ReadByte();
        reader.ReadBytes(_iram);
        reader.ReadBytes(_bwram);
        reader.ReadBytes(_w);
        var flags = reader.ReadByte();
        _cpuIrqFlag = (flags & 1) != 0;
        _charDmaIrqFlag = (flags & 2) != 0;
        _sa1IrqFlag = (flags & 4) != 0;
        _timerIrqFlag = (flags & 8) != 0;
        _dmaIrqFlag = (flags & 16) != 0;
        _sa1NmiFlag = (flags & 32) != 0;
        _overflow = (flags & 64) != 0;
        _charConversion1 = (flags & 128) != 0;
        _hCounter = reader.ReadInt32() & 0x7FF;
        _vCounter = reader.ReadInt32() & 0x1FF;
        _hLatch = reader.ReadUInt16();
        _vLatch = reader.ReadUInt16();
        _result = (ulong)reader.ReadInt64() & ((1UL << 40) - 1);
        _vbrAddress = reader.ReadInt32() & 0xFFFFFF;
        _vbrBit = reader.ReadInt32() & 7;
        _charLine = reader.ReadInt32() & 15;
        reader.EndChunk();
    }
}
