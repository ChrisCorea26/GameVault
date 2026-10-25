using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 28, Action 53 (NESdev Wiki: "Action 53"): a register select at $5000-$5FFF and data at $8000-$FFFF.
/// The four registers are CHR, inner PRG bank, mode (mirroring, PRG mode, game size) and outer PRG bank.
/// </summary>
public sealed class Action53 : MapperBase
{
    private byte _select;
    private byte _chr;
    private byte _inner;
    private byte _mode;
    private byte _outer = 0xFF;

    public Action53(INesRom rom)
        : base(rom with { ChrRamSize = Math.Max(rom.ChrRamSize, rom.ChrRom.Length == 0 ? 0x8000 : 0) }, "A053") =>
        Apply();

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x5000 and < 0x6000)
        {
            _select = (byte)(value & 0x81);
            return;
        }

        base.CpuWrite(address, value);
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (_select)
        {
            case 0x00:
                _chr = value;
                SetOneScreenBit(value);
                break;
            case 0x01:
                _inner = value;
                SetOneScreenBit(value);
                break;
            case 0x80:
                _mode = value;
                break;
            default:
                _outer = value;
                break;
        }

        Apply();
    }

    // In the one-screen modes, writes to the CHR and inner-bank registers also pick the screen with bit 4.
    private void SetOneScreenBit(byte value)
    {
        if ((_mode & 2) == 0)
        {
            _mode = (byte)((_mode & ~1) | ((value >> 4) & 1));
        }
    }

    private int Prg16K(int a14)
    {
        var mask = (2 << ((_mode >> 4) & 3)) - 1; // game size in 16 KB banks, minus one
        var outer = _outer << 1;
        var prgMode = (_mode >> 2) & 3;
        int current;
        if ((prgMode & 2) == 0)
        {
            current = (_inner << 1) | a14;
        }
        else if ((prgMode & 1) != a14)
        {
            current = _inner; // the switchable side: $8000 in mode 3, $C000 in mode 2
        }
        else
        {
            return outer | a14; // the fixed side: the first or last 16 KB of the outer 32 KB bank
        }

        return (outer & ~mask) | (current & mask);
    }

    private void Apply()
    {
        SetPrg16K(0, Prg16K(0));
        SetPrg16K(1, Prg16K(1));
        SetChr8K(_chr & 3);
        Mirroring = (_mode & 3) switch
        {
            0 => Mirroring.SingleScreenA,
            1 => Mirroring.SingleScreenB,
            2 => Mirroring.Vertical,
            _ => Mirroring.Horizontal,
        };
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteByte(_select);
        writer.WriteByte(_chr);
        writer.WriteByte(_inner);
        writer.WriteByte(_mode);
        writer.WriteByte(_outer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _select = reader.ReadByte();
        _chr = reader.ReadByte();
        _inner = reader.ReadByte();
        _mode = reader.ReadByte();
        _outer = reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 64, Tengen RAMBO-1 (NESdev Wiki: "RAMBO-1"): MMC3-like banking with a third PRG register, a 1 KB
/// CHR mode, and an IRQ counter clocked by A12 rises or by CPU cycles divided by 4.
/// </summary>
public sealed class Rambo1 : MapperBase
{
    private const int A12LowCycles = 4;

    private readonly byte[] _registers = new byte[16];
    private byte _select;
    private byte _latch;
    private int _counter;
    private bool _reload;
    private bool _enabled;
    private bool _cycleMode;
    private bool _irq;
    private int _prescaler;
    private long _cycle;
    private long _a12LowSince;

    public Rambo1(INesRom rom)
        : base(rom, "RMB1")
    {
        _registers[0] = 0;
        _registers[1] = 2;
        _registers[2] = 4;
        _registers[3] = 5;
        _registers[4] = 6;
        _registers[5] = 7;
        _registers[6] = 0;
        _registers[7] = 1;
        _registers[15] = 2;
        Apply();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override void OnCpuCycle()
    {
        _cycle++;
        if (_cycleMode && ++_prescaler == 4)
        {
            _prescaler = 0;
            ClockCounter();
        }
    }

    public override void OnA12Fall() => _a12LowSince = _cycle;

    public override void OnA12Rise()
    {
        if (!_cycleMode && _cycle - _a12LowSince >= A12LowCycles)
        {
            ClockCounter();
        }
    }

    private void ClockCounter()
    {
        if (_reload)
        {
            // A reload loads latch + 1 when the latch is non-zero (NESdev Wiki: "RAMBO-1", IRQ counter).
            _counter = _latch == 0 ? 0 : _latch + 1;
            _reload = false;
        }
        else if (_counter == 0)
        {
            _counter = _latch;
        }
        else
        {
            _counter--;
        }

        if (_counter == 0 && _enabled)
        {
            _irq = true;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xE001)
        {
            case 0x8000:
                _select = value;
                break;
            case 0x8001:
                _registers[_select & 0x0F] = value;
                break;
            case 0xA000:
                Mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                break;
            case 0xC000:
                _latch = value;
                return;
            case 0xC001:
                _cycleMode = (value & 1) != 0;
                _prescaler = 0;
                _reload = true;
                return;
            case 0xE000:
                _enabled = false;
                _irq = false;
                return;
            case 0xE001:
                _enabled = true;
                return;
        }

        Apply();
    }

    private void Apply()
    {
        var prgSwap = (_select & 0x40) != 0;
        SetPrg8K(0, prgSwap ? _registers[15] : _registers[6]);
        SetPrg8K(1, prgSwap ? _registers[6] : _registers[7]);
        SetPrg8K(2, prgSwap ? _registers[7] : _registers[15]);
        SetPrg8K(3, -1);

        var invert = (_select & 0x80) != 0 ? 4 : 0;
        if ((_select & 0x20) != 0)
        {
            SetChr1K(0 ^ invert, _registers[0]);
            SetChr1K(1 ^ invert, _registers[8]);
            SetChr1K(2 ^ invert, _registers[1]);
            SetChr1K(3 ^ invert, _registers[9]);
        }
        else
        {
            SetChr1K(0 ^ invert, _registers[0] & ~1);
            SetChr1K(1 ^ invert, _registers[0] | 1);
            SetChr1K(2 ^ invert, _registers[1] & ~1);
            SetChr1K(3 ^ invert, _registers[1] | 1);
        }

        for (var i = 0; i < 4; i++)
        {
            SetChr1K((4 + i) ^ invert, _registers[2 + i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteByte(_select);
        writer.WriteByte(_latch);
        writer.WriteInt32(_counter);
        writer.WriteBool(_reload);
        writer.WriteBool(_enabled);
        writer.WriteBool(_cycleMode);
        writer.WriteBool(_irq);
        writer.WriteInt32(_prescaler);
        writer.WriteInt64(_cycle);
        writer.WriteInt64(_a12LowSince);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_registers);
        _select = reader.ReadByte();
        _latch = reader.ReadByte();
        _counter = reader.ReadInt32();
        _reload = reader.ReadBool();
        _enabled = reader.ReadBool();
        _cycleMode = reader.ReadBool();
        _irq = reader.ReadBool();
        _prescaler = reader.ReadInt32();
        _cycle = reader.ReadInt64();
        _a12LowSince = reader.ReadInt64();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 68, Sunsoft-4 (NESdev Wiki: "INES Mapper 068"): four 2 KB CHR banks, 16 KB PRG, and nametables
/// that can come from CHR-ROM (two 1 KB banks from $C000/$D000, with bit 7 forced on).
/// </summary>
public sealed class Sunsoft4 : MapperBase
{
    private readonly byte[] _chr = new byte[4];
    private readonly byte[] _nametableBanks = new byte[2];
    private byte _control;
    private byte _prg;

    public Sunsoft4(INesRom rom)
        : base(rom, "SUN4") => Apply();

    public override bool HasNametableHooks => true;

    private bool RomNametables => (_control & 0x10) != 0;

    public override byte ReadNametable(ushort address, byte[] ciram)
    {
        if (!RomNametables)
        {
            return base.ReadNametable(address, ciram);
        }

        var bank = _nametableBanks[NametablePage((address >> 10) & 3)] | 0x80;
        return Chr[((bank % Math.Max(1, Chr.Length >> 10)) << 10) | (address & 0x3FF)];
    }

    public override void WriteNametable(ushort address, byte value, byte[] ciram)
    {
        if (!RomNametables)
        {
            base.WriteNametable(address, value, ciram);
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000 or 0x9000 or 0xA000 or 0xB000:
                _chr[(address >> 12) & 3] = value;
                break;
            case 0xC000 or 0xD000:
                _nametableBanks[(address >> 12) & 1] = (byte)(value & 0x7F);
                break;
            case 0xE000:
                _control = value;
                break;
            case 0xF000:
                _prg = value;
                break;
        }

        Apply();
    }

    private void Apply()
    {
        for (var i = 0; i < 4; i++)
        {
            SetChr2K(i, _chr[i]);
        }

        SetPrg16K(0, _prg & 0x0F);
        SetPrg16K(1, -1);
        PrgRamReadable = PrgRamWritable = (_prg & 0x10) != 0;
        Mirroring = (_control & 3) switch
        {
            0 => Mirroring.Vertical,
            1 => Mirroring.Horizontal,
            2 => Mirroring.SingleScreenA,
            _ => Mirroring.SingleScreenB,
        };
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_nametableBanks);
        writer.WriteByte(_control);
        writer.WriteByte(_prg);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_nametableBanks);
        _control = reader.ReadByte();
        _prg = reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 82, Taito X1-017 (NESdev Wiki: "INES Mapper 082"): registers at $7EF0-$7EFF, CHR A12 inversion,
/// 5 KB of PRG-RAM at $6000-$73FF in three chunks, each unlocked by its own magic value.
/// </summary>
public sealed class TaitoX1017 : MapperBase
{
    private static readonly byte[] UnlockValues = [0xCA, 0x69, 0x84];

    private readonly byte[] _chr = new byte[6];
    private readonly byte[] _prg = new byte[3];
    private readonly byte[] _ram = new byte[0x1400];
    private readonly bool[] _ramEnabled = new bool[3];
    private bool _chrInvert;

    public TaitoX1017(INesRom rom)
        : base(rom, "X117")
    {
        _prg[1] = 1;
        _prg[2] = 2;
        Apply();
    }

    public override Span<byte> BatteryRam => _ram;

    private static int Chunk(ushort address) => address < 0x6800 ? 0 : address < 0x7000 ? 1 : 2;

    public override byte CpuRead(ushort address, byte openBus)
    {
        if (address >= 0x8000)
        {
            return ReadPrgRom(address);
        }

        if (address is >= 0x6000 and < 0x7400 && _ramEnabled[Chunk(address)])
        {
            return _ram[address - 0x6000];
        }

        return openBus;
    }

    public override void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x6000 and < 0x7400:
                if (_ramEnabled[Chunk(address)])
                {
                    _ram[address - 0x6000] = value;
                }

                return;
            case >= 0x7EF0 and <= 0x7EF5:
                _chr[address - 0x7EF0] = value;
                break;
            case 0x7EF6:
                Mirroring = (value & 1) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;
                _chrInvert = (value & 2) != 0;
                break;
            case >= 0x7EF7 and <= 0x7EF9:
                _ramEnabled[address - 0x7EF7] = value == UnlockValues[address - 0x7EF7];
                return;
            case >= 0x7EFA and <= 0x7EFC:
                _prg[address - 0x7EFA] = (byte)(value >> 2);
                break;
            default:
                return;
        }

        Apply();
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    private void Apply()
    {
        SetPrg8K(0, _prg[0]);
        SetPrg8K(1, _prg[1]);
        SetPrg8K(2, _prg[2]);
        SetPrg8K(3, -1);

        var invert = _chrInvert ? 4 : 0;
        SetChr1K(0 ^ invert, _chr[0] & ~1);
        SetChr1K(1 ^ invert, _chr[0] | 1);
        SetChr1K(2 ^ invert, _chr[1] & ~1);
        SetChr1K(3 ^ invert, _chr[1] | 1);
        for (var i = 0; i < 4; i++)
        {
            SetChr1K((4 + i) ^ invert, _chr[2 + i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_prg);
        writer.WriteBytes(_ram);
        for (var i = 0; i < 3; i++)
        {
            writer.WriteBool(_ramEnabled[i]);
        }

        writer.WriteBool(_chrInvert);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_prg);
        reader.ReadBytes(_ram);
        for (var i = 0; i < 3; i++)
        {
            _ramEnabled[i] = reader.ReadBool();
        }

        _chrInvert = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 228, Active Enterprises (Action 52, Cheetahmen II) (NESdev Wiki: "INES Mapper 228"). The written
/// address holds mirroring, PRG chip, PRG page and mode and the high CHR bits; data bits 0-1 are the low CHR
/// bits. Chip 3 maps to the third 512 KB chip (chip 2 is absent). Four 4-bit RAM cells sit at $4020-$5FFF.
/// </summary>
public sealed class Mapper228 : MapperBase
{
    private readonly byte[] _nibbles = new byte[4];
    private ushort _address;
    private byte _data;

    public Mapper228(INesRom rom)
        : base(rom, "M228") => Apply();

    public override byte CpuRead(ushort address, byte openBus) =>
        address switch
        {
            >= 0x8000 => ReadPrgRom(address),
            >= 0x4020 and < 0x6000 => (byte)((openBus & 0xF0) | _nibbles[address & 3]),
            _ => openBus,
        };

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x4020 and < 0x6000)
        {
            _nibbles[address & 3] = (byte)(value & 0x0F);
            return;
        }

        if (address >= 0x8000)
        {
            WriteRegister(address, value);
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        _address = address;
        _data = value;
        Apply();
    }

    private void Apply()
    {
        var chip = (_address >> 11) & 3;
        if (chip == 3)
        {
            chip = 2;
        }

        var page = (chip << 5) | ((_address >> 6) & 0x1F);
        if ((_address & 0x20) != 0)
        {
            SetPrg16K(0, page);
            SetPrg16K(1, page);
        }
        else
        {
            SetPrg16K(0, page & ~1);
            SetPrg16K(1, page | 1);
        }

        SetChr8K(((_address & 0x0F) << 2) | (_data & 3));
        Mirroring = (_address & 0x2000) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_nibbles);
        writer.WriteUInt16(_address);
        writer.WriteByte(_data);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_nibbles);
        _address = reader.ReadUInt16();
        _data = reader.ReadByte();
        Apply();
    }
}
