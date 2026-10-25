using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 3 (NESdev Wiki: "CNROM"): fixed 16/32 KB PRG and a switchable 8 KB CHR bank.
/// Submapper 2 has bus conflicts.
/// </summary>
public sealed class CnRom : MapperBase
{
    private byte _bank;

    public CnRom(INesRom rom)
        : base(rom, "CNRM")
    {
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        _bank = Submapper == 2 ? WithBusConflict(address, value) : value;
        SetChr8K(_bank);
    }

    protected override void SaveRegisters(StateWriter writer) => writer.WriteByte(_bank);

    protected override void LoadRegisters(StateReader reader)
    {
        _bank = reader.ReadByte();
        SetChr8K(_bank);
    }
}
