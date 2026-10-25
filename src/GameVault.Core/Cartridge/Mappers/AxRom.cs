using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 7 (NESdev Wiki: "AxROM"): a switchable 32 KB PRG bank (bits 0-2) and one-screen mirroring
/// selected by bit 4. Submapper 2 has bus conflicts.
/// </summary>
public sealed class AxRom : MapperBase
{
    private byte _value;

    public AxRom(INesRom rom)
        : base(rom, "AXRM")
    {
        Apply();
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        _value = Submapper == 2 ? WithBusConflict(address, value) : value;
        Apply();
    }

    private void Apply()
    {
        SetPrg32K(_value & 0x07);
        Mirroring = (_value & 0x10) != 0 ? Mirroring.SingleScreenB : Mirroring.SingleScreenA;
    }

    protected override void SaveRegisters(StateWriter writer) => writer.WriteByte(_value);

    protected override void LoadRegisters(StateReader reader)
    {
        _value = reader.ReadByte();
        Apply();
    }
}
