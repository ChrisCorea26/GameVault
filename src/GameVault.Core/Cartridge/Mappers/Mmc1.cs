using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 1, MMC1 and its SxROM boards (NESdev Wiki: "MMC1", "SxROM").
/// Registers are loaded one bit at a time through a 5-bit shift register at $8000-$FFFF.
/// </summary>
public sealed class Mmc1 : MapperBase
{
    private byte _shift;
    private int _shiftCount;
    private byte _control = 0x0C; // power-on: PRG mode 3 (fix last bank at $C000)
    private byte _chr0;
    private byte _chr1;
    private byte _prg;
    private long _cycle;
    private long _lastWriteCycle = -2;

    public Mmc1(INesRom rom)
        : base(rom, "MMC1")
    {
        UpdateBanks();
    }

    // Needed only to spot writes on consecutive cycles; MMC1 has no IRQ.
    public override bool HasCycleHooks => true;

    public override void OnCpuCycle() => _cycle++;

    protected override void WriteRegister(ushort address, byte value)
    {
        // The MMC1 ignores a write on the cycle right after another one, so read-modify-write instructions
        // (which write twice) only count once (NESdev Wiki: "MMC1", "Consecutive-cycle writes").
        var consecutive = _cycle - _lastWriteCycle <= 1;
        _lastWriteCycle = _cycle;
        if (consecutive)
        {
            return;
        }

        if ((value & 0x80) != 0)
        {
            _shift = 0;
            _shiftCount = 0;
            _control |= 0x0C;
            UpdateBanks();
            return;
        }

        _shift |= (byte)((value & 1) << _shiftCount);
        if (++_shiftCount < 5)
        {
            return;
        }

        switch ((address >> 13) & 3)
        {
            case 0: _control = _shift; break;
            case 1: _chr0 = _shift; break;
            case 2: _chr1 = _shift; break;
            default: _prg = _shift; break;
        }

        _shift = 0;
        _shiftCount = 0;
        UpdateBanks();
    }

    private void UpdateBanks()
    {
        Mirroring = (_control & 0x03) switch
        {
            0 => Mirroring.SingleScreenA,
            1 => Mirroring.SingleScreenB,
            2 => Mirroring.Vertical,
            _ => Mirroring.Horizontal,
        };

        // SUROM/SXROM: with 512 KB of PRG, CHR register bit 4 picks the 256 KB half.
        var outer = PrgRom.Length > 0x40000 ? _chr0 & 0x10 : 0;
        var bank = (_prg & 0x0F) | outer;
        switch ((_control >> 2) & 3)
        {
            case 0:
            case 1:
                SetPrg16K(0, bank & ~1);
                SetPrg16K(1, bank | 1);
                break;
            case 2:
                SetPrg16K(0, outer);
                SetPrg16K(1, bank);
                break;
            default:
                SetPrg16K(0, bank);
                SetPrg16K(1, 0x0F | outer);
                break;
        }

        if ((_control & 0x10) == 0)
        {
            SetChr4K(0, _chr0 & 0x1E);
            SetChr4K(1, (_chr0 & 0x1E) | 1);
        }
        else
        {
            SetChr4K(0, _chr0);
            SetChr4K(1, _chr1);
        }

        // SOROM (16 KB) and SXROM (32 KB) bank PRG-RAM with CHR register bits 3 and 2-3.
        PrgRamOffset = PrgRam.Length switch
        {
            >= 0x8000 => ((_chr0 >> 2) & 3) << 13,
            >= 0x4000 => ((_chr0 >> 3) & 1) << 13,
            _ => 0,
        };

        // MMC1B: PRG bit 4 set disables PRG-RAM.
        var enabled = (_prg & 0x10) == 0;
        PrgRamReadable = enabled;
        PrgRamWritable = enabled;
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteByte(_shift);
        writer.WriteInt32(_shiftCount);
        writer.WriteByte(_control);
        writer.WriteByte(_chr0);
        writer.WriteByte(_chr1);
        writer.WriteByte(_prg);
        writer.WriteInt64(_cycle);
        writer.WriteInt64(_lastWriteCycle);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        _shift = reader.ReadByte();
        _shiftCount = reader.ReadInt32();
        _control = reader.ReadByte();
        _chr0 = reader.ReadByte();
        _chr1 = reader.ReadByte();
        _prg = reader.ReadByte();
        _cycle = reader.ReadInt64();
        _lastWriteCycle = reader.ReadInt64();
        UpdateBanks();
    }
}
