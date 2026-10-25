using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Konami VRC7, mapper 85 (NESdev Wiki: "VRC7"): three 8 KB PRG banks, eight 1 KB CHR banks, mirroring,
/// PRG-RAM enable and VRC IRQ. VRC7a uses A4 and VRC7b A3 as the sub-register line, so both are accepted.
/// Its FM sound is <see cref="Vrc7Audio"/>.
/// </summary>
public sealed class Vrc7 : MapperBase
{
    private readonly byte[] _prg = new byte[3];
    private readonly byte[] _chr = new byte[8];
    private readonly VrcIrq _irq = new();
    private readonly Vrc7Audio _audio = new();
    private byte _control;

    public Vrc7(INesRom rom)
        : base(rom, "VRC7") => UpdateBanks();

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _irq.Pending;

    public override Audio.IExpansionAudio ExpansionAudio => _audio;

    public override void OnCpuCycle() => _irq.Clock();

    protected override void WriteRegister(ushort address, byte value)
    {
        var odd = (address & 0x18) != 0;
        switch (address & 0xF000)
        {
            case 0x8000:
                _prg[odd ? 1 : 0] = (byte)(value & 0x3F);
                break;
            case 0x9000 when !odd:
                _prg[2] = (byte)(value & 0x3F);
                break;
            case 0x9000:
                // FM sound: $9010 selects a register, $9030 writes it.
                if ((address & 0x20) != 0)
                {
                    _audio.WriteData(value);
                }
                else
                {
                    _audio.SelectRegister(value);
                }

                return;
            case 0xA000 or 0xB000 or 0xC000 or 0xD000:
                _chr[(((address & 0xF000) - 0xA000) >> 11) | (odd ? 1 : 0)] = value;
                break;
            case 0xE000 when !odd:
                _control = value;
                PrgRamReadable = PrgRamWritable = (value & 0x80) != 0;
                Mirroring = (value & 3) switch
                {
                    0 => Mirroring.Vertical,
                    1 => Mirroring.Horizontal,
                    2 => Mirroring.SingleScreenA,
                    _ => Mirroring.SingleScreenB,
                };
                return;
            case 0xE000:
                _irq.Latch = value;
                return;
            default: // $F000
                if (odd)
                {
                    _irq.Acknowledge();
                }
                else
                {
                    _irq.WriteControl(value);
                }

                return;
        }

        UpdateBanks();
    }

    private void UpdateBanks()
    {
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
        writer.WriteBytes(_prg);
        writer.WriteBytes(_chr);
        writer.WriteByte(_control);
        writer.WriteByte((byte)Mirroring);
        _irq.Save(writer);
        _audio.Save(writer);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_prg);
        reader.ReadBytes(_chr);
        _control = reader.ReadByte();
        PrgRamReadable = PrgRamWritable = (_control & 0x80) != 0;
        Mirroring = (Mirroring)reader.ReadByte();
        _irq.Load(reader);
        _audio.Load(reader);
        UpdateBanks();
    }
}
