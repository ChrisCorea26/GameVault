using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// Mapper 9, MMC2 (PxROM), and mapper 10, MMC4 (FxROM) (NESdev Wiki: "MMC2", "MMC4"). Each 4 KB CHR half has
/// two banks; a latch picks one and flips when the PPU fetches tile $FD or $FE, so games swap graphics
/// mid-frame without IRQs. MMC2 switches 8 KB of PRG (last three fixed), MMC4 16 KB (last fixed).
/// </summary>
public sealed class Mmc2 : MapperBase
{
    private readonly bool _isMmc4;
    private readonly byte[] _chr = new byte[4]; // [0]=$0000 FD, [1]=$0000 FE, [2]=$1000 FD, [3]=$1000 FE
    private byte _prg;
    private bool _latch0Fe = true;
    private bool _latch1Fe = true;

    public Mmc2(INesRom rom)
        : base(rom, rom.Mapper == 10 ? "MMC4" : "MMC2")
    {
        _isMmc4 = rom.Mapper == 10;
        UpdateBanks();
    }

    public override byte PpuRead(ushort address)
    {
        var value = base.PpuRead(address);

        // The latch flips after the fetch, so the tile that triggers it still uses the old bank.
        switch (address & 0x1FF8)
        {
            case 0x0FD8 when _isMmc4 || address == 0x0FD8:
                SetLatch0(false);
                break;
            case 0x0FE8 when _isMmc4 || address == 0x0FE8:
                SetLatch0(true);
                break;
            case 0x1FD8:
                SetLatch1(false);
                break;
            case 0x1FE8:
                SetLatch1(true);
                break;
        }

        return value;
    }

    protected override void WriteRegister(ushort address, byte value)
    {
        switch (address & 0xF000)
        {
            case 0xA000:
                _prg = (byte)(value & 0x0F);
                break;
            case 0xB000:
                _chr[0] = (byte)(value & 0x1F);
                break;
            case 0xC000:
                _chr[1] = (byte)(value & 0x1F);
                break;
            case 0xD000:
                _chr[2] = (byte)(value & 0x1F);
                break;
            case 0xE000:
                _chr[3] = (byte)(value & 0x1F);
                break;
            case 0xF000:
                Mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                break;
            default:
                return;
        }

        UpdateBanks();
    }

    private void SetLatch0(bool fe)
    {
        if (_latch0Fe != fe)
        {
            _latch0Fe = fe;
            SetChr4K(0, _chr[fe ? 1 : 0]);
        }
    }

    private void SetLatch1(bool fe)
    {
        if (_latch1Fe != fe)
        {
            _latch1Fe = fe;
            SetChr4K(1, _chr[fe ? 3 : 2]);
        }
    }

    private void UpdateBanks()
    {
        if (_isMmc4)
        {
            SetPrg16K(0, _prg);
            SetPrg16K(1, -1);
        }
        else
        {
            SetPrg8K(0, _prg);
            SetPrg8K(1, -3);
            SetPrg8K(2, -2);
            SetPrg8K(3, -1);
        }

        SetChr4K(0, _chr[_latch0Fe ? 1 : 0]);
        SetChr4K(1, _chr[_latch1Fe ? 3 : 2]);
    }

    protected override void SaveRegisters(StateWriter writer)
    {
        writer.WriteBytes(_chr);
        writer.WriteByte(_prg);
        writer.WriteBool(_latch0Fe);
        writer.WriteBool(_latch1Fe);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        reader.ReadBytes(_chr);
        _prg = reader.ReadByte();
        _latch0Fe = reader.ReadBool();
        _latch1Fe = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
        UpdateBanks();
    }
}
