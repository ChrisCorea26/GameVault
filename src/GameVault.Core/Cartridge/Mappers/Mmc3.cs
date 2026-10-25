using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 4, MMC3 and MMC6 (NESdev Wiki: "MMC3", "MMC6"): 8 KB PRG and 1/2 KB CHR banking, and a scanline
/// counter clocked by rising edges of PPU A12.
/// </summary>
public sealed class Mmc3 : MapperBase
{
    /// <summary>
    /// A12 must stay low for a while before a rise counts (the MMC3 counts M2 edges while A12 is low). That
    /// filters the short lows between pattern fetches. The longest such low is 9 dots (3 CPU cycles), from the
    /// garbage nametable fetches at dots 337-340 to the first background pattern fetch at dot 5, so 4 cycles
    /// are required (NESdev Wiki: "MMC3", IRQ specifics; Mesen uses a similar 10-dot window).
    /// </summary>
    private const int A12LowCycles = 4;

    private readonly byte[] _registers = new byte[8];
    private readonly bool _isMmc6;
    private readonly bool _oldIrqBehavior;
    private readonly byte[] _mmc6Ram = new byte[0x400];
    private byte _bankSelect;
    private byte _ramProtect = 0x80; // power-on state is undefined; start with PRG-RAM enabled
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _irq;
    private long _cycle;
    private long _a12LowSince;

    public Mmc3(INesRom rom)
        : base(rom, "MMC3")
    {
        _isMmc6 = rom.Submapper == 1;

        // MMC6 and the NEC-made "MMC3A" use the old IRQ behavior; NES 2.0 submapper 4 selects it.
        _oldIrqBehavior = _isMmc6 || rom.Submapper == 4;
        _registers[6] = 0;
        _registers[7] = 1;
        UpdateBanks();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override Span<byte> BatteryRam => _isMmc6 ? _mmc6Ram : PrgRam;

    public override void OnCpuCycle() => _cycle++;

    public override void OnA12Fall() => _a12LowSince = _cycle;

    public override void OnA12Rise()
    {
        if (_cycle - _a12LowSince >= A12LowCycles)
        {
            ClockIrqCounter();
        }
    }

    private void ClockIrqCounter()
    {
        var before = _irqCounter;
        var reloaded = _irqReload;
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
        }
        else
        {
            _irqCounter--;
        }

        _irqReload = false;

        // New behavior: fires whenever the counter is 0 after clocking. Old behavior: only when it got to 0
        // by decrementing or by an explicit reload.
        if (_irqCounter == 0 && _irqEnabled && (!_oldIrqBehavior || before != 0 || reloaded))
        {
            _irq = true;
        }
    }

    public override byte CpuRead(ushort address, byte openBus)
    {
        if (!_isMmc6 || address is < 0x6000 or >= 0x8000)
        {
            return base.CpuRead(address, openBus);
        }

        // MMC6: 1 KB of internal RAM at $7000-$7FFF (mirrored), split into two 512-byte halves.
        if (address < 0x7000 || (_bankSelect & 0x20) == 0)
        {
            return openBus;
        }

        var high = (address & 0x200) != 0;
        var lowReadable = (_ramProtect & 0x20) != 0;
        var highReadable = (_ramProtect & 0x80) != 0;
        if (!lowReadable && !highReadable)
        {
            return openBus;
        }

        return (high ? highReadable : lowReadable) ? _mmc6Ram[address & 0x3FF] : (byte)0;
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (!_isMmc6 || address is < 0x6000 or >= 0x8000)
        {
            base.CpuWrite(address, value);
            return;
        }

        if (address < 0x7000 || (_bankSelect & 0x20) == 0)
        {
            return;
        }

        var high = (address & 0x200) != 0;
        var writable = high
            ? (_ramProtect & 0xC0) == 0xC0
            : (_ramProtect & 0x30) == 0x30;
        if (writable)
        {
            _mmc6Ram[address & 0x3FF] = value;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        var odd = (address & 1) != 0;
        switch (address & 0xE000)
        {
            case 0x8000:
                if (odd)
                {
                    _registers[_bankSelect & 7] = value;
                }
                else
                {
                    _bankSelect = value;
                }

                UpdateBanks();
                break;
            case 0xA000:
                if (odd)
                {
                    _ramProtect = value;
                    UpdateBanks();
                }
                else if (HeaderMirroring != Mirroring.FourScreen)
                {
                    Mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                }

                break;
            case 0xC000:
                if (odd)
                {
                    _irqCounter = 0;
                    _irqReload = true;
                }
                else
                {
                    _irqLatch = value;
                }

                break;
            default:
                if (odd)
                {
                    _irqEnabled = true;
                }
                else
                {
                    _irqEnabled = false;
                    _irq = false;
                }

                break;
        }
    }

    private void UpdateBanks()
    {
        var prgSwap = (_bankSelect & 0x40) != 0;
        SetPrg8K(prgSwap ? 2 : 0, _registers[6]);
        SetPrg8K(1, _registers[7]);
        SetPrg8K(prgSwap ? 0 : 2, -2);
        SetPrg8K(3, -1);

        // CHR inversion swaps the 2 KB and 1 KB halves (A12 inversion).
        var flip = (_bankSelect & 0x80) != 0 ? 4 : 0;
        SetChr1K(0 ^ flip, _registers[0] & 0xFE);
        SetChr1K(1 ^ flip, _registers[0] | 1);
        SetChr1K(2 ^ flip, _registers[1] & 0xFE);
        SetChr1K(3 ^ flip, _registers[1] | 1);
        SetChr1K(4 ^ flip, _registers[2]);
        SetChr1K(5 ^ flip, _registers[3]);
        SetChr1K(6 ^ flip, _registers[4]);
        SetChr1K(7 ^ flip, _registers[5]);

        if (!_isMmc6)
        {
            // $A001: bit 7 enables PRG-RAM, bit 6 write-protects it.
            PrgRamReadable = (_ramProtect & 0x80) != 0;
            PrgRamWritable = (_ramProtect & 0xC0) == 0x80;
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteBytes(_mmc6Ram);
        writer.WriteByte(_bankSelect);
        writer.WriteByte(_ramProtect);
        writer.WriteByte(_irqLatch);
        writer.WriteByte(_irqCounter);
        writer.WriteBool(_irqReload);
        writer.WriteBool(_irqEnabled);
        writer.WriteBool(_irq);
        writer.WriteByte((byte)Mirroring);
        writer.WriteInt64(_cycle);
        writer.WriteInt64(_a12LowSince);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_registers);
        reader.ReadBytes(_mmc6Ram);
        _bankSelect = reader.ReadByte();
        _ramProtect = reader.ReadByte();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqReload = reader.ReadBool();
        _irqEnabled = reader.ReadBool();
        _irq = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        _cycle = reader.ReadInt64();
        _a12LowSince = reader.ReadInt64();
        UpdateBanks();
    }
}
