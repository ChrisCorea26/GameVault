using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Konami VRC2 and VRC4, mappers 21, 22, 23 and 25 (NESdev Wiki: "VRC2 and VRC4"). The boards differ in which
/// CPU address lines reach the chip's two register-select pins; since each game only uses its own lines,
/// both candidate lines are ORed together per mapper number. Mapper 22 (VRC2a) drops the low CHR bank bit.
/// </summary>
public sealed class Vrc24 : MapperBase
{
    private readonly int _a0Mask;
    private readonly int _a1Mask;
    private readonly bool _vrc2a;
    private readonly int[] _chr = new int[8];
    private readonly VrcIrq _irq = new();
    private byte _prg0;
    private byte _prg1;
    private bool _swapMode;

    public Vrc24(INesRom rom)
        : base(rom, "VRC4")
    {
        (_a0Mask, _a1Mask) = rom.Mapper switch
        {
            21 => (0x02 | 0x40, 0x04 | 0x80), // VRC4a (A1, A2) / VRC4c (A6, A7)
            22 => (0x02, 0x01),               // VRC2a (A1, A0)
            23 => (0x01 | 0x04, 0x02 | 0x08), // VRC2b / VRC4f (A0, A1) / VRC4e (A2, A3)
            _ => (0x02 | 0x08, 0x01 | 0x04),  // 25: VRC2c / VRC4b (A1, A0) / VRC4d (A3, A2)
        };
        _vrc2a = rom.Mapper == 22;
        UpdateBanks();
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq.Pending;

    public override void OnCpuCycle() => _irq.Clock();

    protected override void WriteRegister(ushort address, byte value)
    {
        var sub = ((address & _a0Mask) != 0 ? 1 : 0) | ((address & _a1Mask) != 0 ? 2 : 0);
        switch (address & 0xF000)
        {
            case 0x8000:
                _prg0 = (byte)(value & 0x1F);
                break;
            case 0x9000 when sub == 2:
                _swapMode = (value & 0x02) != 0;
                break;
            case 0x9000:
                Mirroring = (value & 3) switch
                {
                    0 => Mirroring.Vertical,
                    1 => Mirroring.Horizontal,
                    2 => Mirroring.SingleScreenA,
                    _ => Mirroring.SingleScreenB,
                };
                return;
            case 0xA000:
                _prg1 = (byte)(value & 0x1F);
                break;
            case 0xB000 or 0xC000 or 0xD000 or 0xE000:
            {
                var register = (((address & 0xF000) - 0xB000) >> 11) | (sub >> 1);
                _chr[register] = (sub & 1) == 0
                    ? (_chr[register] & 0x1F0) | (value & 0x0F)
                    : (_chr[register] & 0x00F) | ((value & 0x1F) << 4);
                break;
            }

            default: // $F000
                switch (sub)
                {
                    case 0:
                        _irq.Latch = (byte)((_irq.Latch & 0xF0) | (value & 0x0F));
                        break;
                    case 1:
                        _irq.Latch = (byte)((_irq.Latch & 0x0F) | (value << 4));
                        break;
                    case 2:
                        _irq.WriteControl(value);
                        break;
                    default:
                        _irq.Acknowledge();
                        break;
                }

                return;
        }

        UpdateBanks();
    }

    private void UpdateBanks()
    {
        SetPrg8K(_swapMode ? 2 : 0, _prg0);
        SetPrg8K(1, _prg1);
        SetPrg8K(_swapMode ? 0 : 2, -2);
        SetPrg8K(3, -1);
        for (var i = 0; i < 8; i++)
        {
            SetChr1K(i, _vrc2a ? _chr[i] >> 1 : _chr[i]);
        }
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        foreach (var bank in _chr)
        {
            writer.WriteInt32(bank);
        }

        writer.WriteByte(_prg0);
        writer.WriteByte(_prg1);
        writer.WriteBool(_swapMode);
        writer.WriteByte((byte)Mirroring);
        _irq.Save(writer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        for (var i = 0; i < 8; i++)
        {
            _chr[i] = reader.ReadInt32();
        }

        _prg0 = reader.ReadByte();
        _prg1 = reader.ReadByte();
        _swapMode = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        _irq.Load(reader);
        UpdateBanks();
    }
}
