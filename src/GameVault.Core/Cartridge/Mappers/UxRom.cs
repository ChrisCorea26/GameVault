using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 2 (NESdev Wiki: "UxROM"): a switchable 16 KB bank at $8000, last bank fixed at $C000, 8 KB CHR-RAM.
/// Submapper 2 has bus conflicts.
/// </summary>
public sealed class UxRom : MapperBase
{
    private byte _bank;

    public UxRom(INesRom rom)
        : base(rom, "UXRM")
    {
        UpdateBanks();
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        _bank = Submapper == 2 ? WithBusConflict(address, value) : value;
        UpdateBanks();
    }

    private void UpdateBanks()
    {
        SetPrg16K(0, _bank);
        SetPrg16K(1, -1);
    }

    protected override void SaveRegisters(StateWriter writer) => writer.WriteByte(_bank);

    protected override void LoadRegisters(StateReader reader)
    {
        _bank = reader.ReadByte();
        UpdateBanks();
    }
}
