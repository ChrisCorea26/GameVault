using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Discrete-logic boards with one latch register: the latch value picks the PRG and CHR banks. Subclasses
/// say where the latch lives and how its bits split.
/// </summary>
public abstract class LatchBoard : MapperBase
{
    private byte _latch;

    protected LatchBoard(INesRom rom, string tag)
        : base(rom, tag)
    {
    }

    protected byte Latch => _latch;

    /// <summary>Applies <see cref="Latch"/> to the banks (also called after power-on and state loads).</summary>
    protected abstract void Apply();

    protected void SetLatch(byte value)
    {
        _latch = value;
        Apply();
    }

    protected override void WriteRegister(ushort address, byte value) => SetLatch(value);

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteByte(_latch);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _latch = reader.ReadByte();
        Mirroring = (Mirroring)reader.ReadByte();
        Apply();
    }
}

/// <summary>Mapper 11, Color Dreams (NESdev Wiki: "Color Dreams"): bits 0-1 PRG 32 KB, bits 4-7 CHR 8 KB. Bus conflicts.</summary>
public sealed class ColorDreams : LatchBoard
{
    public ColorDreams(INesRom rom)
        : base(rom, "CDRM") => Apply();

    protected override void WriteRegister(ushort address, byte value) => SetLatch(WithBusConflict(address, value));

    protected override void Apply()
    {
        SetPrg32K(Latch & 0x03);
        SetChr8K(Latch >> 4);
    }
}

/// <summary>Mapper 66, GxROM (NESdev Wiki: "GxROM"): bits 4-5 PRG 32 KB, bits 0-1 CHR 8 KB.</summary>
public sealed class GxRom : LatchBoard
{
    public GxRom(INesRom rom)
        : base(rom, "GXRM") => Apply();

    protected override void Apply()
    {
        SetPrg32K((Latch >> 4) & 0x03);
        SetChr8K(Latch & 0x03);
    }
}

/// <summary>
/// Mapper 71, Camerica / Codemasters (NESdev Wiki: "INES Mapper 071"): $C000-$FFFF selects 16 KB at $8000,
/// last bank fixed. Submapper 1 (Fire Hawk) uses $8000-$9FFF bit 4 for one-screen mirroring.
/// </summary>
public sealed class Camerica : LatchBoard
{
    public Camerica(INesRom rom)
        : base(rom, "CAME") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
        if (address >= 0xC000)
        {
            SetLatch(value);
        }
        else if (address < 0xA000 && Submapper == 1)
        {
            Mirroring = (value & 0x10) != 0 ? Mirroring.SingleScreenB : Mirroring.SingleScreenA;
        }
    }

    protected override void Apply()
    {
        SetPrg16K(0, Latch & 0x0F);
        SetPrg16K(1, -1);
    }
}

/// <summary>
/// Mapper 34 (NESdev Wiki: "INES Mapper 034"). BNROM: any write to $8000-$FFFF picks 32 KB of PRG, with
/// CHR-RAM. NINA-001 (more than 8 KB of CHR, or submapper 1): $7FFD PRG 32 KB, $7FFE and $7FFF the two
/// 4 KB CHR banks; the writes also land in PRG-RAM.
/// </summary>
public sealed class Mapper34 : MapperBase
{
    private readonly bool _nina;
    private byte _prg;
    private byte _chr0;
    private byte _chr1 = 1;

    public Mapper34(INesRom rom)
        : base(rom, "M034")
    {
        _nina = rom.Submapper == 1 || (rom.Submapper == 0 && rom.ChrRom.Length > 0x2000);
        Apply();
    }

    public override void CpuWrite(ushort address, byte value)
    {
        base.CpuWrite(address, value);
        if (_nina)
        {
            switch (address)
            {
                case 0x7FFD:
                    _prg = (byte)(value & 1);
                    Apply();
                    break;
                case 0x7FFE:
                    _chr0 = (byte)(value & 0x0F);
                    Apply();
                    break;
                case 0x7FFF:
                    _chr1 = (byte)(value & 0x0F);
                    Apply();
                    break;
            }
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        if (!_nina)
        {
            _prg = WithBusConflict(address, value);
            Apply();
        }
    }

    private void Apply()
    {
        SetPrg32K(_prg);
        if (_nina)
        {
            SetChr4K(0, _chr0);
            SetChr4K(1, _chr1);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteByte(_prg);
        writer.WriteByte(_chr0);
        writer.WriteByte(_chr1);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _prg = reader.ReadByte();
        _chr0 = reader.ReadByte();
        _chr1 = reader.ReadByte();
        Apply();
    }
}

/// <summary>
/// Mapper 79, NINA-03 / NINA-06 (NESdev Wiki: "NINA-003-006"): a register at $4100-$5FFF (when A8 is set
/// and A13-A15 = 010): bit 3 PRG 32 KB, bits 0-2 CHR 8 KB.
/// </summary>
public sealed class Nina0306 : LatchBoard
{
    public Nina0306(INesRom rom)
        : base(rom, "NINA") => Apply();

    public override void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x4100 and < 0x6000 && (address & 0xE100) == 0x4100)
        {
            SetLatch(value);
        }
        else
        {
            base.CpuWrite(address, value);
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    protected override void Apply()
    {
        SetPrg32K((Latch >> 3) & 1);
        SetChr8K(Latch & 0x07);
    }
}

/// <summary>Mapper 87 (NESdev Wiki: "INES Mapper 087"): $6000-$7FFF picks 8 KB CHR with bits 0 and 1 swapped.</summary>
public sealed class Mapper87 : LatchBoard
{
    public Mapper87(INesRom rom)
        : base(rom, "M087") => Apply();

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

    protected override void Apply() => SetChr8K(((Latch & 1) << 1) | ((Latch >> 1) & 1));
}

/// <summary>Mapper 140, Jaleco JF-11/JF-14 (NESdev Wiki: "INES Mapper 140"): $6000-$7FFF bits 4-5 PRG 32 KB, 0-3 CHR 8 KB.</summary>
public sealed class Mapper140 : LatchBoard
{
    public Mapper140(INesRom rom)
        : base(rom, "M140") => Apply();

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
        SetPrg32K((Latch >> 4) & 0x03);
        SetChr8K(Latch & 0x0F);
    }
}

/// <summary>
/// Mapper 206, Namco 108 / DxROM (NESdev Wiki: "INES Mapper 206"): the MMC3's banking without PRG/CHR
/// inversion, mirroring control or IRQ.
/// </summary>
public sealed class Namco108 : MapperBase
{
    private readonly byte[] _registers = [0, 2, 4, 5, 6, 7, 0, 1];
    private byte _select;

    public Namco108(INesRom rom)
        : base(rom, "N108") => Apply();

    protected override void WriteRegister(ushort address, byte value)
    {
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
            Apply();
        }
    }

    private void Apply()
    {
        SetPrg8K(0, _registers[6]);
        SetPrg8K(1, _registers[7]);
        SetPrg8K(2, -2);
        SetPrg8K(3, -1);
        SetChr1K(0, _registers[0] & 0x3E);
        SetChr1K(1, _registers[0] | 1);
        SetChr1K(2, _registers[1] & 0x3E);
        SetChr1K(3, _registers[1] | 1);
        SetChr1K(4, _registers[2]);
        SetChr1K(5, _registers[3]);
        SetChr1K(6, _registers[4]);
        SetChr1K(7, _registers[5]);
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_registers);
        writer.WriteByte(_select);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_registers);
        _select = reader.ReadByte();
        Apply();
    }
}
