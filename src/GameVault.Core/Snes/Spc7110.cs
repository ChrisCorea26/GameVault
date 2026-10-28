using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The SPC7110 (snes-chips-3.md §2), Hudson's chip in Far East of Eden Zero, Momotarou Dentetsu Happy and Super Power
/// League 4. It is a memory controller, a graphics decompressor, a data port and an arithmetic unit, with the Epson
/// RTC-4513 beside it on Far East of Eden Zero's board.
/// </summary>
/// <remarks>
/// <para>The cartridge's first megabyte is the program ROM, the rest the data ROM. The CPU sees the program ROM in
/// HiROM order in $00–$0F (and $80–$8F) $8000–$FFFF and in $C0–$CF. $D0–$DF, $E0–$EF and $F0–$FF (and their LoROM
/// mirrors) each show the data ROM megabyte $4831, $4832 and $4833 choose ($D0–$DF shows the program ROM's second
/// megabyte instead when $4834 bit 2 says it has one). SRAM is at $00–$3F/$80–$BF $6000–$7FFF, enabled by $4830 bit 7.
/// $4834 bits 0–1 set the data ROM's size.</para>
/// <para>The registers are $4800–$4842 in banks $00–$3F and $80–$BF; reading anywhere in bank $50 is $4800, and bank
/// $58 is $4808. The decompressor is Neviksti's reverse engineering of the chip's context-model coder, as talarubi
/// optimised it, ported from ares (ISC licence, see THIRD-PARTY-NOTICES.md). Its results are checked in the tests
/// against ares' own code, compiled from its source.</para>
/// </remarks>
public sealed class Spc7110 : ISnesCoprocessor, ISnesRealTimeClock
{
    private const int ProgramRomSize = 0x100000;

    private static readonly StateTag Tag = StateTag.From("7110");

    private readonly byte[] _rom;
    private readonly int _dataRomSize;
    private readonly byte[] _ram;
    private readonly Decompressor _decompressor;
    private readonly byte[] _tile = new byte[32];
    private long _clock;

    // The decompression unit.
    private byte _r4801, _r4802, _r4803, _r4804, _r4805, _r4806, _r4807, _r4809, _r480a, _r480b, _r480c;
    private int _dcuMode;
    private int _dcuAddress;
    private int _dcuOffset;

    // The data port.
    private byte _r4810, _r4811, _r4812, _r4813, _r4814, _r4815, _r4816, _r4817, _r4818;

    // The arithmetic unit.
    private readonly byte[] _alu = new byte[16]; // $4820–$482F

    // The memory controller.
    private byte _r4830, _r4831, _r4832 = 1, _r4833 = 2, _r4834;

    /// <param name="rom">The whole cartridge ROM: the program ROM's megabyte, then the data ROM.</param>
    /// <param name="ramSize">The battery SRAM's size from the header.</param>
    /// <param name="withRtc">Far East of Eden Zero's board, with the RTC-4513 at $4840–$4842.</param>
    public Spc7110(byte[] rom, int ramSize, bool withRtc, double masterClock)
    {
        _rom = rom;
        _dataRomSize = Math.Max(0, rom.Length - ProgramRomSize);
        _ram = new byte[ramSize];
        Array.Fill(_ram, (byte)0xFF);
        _decompressor = new Decompressor(this);
        Rtc = withRtc ? new EpsonRtc(masterClock) : null;
    }

    public string Name => "SPC7110";

    public bool Irq => false;

    /// <summary>The battery-backed SRAM.</summary>
    public Span<byte> Ram => _ram;

    /// <summary>The RTC-4513, on Far East of Eden Zero's board.</summary>
    public EpsonRtc? Rtc { get; }

    public void SetTime(DateTime time) => Rtc?.SetTime(time);

    public bool Claims(uint address)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        if (bank >= 0xC0 || bank is 0x50 or 0x58)
        {
            return true;
        }

        return (bank & 0x40) == 0 && (offset >= 0x6000 || (offset & 0xFFC0) == 0x4800 || offset is >= 0x4840 and <= 0x4842);
    }

    public byte Read(uint address, byte openBus)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        switch (bank)
        {
            case 0x50:
                return ReadRegister(0x00, openBus);
            case 0x58:
                return ReadRegister(0x08, openBus);
            case >= 0xC0:
                return ReadMcuRom(((bank & 0x3F) << 16) | offset);
        }

        if (offset >= 0x8000)
        {
            return ReadMcuRom(((bank & 0x3F) << 16) | offset);
        }

        if (offset >= 0x6000)
        {
            return (_r4830 & 0x80) != 0 && _ram.Length > 0 ? _ram[SramIndex(bank, offset)] : (byte)0;
        }

        if (offset >= 0x4840)
        {
            return Rtc?.Read(offset - 0x4840, openBus) ?? openBus;
        }

        return ReadRegister(offset & 0x3F, openBus);
    }

    public void Write(uint address, byte value)
    {
        var bank = (int)(address >> 16);
        var offset = (int)(address & 0xFFFF);
        switch (bank)
        {
            case 0x50:
                WriteRegister(0x00, value);
                return;
            case 0x58:
                WriteRegister(0x08, value);
                return;
            case >= 0xC0:
                return;
        }

        if (offset >= 0x8000)
        {
            return;
        }

        if (offset >= 0x6000)
        {
            if ((_r4830 & 0x80) != 0 && _ram.Length > 0)
            {
                _ram[SramIndex(bank, offset)] = value;
            }

            return;
        }

        if (offset >= 0x4840)
        {
            Rtc?.Write(offset - 0x4840, value);
            return;
        }

        WriteRegister(offset & 0x3F, value);
    }

    // ---- Memory ----

    /// <summary>SRAM: the bank and the offset's low 13 bits, mirrored over its size.</summary>
    private int SramIndex(int bank, int offset) => Mirror(((bank & 0x3F) << 16) | (offset & 0x1FFF), _ram.Length);

    /// <summary>The CPU's view of the ROM windows: <paramref name="address"/> is HiROM-style, $000000–$3FFFFF.</summary>
    private byte ReadMcuRom(int address)
    {
        var window = address >> 20;
        address &= 0x0FFFFF;
        switch (window)
        {
            case 0:
                return _rom[Mirror(address, Math.Min(ProgramRomSize, _rom.Length))];
            case 1:
                if ((_r4834 & 4) != 0)
                {
                    return _rom[Mirror(ProgramRomSize + address, Math.Min(2 * ProgramRomSize, _rom.Length))];
                }

                return DataRom(address | 0x100000 * (_r4831 & 7));
            case 2:
                return DataRom(address | 0x100000 * (_r4832 & 7));
            default:
                return DataRom(address | 0x100000 * (_r4833 & 7));
        }
    }

    /// <summary>A data ROM byte, within the size $4834 sets (a 64 Mbit setting also reaches the addresses past 4 MB).</summary>
    internal byte DataRom(int address)
    {
        if (_dataRomSize == 0)
        {
            return 0;
        }

        address &= 0xFFFFFF;
        var size = 1 << (_r4834 & 3);
        if ((_r4834 & 3) != 3 && (address & 0x400000) != 0)
        {
            return 0;
        }

        return _rom[ProgramRomSize + Mirror(address & (0x100000 * size - 1), _dataRomSize)];
    }

    /// <summary>
    /// The SNES's mirroring of a memory whose size isn't a power of two: the address loses its highest bit until it
    /// fits, the part of the memory past that bit's size repeating within it.
    /// </summary>
    internal static int Mirror(int address, int size)
    {
        if (size <= 0)
        {
            return 0;
        }

        var baseAddress = 0;
        var mask = 1 << 23;
        while (address >= size)
        {
            while ((address & mask) == 0)
            {
                mask >>= 1;
            }

            address -= mask;
            if (size > mask)
            {
                size -= mask;
                baseAddress += mask;
            }

            mask >>= 1;
        }

        return baseAddress + address;
    }

    // ---- Registers ----

    private byte ReadRegister(int register, byte openBus)
    {
        switch (register)
        {
            case 0x00:
            {
                var counter = (_r4809 | _r480a << 8) - 1;
                _r4809 = (byte)counter;
                _r480a = (byte)(counter >> 8);
                return DcuRead();
            }

            case 0x01: return _r4801;
            case 0x02: return _r4802;
            case 0x03: return _r4803;
            case 0x04: return _r4804;
            case 0x05: return _r4805;
            case 0x06: return _r4806;
            case 0x07: return _r4807;
            case 0x08: return 0;
            case 0x09: return _r4809;
            case 0x0A: return _r480a;
            case 0x0B: return _r480b;
            case 0x0C: return _r480c;
            case 0x10:
            {
                var value = _r4810;
                Increment4810();
                return value;
            }

            case 0x11: return _r4811;
            case 0x12: return _r4812;
            case 0x13: return _r4813;
            case 0x14: return _r4814;
            case 0x15: return _r4815;
            case 0x16: return _r4816;
            case 0x17: return _r4817;
            case 0x18: return _r4818;
            case 0x1A:
                IncrementByAdjust(3);
                return 0;
            case >= 0x20 and < 0x30: return _alu[register - 0x20];
            case 0x30: return _r4830;
            case 0x31: return _r4831;
            case 0x32: return _r4832;
            case 0x33: return _r4833;
            case 0x34: return _r4834;
            default: return openBus;
        }
    }

    private void WriteRegister(int register, byte value)
    {
        switch (register)
        {
            case 0x01: _r4801 = value; break;
            case 0x02: _r4802 = value; break;
            case 0x03: _r4803 = (byte)(value & 0x7F); break;
            case 0x04: _r4804 = value; DcuLoadAddress(); break;
            case 0x05: _r4805 = value; break;
            case 0x06:
                _r4806 = value;
                _r480c &= 0x7F;
                DcuBeginTransfer();
                break;
            case 0x07: _r4807 = value; break;
            case 0x09: _r4809 = value; break;
            case 0x0A: _r480a = value; break;
            case 0x0B: _r480b = (byte)(value & 3); break;
            case 0x11: _r4811 = value; break;
            case 0x12: _r4812 = value; break;
            case 0x13: _r4813 = (byte)(value & 0x7F); DataPortRead(); break;
            case 0x14: _r4814 = value; IncrementByAdjust(1); break;
            case 0x15:
                _r4815 = value;
                if ((_r4818 & 2) != 0)
                {
                    DataPortRead();
                }

                IncrementByAdjust(2);
                break;
            case 0x16: _r4816 = value; break;
            case 0x17: _r4817 = value; break;
            case 0x18: _r4818 = (byte)(value & 0x7F); DataPortRead(); break;
            case >= 0x20 and <= 0x24: _alu[register - 0x20] = value; break;
            case 0x25:
                _alu[0x05] = value;
                _alu[0x0F] |= 0x81; // busy (bit 7, cleared when done; bit 0 stays, as ares has it)
                Multiply();
                break;
            case 0x26: _alu[0x06] = value; break;
            case 0x27:
                _alu[0x07] = value;
                _alu[0x0F] |= 0x80;
                Divide();
                break;
            case 0x2E: _alu[0x0E] = (byte)(value & 1); break;
            case 0x30: _r4830 = (byte)(value & 0x87); break;
            case 0x31: _r4831 = (byte)(value & 7); break;
            case 0x32: _r4832 = (byte)(value & 7); break;
            case 0x33: _r4833 = (byte)(value & 7); break;
            case 0x34: _r4834 = (byte)(value & 7); break;
        }
    }

    // ---- The decompression unit ----

    /// <summary>$4804: the table entry ($4801–$4803 + 4 × $4804) gives the mode and the data's address.</summary>
    private void DcuLoadAddress()
    {
        var entry = (_r4801 | _r4802 << 8 | _r4803 << 16) + (_r4804 << 2);
        _dcuMode = DataRom(entry) & 3;
        _dcuAddress = (DataRom(entry + 1) << 16 | DataRom(entry + 2) << 8 | DataRom(entry + 3)) & 0x7FFFFF;
    }

    /// <summary>$4806 starts decompressing (done at once; $480C bit 7 says it's ready), skipping $4805/6 rows with $480B bit 1.</summary>
    private void DcuBeginTransfer()
    {
        if ((_dcuMode & 3) == 3)
        {
            return; // not a mode
        }

        _decompressor.Initialize(_dcuMode & 3, _dcuAddress);
        _decompressor.Decode();
        var seek = (_r480b & 2) != 0 ? (_r4805 | _r4806 << 8) : 0;
        for (var i = 0; i < seek; i++)
        {
            _decompressor.Decode();
        }

        _r480c |= 0x80;
        _dcuOffset = 0;
    }

    /// <summary>$4800: the next byte of the tile being unpacked; each tile's eight rows are taken $4807 rows apart with $480B bit 0.</summary>
    private byte DcuRead()
    {
        if ((_r480c & 0x80) == 0)
        {
            return 0;
        }

        var bpp = _decompressor.Bpp;
        if (_dcuOffset == 0)
        {
            for (var row = 0; row < 8; row++)
            {
                var result = _decompressor.Result;
                switch (bpp)
                {
                    case 1:
                        _tile[row] = (byte)result;
                        break;
                    case 2:
                        _tile[row * 2] = (byte)result;
                        _tile[row * 2 + 1] = (byte)(result >> 8);
                        break;
                    default:
                        _tile[row * 2] = (byte)result;
                        _tile[row * 2 + 1] = (byte)(result >> 8);
                        _tile[row * 2 + 16] = (byte)(result >> 16);
                        _tile[row * 2 + 17] = (byte)(result >> 24);
                        break;
                }

                var seek = (_r480b & 1) != 0 ? _r4807 : 1;
                for (var i = 0; i < seek; i++)
                {
                    _decompressor.Decode();
                }
            }
        }

        var value = _tile[_dcuOffset];
        _dcuOffset = (_dcuOffset + 1) & (8 * bpp - 1);
        return value;
    }

    // ---- The data port ----

    private int DataOffset
    {
        get => _r4811 | _r4812 << 8 | _r4813 << 16;
        set
        {
            _r4811 = (byte)value;
            _r4812 = (byte)(value >> 8);
            _r4813 = (byte)((value >> 16) & 0x7F);
        }
    }

    private int DataAdjust
    {
        get => _r4814 | _r4815 << 8;
        set
        {
            _r4814 = (byte)value;
            _r4815 = (byte)(value >> 8);
        }
    }

    private int DataStride => _r4816 | _r4817 << 8;

    /// <summary>The adjust, sign-extended when $4818 bit 3 says it's signed.</summary>
    private int SignedAdjust => (_r4818 & 8) != 0 ? (short)DataAdjust : DataAdjust;

    /// <summary>$4810 holds the byte at the offset (plus the adjust, with $4818 bit 1).</summary>
    private void DataPortRead()
    {
        var adjust = (_r4818 & 2) != 0 ? SignedAdjust : 0;
        _r4810 = DataRom((DataOffset + adjust) & 0xFFFFFF);
    }

    /// <summary>A read of $4810 steps the offset (or, with $4818 bit 4, the adjust) by the stride ($4818 bit 0) or 1.</summary>
    private void Increment4810()
    {
        var stride = (_r4818 & 1) != 0 ? DataStride : 1;
        if ((_r4818 & 4) != 0)
        {
            stride = (short)stride;
        }

        if ((_r4818 & 16) == 0)
        {
            DataOffset = (DataOffset + stride) & 0xFFFFFF;
        }
        else
        {
            DataAdjust = (SignedAdjust + stride) & 0xFFFF;
        }

        DataPortRead();
    }

    /// <summary>
    /// $4818 bits 5–6 choose a register (1: writing $4814, 2: writing $4815, 3: reading $481A) that adds the adjust to the
    /// offset.
    /// </summary>
    private void IncrementByAdjust(int trigger)
    {
        if (_r4818 >> 5 != trigger)
        {
            return;
        }

        DataOffset = (DataOffset + SignedAdjust) & 0xFFFFFF;
        DataPortRead();
    }

    // ---- The arithmetic unit ----

    /// <summary>$4825: $4824/5 × $4820/1 into $4828–$482B, signed with $482E bit 0.</summary>
    private void Multiply()
    {
        int a = _alu[4] | _alu[5] << 8, b = _alu[0] | _alu[1] << 8;
        var product = (_alu[0x0E] & 1) != 0 ? (uint)((short)a * (short)b) : (uint)a * (uint)b;
        Store32(0x08, product);
        _alu[0x0F] &= 0x7F;
    }

    /// <summary>$4827: $4820–$4823 ÷ $4826/7 into $4828–$482B and the remainder into $482C/D; ÷ 0 gives 0 and the dividend.</summary>
    private void Divide()
    {
        var dividend = (uint)(_alu[0] | _alu[1] << 8 | _alu[2] << 16 | _alu[3] << 24);
        var divisor = (ushort)(_alu[6] | _alu[7] << 8);
        uint quotient;
        ushort remainder;
        if ((_alu[0x0E] & 1) != 0)
        {
            var d = (int)dividend;
            var s = (short)divisor;
            if (s != 0)
            {
                quotient = (uint)(int)((long)d / s);
                remainder = (ushort)(short)((long)d % s);
            }
            else
            {
                quotient = 0;
                remainder = (ushort)d;
            }
        }
        else if (divisor != 0)
        {
            quotient = dividend / divisor;
            remainder = (ushort)(dividend % divisor);
        }
        else
        {
            quotient = 0;
            remainder = (ushort)dividend;
        }

        Store32(0x08, quotient);
        _alu[0x0C] = (byte)remainder;
        _alu[0x0D] = (byte)(remainder >> 8);
        _alu[0x0F] &= 0x7F;
    }

    private void Store32(int at, uint value)
    {
        _alu[at] = (byte)value;
        _alu[at + 1] = (byte)(value >> 8);
        _alu[at + 2] = (byte)(value >> 16);
        _alu[at + 3] = (byte)(value >> 24);
    }

    // ---- Timing, power and state ----

    public void CatchUp(long clock)
    {
        if (clock > _clock)
        {
            Rtc?.Advance(clock - _clock);
            _clock = clock;
        }
    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    public void Power()
    {
        Reset();
        _clock = 0;
        Rtc?.Power();
    }

    public void Reset()
    {
        _r4801 = _r4802 = _r4803 = _r4804 = _r4805 = _r4806 = _r4807 = _r4809 = _r480a = _r480b = _r480c = 0;
        _dcuMode = _dcuAddress = _dcuOffset = 0;
        _r4810 = _r4811 = _r4812 = _r4813 = _r4814 = _r4815 = _r4816 = _r4817 = _r4818 = 0;
        Array.Clear(_alu);
        _r4830 = _r4831 = _r4834 = 0;
        _r4832 = 1;
        _r4833 = 2;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteBytes([_r4801, _r4802, _r4803, _r4804, _r4805, _r4806, _r4807, _r4809, _r480a, _r480b, _r480c]);
        writer.WriteInt32(_dcuMode);
        writer.WriteInt32(_dcuAddress);
        writer.WriteInt32(_dcuOffset);
        writer.WriteBytes(_tile);
        writer.WriteBytes([_r4810, _r4811, _r4812, _r4813, _r4814, _r4815, _r4816, _r4817, _r4818]);
        writer.WriteBytes(_alu);
        writer.WriteBytes([_r4830, _r4831, _r4832, _r4833, _r4834]);
        writer.WriteInt64(_clock);
        _decompressor.SaveState(writer);
        Rtc?.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        Span<byte> dcu = stackalloc byte[11];
        reader.ReadBytes(dcu);
        (_r4801, _r4802, _r4803, _r4804, _r4805, _r4806, _r4807, _r4809, _r480a, _r480b, _r480c) =
            (dcu[0], dcu[1], (byte)(dcu[2] & 0x7F), dcu[3], dcu[4], dcu[5], dcu[6], dcu[7], dcu[8], (byte)(dcu[9] & 3), dcu[10]);
        _dcuMode = reader.ReadInt32() & 3;
        _dcuAddress = reader.ReadInt32() & 0x7FFFFF;
        _dcuOffset = reader.ReadInt32() & 31;
        reader.ReadBytes(_tile);
        Span<byte> port = stackalloc byte[9];
        reader.ReadBytes(port);
        (_r4810, _r4811, _r4812, _r4813, _r4814, _r4815, _r4816, _r4817, _r4818) =
            (port[0], port[1], port[2], (byte)(port[3] & 0x7F), port[4], port[5], port[6], port[7], (byte)(port[8] & 0x7F));
        reader.ReadBytes(_alu);
        Span<byte> mcu = stackalloc byte[5];
        reader.ReadBytes(mcu);
        (_r4830, _r4831, _r4832, _r4833, _r4834) =
            ((byte)(mcu[0] & 0x87), (byte)(mcu[1] & 7), (byte)(mcu[2] & 7), (byte)(mcu[3] & 7), (byte)(mcu[4] & 7));
        _clock = reader.ReadInt64();
        _decompressor.LoadState(reader);
        Rtc?.LoadState(reader);
        reader.EndChunk();
    }

    /// <summary>
    /// The context-model decoder: a binary arithmetic coder over up to 75 contexts (chosen by the pixel's neighbours
    /// and the bits decoded so far), a 53-state probability model, and a most-recently-used colour list for 2 and 4
    /// bits a pixel. Each <see cref="Decode"/> produces eight pixels: one row of a tile, in <see cref="Result"/> as
    /// the row's bitplanes (1 bpp: a byte; 2 bpp: two; 4 bpp: four).
    /// </summary>
    internal sealed class Decompressor(Spc7110 chip)
    {
        private const int Mps = 0, Lps = 1;
        private const int Half = 0x55, Max = 0xFF;

        // Each model state: the probability of the more probable symbol, and the next state after each symbol.
        private static readonly (byte Probability, byte NextMps, byte NextLps)[] Evolution =
        [
            (0x5a, 1, 1), (0x25, 2, 6), (0x11, 3, 8), (0x08, 4, 10), (0x03, 5, 12), (0x01, 5, 15),
            (0x5a, 7, 7), (0x3f, 8, 19), (0x2c, 9, 21), (0x20, 10, 22), (0x17, 11, 23), (0x11, 12, 25),
            (0x0c, 13, 26), (0x09, 14, 28), (0x07, 15, 29), (0x05, 16, 31), (0x04, 17, 32), (0x03, 18, 34),
            (0x02, 5, 35),
            (0x5a, 20, 20), (0x48, 21, 39), (0x3a, 22, 40), (0x2e, 23, 42), (0x26, 24, 44), (0x1f, 25, 45),
            (0x19, 26, 46), (0x15, 27, 25), (0x11, 28, 26), (0x0e, 29, 26), (0x0b, 30, 27), (0x09, 31, 28),
            (0x08, 32, 29), (0x07, 33, 30), (0x05, 34, 31), (0x04, 35, 33), (0x04, 36, 33), (0x03, 37, 34),
            (0x02, 38, 35), (0x02, 5, 36),
            (0x58, 40, 39), (0x4d, 41, 47), (0x43, 42, 48), (0x3b, 43, 49), (0x34, 44, 50), (0x2e, 45, 51),
            (0x29, 46, 44), (0x25, 24, 45),
            (0x56, 48, 47), (0x4f, 49, 47), (0x47, 50, 48), (0x41, 51, 49), (0x3c, 52, 50), (0x37, 43, 51),
        ];

        private readonly byte[] _prediction = new byte[5 * 15];
        private readonly byte[] _swap = new byte[5 * 15];
        private int _offset;
        private int _bits;
        private int _range;
        private int _input;
        private int _output;
        private ulong _pixels;
        private ulong _colormap;

        public int Bpp { get; private set; } = 1;

        public uint Result { get; private set; }

        private byte Next() => chip.DataRom(_offset++ & 0xFFFFFF);

        public void Initialize(int mode, int origin)
        {
            Array.Clear(_prediction);
            Array.Clear(_swap);
            Bpp = 1 << mode;
            _offset = origin;
            _bits = 8;
            _range = Max + 1;
            _input = Next();
            _input = ((_input << 8) | Next()) & 0xFFFF;
            _output = 0;
            _pixels = 0;
            _colormap = 0xFEDCBA9876543210;
        }

        /// <summary>Moves the nibble <paramref name="nibble"/> to the front of the list of sixteen.</summary>
        private static ulong MoveToFront(ulong list, int nibble)
        {
            ulong mask = ~15UL;
            for (var n = 0; n < 64; n += 4, mask <<= 4)
            {
                if ((int)((list >> n) & 15) != nibble)
                {
                    continue;
                }

                return (list & mask) + ((list << 4) & ~mask) + (ulong)nibble;
            }

            return list;
        }

        /// <summary>Unpacks pixels packed MSB first: the odd bits to the low half, the even bits to the high half.</summary>
        private static uint Deinterleave(ulong data, int bits)
        {
            data &= (1UL << bits) - 1;
            data = 0x5555555555555555UL & ((data << bits) | (data >> 1));
            data = 0x3333333333333333UL & (data | (data >> 1));
            data = 0x0F0F0F0F0F0F0F0FUL & (data | (data >> 2));
            data = 0x00FF00FF00FF00FFUL & (data | (data >> 4));
            data = 0x0000FFFF0000FFFFUL & (data | (data >> 8));
            return (uint)(data | (data >> 16));
        }

        public void Decode()
        {
            for (var pixel = 0; pixel < 8; pixel++)
            {
                var map = _colormap;
                var diff = 0;
                if (Bpp > 1)
                {
                    int pa = (int)(Bpp == 2 ? (_pixels >> 2) & 3 : _pixels & 15);
                    int pb = (int)(Bpp == 2 ? (_pixels >> 14) & 3 : (_pixels >> 28) & 15);
                    int pc = (int)(Bpp == 2 ? (_pixels >> 16) & 3 : (_pixels >> 32) & 15);
                    if (pa != pb || pb != pc)
                    {
                        var match = pa ^ pb ^ pc;
                        diff = 4; // all differ
                        if ((match ^ pc) == 0)
                        {
                            diff = 3; // a == b
                        }

                        if ((match ^ pb) == 0)
                        {
                            diff = 2; // a == c
                        }

                        if ((match ^ pa) == 0)
                        {
                            diff = 1; // b == c
                        }
                    }

                    _colormap = MoveToFront(_colormap, pa);
                    map = MoveToFront(map, pc);
                    map = MoveToFront(map, pb);
                    map = MoveToFront(map, pa);
                }

                for (var plane = 0; plane < Bpp; plane++)
                {
                    var bit = Bpp > 1 ? 1 << plane : 1 << (pixel & 3);
                    var history = (bit - 1) & _output;
                    var set = 0;
                    if (Bpp == 1)
                    {
                        set = pixel >= 4 ? 1 : 0;
                    }

                    if (Bpp == 2)
                    {
                        set = diff;
                    }

                    if (plane >= 2 && history <= 1)
                    {
                        set = diff;
                    }

                    var context = set * 15 + bit + history - 1;
                    var model = Evolution[_prediction[context]];
                    var lpsOffset = (_range - model.Probability) & 0xFF;
                    var symbol = _input >= (lpsOffset << 8) ? Lps : Mps; // only the top byte is compared
                    _output = ((_output << 1) | (symbol ^ _swap[context])) & 0xFF;
                    if (symbol == Mps)
                    {
                        _range = lpsOffset;
                    }
                    else
                    {
                        _range = (_range - lpsOffset) & 0xFFFF;
                        _input = (_input - (lpsOffset << 8)) & 0xFFFF;
                    }

                    while (_range <= Max / 2)
                    {
                        _prediction[context] = symbol == Mps ? model.NextMps : model.NextLps;
                        _range = (_range << 1) & 0xFFFF;
                        _input = (_input << 1) & 0xFFFF;
                        if (--_bits == 0)
                        {
                            _bits = 8;
                            _input = (_input + Next()) & 0xFFFF;
                        }
                    }

                    if (symbol == Lps && model.Probability > Half)
                    {
                        _swap[context] ^= 1;
                    }
                }

                var index = _output & ((1 << Bpp) - 1);
                if (Bpp == 1)
                {
                    index ^= (int)((_pixels >> 15) & 1);
                }

                _pixels = (_pixels << Bpp) | ((map >> (4 * index)) & 15);
            }

            Result = Bpp switch
            {
                1 => (uint)_pixels,
                2 => Deinterleave(_pixels, 16),
                _ => Deinterleave(Deinterleave(_pixels, 32), 32),
            };
        }

        public void SaveState(StateWriter writer)
        {
            writer.WriteBytes(_prediction);
            writer.WriteBytes(_swap);
            writer.WriteInt32(Bpp);
            writer.WriteInt32(_offset);
            writer.WriteInt32(_bits);
            writer.WriteInt32(_range);
            writer.WriteInt32(_input);
            writer.WriteInt32(_output);
            writer.WriteUInt64(_pixels);
            writer.WriteUInt64(_colormap);
            writer.WriteUInt32(Result);
        }

        public void LoadState(StateReader reader)
        {
            reader.ReadBytes(_prediction);
            reader.ReadBytes(_swap);
            for (var i = 0; i < _prediction.Length; i++)
            {
                _prediction[i] = (byte)(_prediction[i] % Evolution.Length);
                _swap[i] &= 1;
            }

            Bpp = reader.ReadInt32() switch { 2 => 2, 4 => 4, _ => 1 };
            _offset = reader.ReadInt32();
            _bits = Math.Clamp(reader.ReadInt32(), 1, 8);
            _range = reader.ReadInt32() & 0xFFFF;
            _input = reader.ReadInt32() & 0xFFFF;
            _output = reader.ReadInt32() & 0xFF;
            _pixels = reader.ReadUInt64();
            _colormap = reader.ReadUInt64();
            Result = reader.ReadUInt32();
        }
    }
}
