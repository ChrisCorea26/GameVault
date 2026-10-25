using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>Mapper 13, CPROM (NESdev Wiki: "CPROM"): 16 KB CHR-RAM; $0000 fixed, $1000 switchable (bits 0-1). Bus conflicts.</summary>
public sealed class CpRom : LatchBoard
{
    public CpRom(INesRom rom)
        : base(rom with { ChrRamSize = Math.Max(rom.ChrRamSize, 0x4000) }, "CPRM") => Apply();

    protected override void WriteRegister(ushort address, byte value) => SetLatch(WithBusConflict(address, value));

    protected override void Apply()
    {
        SetChr4K(0, 0);
        SetChr4K(1, Latch & 3);
    }
}

/// <summary>
/// Mapper 30, UNROM 512 (NESdev Wiki: "UNROM 512"): bits 0-4 select 16 KB at $8000 (last fixed), bits 5-6
/// select 8 KB of the 32 KB CHR-RAM. Bus conflicts unless the board is flashable (battery bit).
/// </summary>
public sealed class Unrom512 : LatchBoard
{
    private readonly bool _flashable;

    public Unrom512(INesRom rom)
        : base(rom with { ChrRamSize = Math.Max(rom.ChrRamSize, 0x8000) }, "U512")
    {
        _flashable = rom.HasBattery;
        Apply();
    }

    protected override void WriteRegister(ushort address, byte value) =>
        SetLatch(_flashable ? value : WithBusConflict(address, value));

    protected override void Apply()
    {
        SetPrg16K(0, Latch & 0x1F);
        SetPrg16K(1, -1);
        SetChr8K((Latch >> 5) & 3);
    }
}

/// <summary>
/// Mapper 15, the "100-in-1 Contra Function 16" multicart (NESdev Wiki: "INES Mapper 015"): the address
/// picks one of four PRG layouts; data bits 0-5 pick a 16 KB bank, bit 6 the mirroring, bit 7 the 8 KB half.
/// </summary>
public sealed class Mapper15 : MapperBase
{
    private int _mode;
    private byte _value;

    public Mapper15(INesRom rom)
        : base(rom, "M015") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
        _mode = address & 3;
        _value = value;
        Apply();
    }

    private void Apply()
    {
        var bank = _value & 0x3F;
        var half = _value >> 7;
        switch (_mode)
        {
            case 0: // 32 KB
                SetPrg16K(0, bank & ~1);
                SetPrg16K(1, bank | 1);
                break;
            case 1: // UNROM-like: bank, then the last bank of its 128 KB block
                SetPrg16K(0, bank);
                SetPrg16K(1, bank | 7);
                break;
            case 2: // one 8 KB bank everywhere
                for (var slot = 0; slot < 4; slot++)
                {
                    SetPrg8K(slot, (bank << 1) | half);
                }

                break;
            default: // 16 KB mirrored
                SetPrg16K(0, bank);
                SetPrg16K(1, bank);
                break;
        }

        Mirroring = (_value & 0x40) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteInt32(_mode);
        writer.WriteByte(_value);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _mode = reader.ReadInt32();
        _value = reader.ReadByte();
        Apply();
    }
}

/// <summary>Mapper 32, Irem G-101 (NESdev Wiki: "INES Mapper 032"): two PRG registers with a swap mode, eight 1 KB CHR banks.</summary>
public sealed class IremG101 : MapperBase
{
    private readonly byte[] _chr = new byte[8];
    private byte _prg0;
    private byte _prg1 = 1;
    private bool _swap;

    public IremG101(INesRom rom)
        : base(rom, "G101") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000:
                _prg0 = (byte)(value & 0x1F);
                break;
            case 0x9000:
                _swap = (value & 2) != 0;
                Mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                break;
            case 0xA000:
                _prg1 = (byte)(value & 0x1F);
                break;
            case 0xB000:
                _chr[address & 7] = value;
                break;
        }

        Apply();
    }

    private void Apply()
    {
        SetPrg8K(_swap ? 2 : 0, _prg0);
        SetPrg8K(1, _prg1);
        SetPrg8K(_swap ? 0 : 2, -2);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _chr[i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteByte(_prg0);
        writer.WriteByte(_prg1);
        writer.WriteBool(_swap);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        _prg0 = reader.ReadByte();
        _prg1 = reader.ReadByte();
        _swap = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mappers 33 and 48, Taito TC0190 / TC0690 (NESdev Wiki: "INES Mapper 033", "INES Mapper 048"): two 8 KB
/// PRG banks, two 2 KB and four 1 KB CHR banks. The TC0690 adds an MMC3-style scanline IRQ (its latch is
/// written inverted) and moves the mirroring bit to $E000.
/// </summary>
public sealed class TaitoTc0190 : MapperBase
{
    private const int A12LowCycles = 4;

    private readonly bool _tc0690;
    private readonly byte[] _chr = new byte[6];
    private byte _prg0;
    private byte _prg1 = 1;
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _irq;
    private long _cycle;
    private long _a12LowSince;

    public TaitoTc0190(INesRom rom)
        : base(rom, rom.Mapper == 48 ? "T690" : "T190")
    {
        _tc0690 = rom.Mapper == 48;
        Apply();
    }

    public override bool HasCycleHooks => _tc0690;

    public override bool IrqPending => _irq;

    public override void OnCpuCycle() => _cycle++;

    public override void OnA12Fall() => _a12LowSince = _cycle;

    public override void OnA12Rise()
    {
        if (!_tc0690 || _cycle - _a12LowSince < A12LowCycles)
        {
            return;
        }

        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
        {
            _irq = true;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xE003)
        {
            case 0x8000:
                _prg0 = (byte)(value & 0x3F);
                if (!_tc0690)
                {
                    Mirroring = (value & 0x40) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                }

                break;
            case 0x8001:
                _prg1 = (byte)(value & 0x3F);
                break;
            case 0x8002 or 0x8003:
                _chr[address & 1] = value;
                break;
            case 0xA000 or 0xA001 or 0xA002 or 0xA003:
                _chr[2 + (address & 3)] = value;
                break;
            case 0xC000 when _tc0690:
                _irqLatch = (byte)(value ^ 0xFF);
                return;
            case 0xC001 when _tc0690:
                _irqCounter = 0;
                _irqReload = true;
                return;
            case 0xC002 when _tc0690:
                _irqEnabled = true;
                return;
            case 0xC003 when _tc0690:
                _irqEnabled = false;
                _irq = false;
                return;
            case 0xE000 when _tc0690:
                Mirroring = (value & 0x40) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                return;
        }

        Apply();
    }

    private void Apply()
    {
        SetPrg8K(0, _prg0);
        SetPrg8K(1, _prg1);
        SetPrg8K(2, -2);
        SetPrg8K(3, -1);
        SetChr2K(0, _chr[0]);
        SetChr2K(1, _chr[1]);
        for (var i = 0; i < 4; i++)
        {
            SetChr1K(4 + i, _chr[2 + i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteByte(_prg0);
        writer.WriteByte(_prg1);
        writer.WriteByte((byte)Mirroring);
        writer.WriteByte(_irqLatch);
        writer.WriteByte(_irqCounter);
        writer.WriteBool(_irqReload);
        writer.WriteBool(_irqEnabled);
        writer.WriteBool(_irq);
        writer.WriteInt64(_cycle);
        writer.WriteInt64(_a12LowSince);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        _prg0 = reader.ReadByte();
        _prg1 = reader.ReadByte();
        Mirroring = (Mirroring)reader.ReadByte();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqReload = reader.ReadBool();
        _irqEnabled = reader.ReadBool();
        _irq = reader.ReadBool();
        _cycle = reader.ReadInt64();
        _a12LowSince = reader.ReadInt64();
        Apply();
    }
}

/// <summary>
/// Mappers 65 (Irem H3001) and 67 (Sunsoft-3): banking plus a 16-bit IRQ counter that counts down every CPU
/// cycle (NESdev Wiki: "INES Mapper 065", "INES Mapper 067").
/// </summary>
public sealed class CycleIrqBoard : MapperBase
{
    private readonly bool _sunsoft3;
    private readonly byte[] _chr = new byte[8];
    private readonly byte[] _prg = [0, 1, 0xFE];
    private ushort _counter;
    private ushort _latch;
    private bool _enabled;
    private bool _irq;
    private bool _writeLow;

    public CycleIrqBoard(INesRom rom)
        : base(rom, rom.Mapper == 67 ? "SUN3" : "H301")
    {
        _sunsoft3 = rom.Mapper == 67;
        Apply();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override void OnCpuCycle()
    {
        if (!_enabled)
        {
            return;
        }

        if (_sunsoft3)
        {
            if (_counter-- == 0)
            {
                _irq = true;
                _enabled = false;
            }
        }
        else if (_counter > 0 && --_counter == 0)
        {
            _irq = true;
            _enabled = false;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        if (_sunsoft3)
        {
            WriteSunsoft3(address, value);
        }
        else
        {
            WriteIrem(address, value);
        }

        Apply();
    }

    private void WriteIrem(ushort address, byte value)
    {
        switch (address)
        {
            case 0x8000:
                _prg[0] = value;
                break;
            case 0x9001:
                Mirroring = (value & 0x80) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                break;
            case 0x9003:
                _enabled = (value & 0x80) != 0;
                _irq = false;
                break;
            case 0x9004:
                _counter = _latch;
                _irq = false;
                break;
            case 0x9005:
                _latch = (ushort)((_latch & 0x00FF) | (value << 8));
                break;
            case 0x9006:
                _latch = (ushort)((_latch & 0xFF00) | value);
                break;
            case 0xA000:
                _prg[1] = value;
                break;
            case 0xC000:
                _prg[2] = value;
                break;
            case >= 0xB000 and <= 0xB007:
                _chr[address & 7] = value;
                break;
        }
    }

    private void WriteSunsoft3(ushort address, byte value)
    {
        switch (address & 0xF800)
        {
            case 0x8800:
                _chr[0] = value;
                break;
            case 0x9800:
                _chr[1] = value;
                break;
            case 0xA800:
                _chr[2] = value;
                break;
            case 0xB800:
                _chr[3] = value;
                break;
            case 0xC800:
                _counter = _writeLow ? (ushort)((_counter & 0xFF00) | value) : (ushort)((_counter & 0x00FF) | (value << 8));
                _writeLow = !_writeLow;
                break;
            case 0xD800:
                _enabled = (value & 0x10) != 0;
                _writeLow = false;
                _irq = false;
                break;
            case 0xE800:
                Mirroring = (value & 3) switch
                {
                    0 => Mirroring.Vertical,
                    1 => Mirroring.Horizontal,
                    2 => Mirroring.SingleScreenA,
                    _ => Mirroring.SingleScreenB,
                };
                break;
            case 0xF800:
                _prg[0] = value;
                break;
        }
    }

    private void Apply()
    {
        if (_sunsoft3)
        {
            SetPrg16K(0, _prg[0]);
            SetPrg16K(1, -1);
            for (var i = 0; i < 4; i++)
            {
                SetChr2K(i, _chr[i]);
            }

            return;
        }

        SetPrg8K(0, _prg[0]);
        SetPrg8K(1, _prg[1]);
        SetPrg8K(2, _prg[2]);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _chr[i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_prg);
        writer.WriteUInt16(_counter);
        writer.WriteUInt16(_latch);
        writer.WriteBool(_enabled);
        writer.WriteBool(_irq);
        writer.WriteBool(_writeLow);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_prg);
        _counter = reader.ReadUInt16();
        _latch = reader.ReadUInt16();
        _enabled = reader.ReadBool();
        _irq = reader.ReadBool();
        _writeLow = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 73, Konami VRC3 (NESdev Wiki: "VRC3"): 16 KB PRG at $8000 and a 16-bit (or 8-bit) up-counting
/// CPU-cycle IRQ with a latch written four bits at a time.
/// </summary>
public sealed class Vrc3 : MapperBase
{
    private ushort _latch;
    private ushort _counter;
    private bool _enabled;
    private bool _enableAfterAck;
    private bool _eightBit;
    private bool _irq;
    private byte _prg;

    public Vrc3(INesRom rom)
        : base(rom, "VRC3") => Apply();

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq;

    public override void OnCpuCycle()
    {
        if (!_enabled)
        {
            return;
        }

        if (_eightBit)
        {
            var low = (byte)(_counter + 1);
            if (low == 0)
            {
                _irq = true;
                low = (byte)_latch;
            }

            _counter = (ushort)((_counter & 0xFF00) | low);
        }
        else if (++_counter == 0)
        {
            _irq = true;
            _counter = _latch;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        var nibble = value & 0x0F;
        switch (address & 0xF000)
        {
            case 0x8000:
                _latch = (ushort)((_latch & 0xFFF0) | nibble);
                break;
            case 0x9000:
                _latch = (ushort)((_latch & 0xFF0F) | (nibble << 4));
                break;
            case 0xA000:
                _latch = (ushort)((_latch & 0xF0FF) | (nibble << 8));
                break;
            case 0xB000:
                _latch = (ushort)((_latch & 0x0FFF) | (nibble << 12));
                break;
            case 0xC000:
                _eightBit = (value & 4) != 0;
                _enabled = (value & 2) != 0;
                _enableAfterAck = (value & 1) != 0;
                _irq = false;
                if (_enabled)
                {
                    _counter = _latch;
                }

                break;
            case 0xD000:
                _irq = false;
                _enabled = _enableAfterAck;
                break;
            case 0xF000:
                _prg = (byte)(value & 7);
                Apply();
                break;
        }
    }

    private void Apply()
    {
        SetPrg16K(0, _prg);
        SetPrg16K(1, -1);
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteUInt16(_latch);
        writer.WriteUInt16(_counter);
        writer.WriteBool(_enabled);
        writer.WriteBool(_enableAfterAck);
        writer.WriteBool(_eightBit);
        writer.WriteBool(_irq);
        writer.WriteByte(_prg);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _latch = reader.ReadUInt16();
        _counter = reader.ReadUInt16();
        _enabled = reader.ReadBool();
        _enableAfterAck = reader.ReadBool();
        _eightBit = reader.ReadBool();
        _irq = reader.ReadBool();
        _prg = reader.ReadByte();
        Apply();
    }
}

/// <summary>Mapper 75, Konami VRC1 (NESdev Wiki: "VRC1"): three 8 KB PRG banks and two 4 KB CHR banks with a fifth bit in $9000.</summary>
public sealed class Vrc1 : MapperBase
{
    private readonly byte[] _prg = [0, 1, 2];
    private int _chr0;
    private int _chr1;

    public Vrc1(INesRom rom)
        : base(rom, "VRC1") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0x8000:
                _prg[0] = (byte)(value & 0x0F);
                break;
            case 0x9000:
                if (HeaderMirroring != Mirroring.FourScreen)
                {
                    Mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                }

                _chr0 = (_chr0 & 0x0F) | ((value & 2) << 3);
                _chr1 = (_chr1 & 0x0F) | ((value & 4) << 2);
                break;
            case 0xA000:
                _prg[1] = (byte)(value & 0x0F);
                break;
            case 0xC000:
                _prg[2] = (byte)(value & 0x0F);
                break;
            case 0xE000:
                _chr0 = (_chr0 & 0x10) | (value & 0x0F);
                break;
            case 0xF000:
                _chr1 = (_chr1 & 0x10) | (value & 0x0F);
                break;
        }

        Apply();
    }

    private void Apply()
    {
        SetPrg8K(0, _prg[0]);
        SetPrg8K(1, _prg[1]);
        SetPrg8K(2, _prg[2]);
        SetPrg8K(3, -1);
        SetChr4K(0, _chr0);
        SetChr4K(1, _chr1);
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_prg);
        writer.WriteInt32(_chr0);
        writer.WriteInt32(_chr1);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_prg);
        _chr0 = reader.ReadInt32();
        _chr1 = reader.ReadInt32();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Namco 108 variants (NESdev Wiki: "INES Mapper 076/088/095/154"):
/// <list type="bullet">
/// <item>76: 2 KB CHR banks from R2-R5.</item>
/// <item>88: the left pattern table from the lower 64 KB, the right from the upper.</item>
/// <item>95: R0/R1 bit 5 picks the nametable for each half.</item>
/// <item>154: 88 plus one-screen mirroring from data bit 6.</item>
/// </list>
/// </summary>
public sealed class Namco108Variant : MapperBase
{
    private readonly int _board;
    private readonly byte[] _registers = [0, 2, 4, 5, 6, 7, 0, 1];
    private byte _select;

    public Namco108Variant(INesRom rom)
        : base(rom, "N108")
    {
        _board = rom.Mapper;
        Apply();
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        if (_board == 154)
        {
            Mirroring = (value & 0x40) != 0 ? Mirroring.SingleScreenB : Mirroring.SingleScreenA;
        }

        if (address >= 0xA000)
        {
            return;
        }

        if ((address & 1) == 0)
        {
            _select = (byte)(value & 7);
        }
        else
        {
            _registers[_select] = (byte)(value & (_select < 6 ? 0x3F : 0x0F));
        }

        Apply();
    }

    public override int NametablePage(int slot) =>
        _board == 95 ? (_registers[slot >> 1] >> 5) & 1 : base.NametablePage(slot);

    private void Apply()
    {
        SetPrg8K(0, _registers[6]);
        SetPrg8K(1, _registers[7]);
        SetPrg8K(2, -2);
        SetPrg8K(3, -1);
        if (_board == 76)
        {
            for (var i = 0; i < 4; i++)
            {
                SetChr2K(i, _registers[2 + i]);
            }

            return;
        }

        var high = _board is 88 or 154 ? 0x40 : 0;
        var lowMask = _board is 88 or 154 ? 0x3F : 0xFF;
        var r0 = _board == 95 ? _registers[0] & 0x1F : _registers[0] & lowMask;
        var r1 = _board == 95 ? _registers[1] & 0x1F : _registers[1] & lowMask;
        SetChr1K(0, r0 & ~1);
        SetChr1K(1, r0 | 1);
        SetChr1K(2, r1 & ~1);
        SetChr1K(3, r1 | 1);
        for (var i = 0; i < 4; i++)
        {
            SetChr1K(4 + i, _registers[2 + i] | high);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteByte(_select);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_registers);
        _select = reader.ReadByte();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>Mapper 152 (NESdev Wiki: "INES Mapper 152"): bits 4-6 PRG 16 KB, bits 0-3 CHR 8 KB, bit 7 one-screen page. Bus conflicts.</summary>
public sealed class Mapper152 : LatchBoard
{
    public Mapper152(INesRom rom)
        : base(rom, "M152") => Apply();

    protected override void WriteRegister(ushort address, byte value) => SetLatch(WithBusConflict(address, value));

    protected override void Apply()
    {
        SetPrg16K(0, (Latch >> 4) & 7);
        SetPrg16K(1, -1);
        SetChr8K(Latch & 0x0F);
        Mirroring = (Latch & 0x80) != 0 ? Mirroring.SingleScreenB : Mirroring.SingleScreenA;
    }
}

/// <summary>Mapper 180 (NESdev Wiki: "INES Mapper 180"): UNROM with the first bank fixed at $8000 and the switchable bank at $C000.</summary>
public sealed class Mapper180 : LatchBoard
{
    public Mapper180(INesRom rom)
        : base(rom, "M180") => Apply();

    protected override void Apply()
    {
        SetPrg16K(0, 0);
        SetPrg16K(1, Latch & 7);
    }
}

/// <summary>Mapper 184, Sunsoft-1 (NESdev Wiki: "INES Mapper 184"): $6000-$7FFF sets two 4 KB CHR banks.</summary>
public sealed class Sunsoft1 : LatchBoard
{
    public Sunsoft1(INesRom rom)
        : base(rom, "SUN1") => Apply();

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            SetLatch(value);
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    protected override void Apply()
    {
        SetChr4K(0, Latch & 7);
        SetChr4K(1, (Latch >> 4) & 7);
    }
}

/// <summary>
/// Mapper 185, CNROM with a CHR-disable copy protection (NESdev Wiki: "INES Mapper 185"): the latch only
/// enables CHR; the wrong value makes pattern reads return $FF. Submappers 4-7 name the enabling value.
/// </summary>
public sealed class Mapper185 : LatchBoard
{
    public Mapper185(INesRom rom)
        : base(rom, "M185") => Apply();

    private bool ChrEnabled => Submapper switch
    {
        >= 4 and <= 7 => (Latch & 3) == Submapper - 4,
        _ => (Latch & 0x0F) != 0 && Latch != 0x13,
    };

    public override byte PpuRead(ushort address) => ChrEnabled ? base.PpuRead(address) : (byte)0xFF;

    protected override void WriteRegister(ushort address, byte value) => SetLatch(WithBusConflict(address, value));

    protected override void Apply()
    {
    }
}

/// <summary>Mapper 232, Camerica Quattro (NESdev Wiki: "INES Mapper 232"): a 64 KB outer block ($8000-$BFFF) and 16 KB inner bank ($C000-$FFFF).</summary>
public sealed class Quattro : MapperBase
{
    private int _outer;
    private int _inner;

    public Quattro(INesRom rom)
        : base(rom, "QUAT") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
        if (address < 0xC000)
        {
            _outer = (value >> 3) & 3;
        }
        else
        {
            _inner = value & 3;
        }

        Apply();
    }

    private void Apply()
    {
        SetPrg16K(0, (_outer << 2) | _inner);
        SetPrg16K(1, (_outer << 2) | 3);
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteInt32(_outer);
        writer.WriteInt32(_inner);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _outer = reader.ReadInt32();
        _inner = reader.ReadInt32();
        Apply();
    }
}

/// <summary>
/// Mappers 80 and 207, Taito X1-005 (NESdev Wiki: "INES Mapper 080", "INES Mapper 207"): registers at
/// $7EF0-$7EFF, 128 bytes of internal RAM at $7F00 enabled by writing $A3 to $7EF8. Mapper 207 takes
/// nametables from the CHR registers' bit 7 instead of the mirroring register.
/// </summary>
public sealed class TaitoX1005 : MapperBase
{
    private readonly bool _chrNametables;
    private readonly byte[] _chr = new byte[6];
    private readonly byte[] _prg = [0, 1, 0xFE];
    private readonly byte[] _ram = new byte[128];
    private bool _ramEnabled;

    public TaitoX1005(INesRom rom)
        : base(rom, "X105")
    {
        _chrNametables = rom.Mapper == 207;
        Apply();
    }

    public override Span<byte> BatteryRam => _ram;

    public override byte CpuRead(ushort address, byte openBus)
    {
        if (address is >= 0x7F00 and < 0x8000)
        {
            return _ramEnabled ? _ram[address & 0x7F] : openBus;
        }

        return address >= 0x8000 ? ReadPrgRom(address) : openBus;
    }

    public override void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case 0x7EF0 or 0x7EF1:
                _chr[address & 1] = value;
                break;
            case >= 0x7EF2 and <= 0x7EF5:
                _chr[address - 0x7EF0] = value;
                break;
            case 0x7EF6 or 0x7EF7:
                if (!_chrNametables)
                {
                    Mirroring = (value & 1) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;
                }

                break;
            case 0x7EF8 or 0x7EF9:
                _ramEnabled = value == 0xA3;
                break;
            case 0x7EFA or 0x7EFB:
                _prg[0] = value;
                break;
            case 0x7EFC or 0x7EFD:
                _prg[1] = value;
                break;
            case 0x7EFE or 0x7EFF:
                _prg[2] = value;
                break;
            case >= 0x7F00 and < 0x8000:
                if (_ramEnabled)
                {
                    _ram[address & 0x7F] = value;
                }

                return;
            default:
                return;
        }

        Apply();
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    public override int NametablePage(int slot) =>
        _chrNametables ? _chr[slot >> 1] >> 7 : base.NametablePage(slot);

    private void Apply()
    {
        SetPrg8K(0, _prg[0]);
        SetPrg8K(1, _prg[1]);
        SetPrg8K(2, _prg[2]);
        SetPrg8K(3, -1);
        SetChr2K(0, (_chr[0] & 0x7E) >> 1);
        SetChr2K(1, (_chr[1] & 0x7E) >> 1);
        for (var i = 0; i < 4; i++)
        {
            SetChr1K(4 + i, _chr[2 + i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteBytes(_prg);
        writer.WriteBytes(_ram);
        writer.WriteBool(_ramEnabled);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        reader.ReadBytes(_prg);
        reader.ReadBytes(_ram);
        _ramEnabled = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}
