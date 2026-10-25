using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 5, MMC5 (NESdev Wiki: "MMC5"): four PRG modes with ROM or RAM in each 8 KB slot, four CHR modes with
/// separate sprite and background sets for 8×16 sprites, 1 KB of ExRAM (nametable, extended attributes,
/// or plain RAM), fill mode, a scanline IRQ found by watching nametable fetches, and an 8×8 multiplier.
/// Not emulated yet: the vertical split screen and the MMC5's own sound (SPEC §12 Phase 7).
/// </summary>
public sealed class Mmc5 : MapperBase
{
    private const int PrgRamSize = 0x10000;

    private readonly byte[] _prgRam = new byte[PrgRamSize];
    private readonly byte[] _exRam = new byte[0x400];
    private readonly byte[] _prgRegs = new byte[5]; // $5113-$5117
    private readonly int[] _chrA = new int[8];       // $5120-$5127 (sprites, or everything in 8x8 mode)
    private readonly int[] _chrB = new int[4];       // $5128-$512B (background in 8x16 mode)
    private readonly int[] _prgOffsets = new int[4];
    private readonly bool[] _prgIsRam = new bool[4];
    private int _prgMode = 3;
    private int _chrMode;
    private byte _ramProtect1;
    private byte _ramProtect2;
    private int _exRamMode;
    private byte _ntMapping;
    private byte _fillTile;
    private byte _fillAttribute;
    private int _chrUpper;
    private bool _lastWrittenB;
    private byte _multiplicand = 0xFF;
    private byte _multiplier = 0xFF;

    // PPU snooping.
    private bool _tallSprites;
    private bool _rendering;

    // Scanline detection (NESdev Wiki: "MMC5", scanline detection and IRQ).
    private byte _irqCompare;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _inFrame;
    private int _scanline;
    private ushort _lastNtAddress;
    private int _sameNtReads;
    private int _ntReadsThisLine;
    private int _cyclesSincePpuRead;

    // Extended attribute mode: the ExRAM byte of the tile being fetched.
    private byte _exAttribute;

    public Mmc5(INesRom rom)
        : base(rom, "MMC5")
    {
        _prgRegs[4] = 0xFF;
        UpdatePrg();
        UpdateChr();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irqPending && _irqEnabled;

    public override bool HasNametableHooks => true;

    public override bool SnoopsPpuRegisters => true;

    public override Span<byte> BatteryRam => _prgRam;

    public override void OnCpuCycle()
    {
        // No PPU reads for a few cycles means rendering stopped (vblank or disabled): leave the frame.
        if (++_cyclesSincePpuRead > 3)
        {
            _inFrame = false;
            _sameNtReads = 0;
        }
    }

    public override void OnPpuRegisterWrite(ushort register, byte value)
    {
        switch (register)
        {
            case 0x2000:
                _tallSprites = (value & 0x20) != 0;
                break;
            case 0x2001:
                _rendering = (value & 0x18) != 0;
                if (!_rendering)
                {
                    _inFrame = false;
                }

                break;
        }
    }

    // ------------------------------------------------------------------ CPU

    public override byte CpuRead(ushort address, byte openBus)
    {
        switch (address)
        {
            case 0x5204:
            {
                var status = (byte)((_irqPending ? 0x80 : 0) | (_inFrame ? 0x40 : 0));
                _irqPending = false;
                return status;
            }

            case 0x5205:
                return (byte)(_multiplicand * _multiplier);
            case 0x5206:
                return (byte)((_multiplicand * _multiplier) >> 8);
            case >= 0x5C00 and < 0x6000:
                return _exRamMode >= 2 ? _exRam[address - 0x5C00] : openBus;
            case >= 0x6000 and < 0x8000:
                return _prgRam[(((_prgRegs[0] & 0x07) << 13) | (address & 0x1FFF)) & (PrgRamSize - 1)];
            case >= 0x8000:
            {
                var slot = (address >> 13) & 3;
                var offset = _prgOffsets[slot] + (address & 0x1FFF);
                return _prgIsRam[slot] ? _prgRam[offset & (PrgRamSize - 1)] : PrgRom[offset % PrgRom.Length];
            }

            default:
                return openBus;
        }
    }

    public override void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case 0x5100:
                _prgMode = value & 3;
                UpdatePrg();
                return;
            case 0x5101:
                _chrMode = value & 3;
                UpdateChr();
                return;
            case 0x5102:
                _ramProtect1 = (byte)(value & 3);
                return;
            case 0x5103:
                _ramProtect2 = (byte)(value & 3);
                return;
            case 0x5104:
                _exRamMode = value & 3;
                return;
            case 0x5105:
                _ntMapping = value;
                return;
            case 0x5106:
                _fillTile = value;
                return;
            case 0x5107:
                _fillAttribute = (byte)((value & 3) * 0x55);
                return;
            case >= 0x5113 and <= 0x5117:
                _prgRegs[address - 0x5113] = value;
                UpdatePrg();
                return;
            case >= 0x5120 and <= 0x5127:
                _chrA[address - 0x5120] = value | (_chrUpper << 8);
                _lastWrittenB = false;
                UpdateChr();
                return;
            case >= 0x5128 and <= 0x512B:
                _chrB[address - 0x5128] = value | (_chrUpper << 8);
                _lastWrittenB = true;
                UpdateChr();
                return;
            case 0x5130:
                _chrUpper = value & 3;
                return;
            case 0x5203:
                _irqCompare = value;
                return;
            case 0x5204:
                _irqEnabled = (value & 0x80) != 0;
                return;
            case 0x5205:
                _multiplicand = value;
                return;
            case 0x5206:
                _multiplier = value;
                return;
            case >= 0x5C00 and < 0x6000:
                if (_exRamMode != 3)
                {
                    // Modes 0 and 1 only take writes while rendering; otherwise the chip stores 0.
                    _exRam[address - 0x5C00] = _exRamMode == 2 || _inFrame ? value : (byte)0;
                }

                return;
            case >= 0x6000 and < 0x8000:
                if (RamWritable)
                {
                    _prgRam[(((_prgRegs[0] & 0x07) << 13) | (address & 0x1FFF)) & (PrgRamSize - 1)] = value;
                }

                return;
            case >= 0x8000:
            {
                var slot = (address >> 13) & 3;
                if (_prgIsRam[slot] && RamWritable)
                {
                    _prgRam[(_prgOffsets[slot] + (address & 0x1FFF)) & (PrgRamSize - 1)] = value;
                }

                return;
            }
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    private bool RamWritable => _ramProtect1 == 2 && _ramProtect2 == 1;

    // ------------------------------------------------------------------ PPU

    public override byte ReadNametable(ushort address, byte[] ciram)
    {
        _cyclesSincePpuRead = 0;
        DetectScanline(address);

        var offset = address & 0x3FF;
        var isAttribute = offset >= 0x3C0;
        if (_exRamMode == 1 && _ntReadsThisLine is <= 64 or > 80)
        {
            // Extended attributes: each tile's palette and 4 KB CHR bank come from ExRAM.
            if (!isAttribute)
            {
                _exAttribute = _exRam[offset];
            }
            else
            {
                return (byte)(((_exAttribute >> 6) & 3) * 0x55);
            }
        }

        return ((_ntMapping >> (((address >> 10) & 3) * 2)) & 3) switch
        {
            0 => ciram[offset],
            1 => ciram[0x400 | offset],
            2 => _exRamMode <= 1 ? _exRam[offset] : (byte)0,
            _ => isAttribute ? _fillAttribute : _fillTile,
        };
    }

    public override void WriteNametable(ushort address, byte value, byte[] ciram)
    {
        var offset = address & 0x3FF;
        switch ((_ntMapping >> (((address >> 10) & 3) * 2)) & 3)
        {
            case 0:
                ciram[offset] = value;
                break;
            case 1:
                ciram[0x400 | offset] = value;
                break;
            case 2 when _exRamMode <= 1:
                _exRam[offset] = value;
                break;
        }
    }

    public override byte PpuRead(ushort address)
    {
        _cyclesSincePpuRead = 0;

        // Any other fetch between nametable reads breaks the "same address three times" pattern, so the
        // paired dummy fetches during sprite loading don't look like a new scanline.
        _lastNtAddress = 0xFFFF;
        _sameNtReads = 0;
        var spriteFetch = _ntReadsThisLine is > 64 and <= 80;
        if (_exRamMode == 1 && !spriteFetch && _inFrame)
        {
            var bank = (_exAttribute & 0x3F) | (_chrUpper << 6);
            return Chr[((bank << 12) | (address & 0x0FFF)) % Chr.Length];
        }

        // 8x16 sprites: sprites use set A, the background set B. 8x8: the set written last, for both.
        var useB = _tallSprites ? !spriteFetch && _rendering : _lastWrittenB;
        if (useB)
        {
            return Chr[ChrOffsetB(address) % Chr.Length];
        }

        return base.PpuRead(address);
    }

    private void DetectScanline(ushort address)
    {
        if (address == _lastNtAddress && address < 0x3000)
        {
            if (++_sameNtReads == 2)
            {
                // Third identical fetch: the start of a new scanline.
                if (!_inFrame)
                {
                    _inFrame = true;
                    _scanline = 0;
                }
                else if (++_scanline == _irqCompare && _irqCompare != 0)
                {
                    _irqPending = true;
                }

                _ntReadsThisLine = 0;
            }
        }
        else
        {
            _sameNtReads = 0;
        }

        _lastNtAddress = address;
        _ntReadsThisLine++;
    }

    // ------------------------------------------------------------------ banking

    private void UpdatePrg()
    {
        // Each mode maps the registers onto the four 8 KB slots (NESdev Wiki: "MMC5", PRG mode).
        switch (_prgMode)
        {
            case 0:
                Map32K(_prgRegs[4]);
                break;
            case 1:
                Map16K(0, _prgRegs[2]);
                Map16K(2, _prgRegs[4]);
                break;
            case 2:
                Map16K(0, _prgRegs[2]);
                Map8K(2, _prgRegs[3]);
                Map8K(3, _prgRegs[4]);
                break;
            default:
                Map8K(0, _prgRegs[1]);
                Map8K(1, _prgRegs[2]);
                Map8K(2, _prgRegs[3]);
                Map8K(3, _prgRegs[4]);
                break;
        }

        // $5117 is always ROM.
        _prgIsRam[3] = false;
    }

    private void Map8K(int slot, byte value)
    {
        _prgIsRam[slot] = (value & 0x80) == 0;
        _prgOffsets[slot] = _prgIsRam[slot] ? (value & 0x07) << 13 : (value & 0x7F) << 13;
    }

    private void Map16K(int slot, byte value)
    {
        Map8K(slot, (byte)(value & 0xFE));
        Map8K(slot + 1, (byte)(value | 1));
    }

    private void Map32K(byte value)
    {
        for (var i = 0; i < 4; i++)
        {
            Map8K(i, (byte)((value & 0xFC) | i));
        }
    }

    private void UpdateChr()
    {
        switch (_chrMode)
        {
            case 0:
                SetChr8K(_chrA[7]);
                break;
            case 1:
                SetChr4K(0, _chrA[3]);
                SetChr4K(1, _chrA[7]);
                break;
            case 2:
                SetChr2K(0, _chrA[1]);
                SetChr2K(1, _chrA[3]);
                SetChr2K(2, _chrA[5]);
                SetChr2K(3, _chrA[7]);
                break;
            default:
                for (var i = 0; i < 8; i++)
                {
                    SetChr1K(i, _chrA[i]);
                }

                break;
        }
    }

    /// <summary>Set B covers $0000-$0FFF and is mirrored at $1000-$1FFF.</summary>
    private int ChrOffsetB(ushort address)
    {
        var a = address & 0x0FFF;
        return _chrMode switch
        {
            0 => (_chrB[3] << 13) | (address & 0x1FFF),
            1 => (_chrB[3] << 12) | a,
            2 => (_chrB[(a >> 11) * 2 + 1] << 11) | (a & 0x7FF),
            _ => (_chrB[a >> 10] << 10) | (a & 0x3FF),
        };
    }

    // ------------------------------------------------------------------ state

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_prgRam);
        writer.WriteBytes(_exRam);
        writer.WriteBytes(_prgRegs);
        foreach (var bank in _chrA)
        {
            writer.WriteInt32(bank);
        }

        foreach (var bank in _chrB)
        {
            writer.WriteInt32(bank);
        }

        writer.WriteInt32(_prgMode);
        writer.WriteInt32(_chrMode);
        writer.WriteByte(_ramProtect1);
        writer.WriteByte(_ramProtect2);
        writer.WriteInt32(_exRamMode);
        writer.WriteByte(_ntMapping);
        writer.WriteByte(_fillTile);
        writer.WriteByte(_fillAttribute);
        writer.WriteInt32(_chrUpper);
        writer.WriteBool(_lastWrittenB);
        writer.WriteByte(_multiplicand);
        writer.WriteByte(_multiplier);
        writer.WriteBool(_tallSprites);
        writer.WriteBool(_rendering);
        writer.WriteByte(_irqCompare);
        writer.WriteBool(_irqEnabled);
        writer.WriteBool(_irqPending);
        writer.WriteBool(_inFrame);
        writer.WriteInt32(_scanline);
        writer.WriteUInt16(_lastNtAddress);
        writer.WriteInt32(_sameNtReads);
        writer.WriteInt32(_ntReadsThisLine);
        writer.WriteInt32(_cyclesSincePpuRead);
        writer.WriteByte(_exAttribute);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_prgRam);
        reader.ReadBytes(_exRam);
        reader.ReadBytes(_prgRegs);
        for (var i = 0; i < 8; i++)
        {
            _chrA[i] = reader.ReadInt32();
        }

        for (var i = 0; i < 4; i++)
        {
            _chrB[i] = reader.ReadInt32();
        }

        _prgMode = reader.ReadInt32();
        _chrMode = reader.ReadInt32();
        _ramProtect1 = reader.ReadByte();
        _ramProtect2 = reader.ReadByte();
        _exRamMode = reader.ReadInt32();
        _ntMapping = reader.ReadByte();
        _fillTile = reader.ReadByte();
        _fillAttribute = reader.ReadByte();
        _chrUpper = reader.ReadInt32();
        _lastWrittenB = reader.ReadBool();
        _multiplicand = reader.ReadByte();
        _multiplier = reader.ReadByte();
        _tallSprites = reader.ReadBool();
        _rendering = reader.ReadBool();
        _irqCompare = reader.ReadByte();
        _irqEnabled = reader.ReadBool();
        _irqPending = reader.ReadBool();
        _inFrame = reader.ReadBool();
        _scanline = reader.ReadInt32();
        _lastNtAddress = reader.ReadUInt16();
        _sameNtReads = reader.ReadInt32();
        _ntReadsThisLine = reader.ReadInt32();
        _cyclesSincePpuRead = reader.ReadInt32();
        _exAttribute = reader.ReadByte();
        UpdatePrg();
        UpdateChr();
    }
}
