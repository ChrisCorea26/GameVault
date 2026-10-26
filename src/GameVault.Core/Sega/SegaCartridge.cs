using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Sega;

/// <summary>Sega 8-bit cartridge boards (SMS Power!: "Mapper").</summary>
public enum SegaMapper
{
    /// <summary>Up to 48 KB, no banking (SG-1000 and small SMS games).</summary>
    None,

    /// <summary>Sega's own: $FFFC control (cartridge RAM), $FFFD–$FFFF select the three 16 KB slots; the first 1 KB is fixed.</summary>
    Sega,

    /// <summary>Codemasters: writes to $0000, $4000 and $8000 select the slots.</summary>
    Codemasters,

    /// <summary>Korean boards: a write to $A000 selects the third slot.</summary>
    Korean,
}

/// <summary>
/// A Sega 8-bit cartridge: the ROM (and battery-backed RAM on Sega boards), mapped into $0000–$BFFF. The console
/// handles $C000–$FFFF (system RAM) and passes the mapper's register writes here.
/// </summary>
public sealed class SegaCartridge : IStateful, IBatteryBacked
{
    private static readonly StateTag Tag = StateTag.From("SCRT");

    private readonly byte[] _rom;
    private readonly int _bankCount;
    private readonly byte[] _ram = new byte[0x8000];
    private readonly byte[] _expansionRam;
    private readonly byte[] _banks = new byte[3];
    private byte _control;

    /// <param name="rom">The ROM image (any 512-byte copier header already removed).</param>
    /// <param name="mapper">The board.</param>
    /// <param name="expansionRamAt">8 KB of extra RAM some SG-1000 boards have ($2000 or $8000), or null.</param>
    public SegaCartridge(byte[] rom, SegaMapper mapper, ushort? expansionRamAt = null)
    {
        if (rom.Length == 0)
        {
            throw new RomFormatException("The cartridge image is empty.");
        }

        _rom = rom;
        Mapper = mapper;
        _bankCount = Math.Max(1, (rom.Length + 0x3FFF) / 0x4000);
        ExpansionRamAt = expansionRamAt;
        _expansionRam = expansionRamAt is null ? [] : new byte[0x2000];
        Reset();
    }

    public SegaMapper Mapper { get; }

    public ushort? ExpansionRamAt { get; }

    public ReadOnlySpan<byte> Rom => _rom;

    /// <summary>
    /// Sega boards can have battery-backed RAM; the header doesn't say, so it is kept for every Sega board (the host
    /// only writes a save file once the RAM changes).
    /// </summary>
    public bool HasBattery => Mapper == SegaMapper.Sega;

    public Span<byte> BatteryRam => _ram;

    public void Reset()
    {
        _banks[0] = 0;
        _banks[1] = 1;
        _banks[2] = Mapper == SegaMapper.Codemasters ? (byte)0 : (byte)2;
        _control = 0;
    }

    public byte Read(ushort address)
    {
        if (ExpansionRamAt is { } at && address >= at && address < at + 0x2000)
        {
            return _expansionRam[address - at];
        }

        switch (Mapper)
        {
            case SegaMapper.None:
                return address < _rom.Length ? _rom[address] : (byte)0xFF;
            case SegaMapper.Sega:
                if (address < 0x0400)
                {
                    return _rom[address];
                }

                if (address >= 0x8000 && (_control & 0x08) != 0)
                {
                    return _ram[((_control & 0x04) << 12) | (address & 0x3FFF)];
                }

                break;
        }

        return RomAt(_banks[address >> 14], address);
    }

    public void Write(ushort address, byte value)
    {
        if (ExpansionRamAt is { } at && address >= at && address < at + 0x2000)
        {
            _expansionRam[address - at] = value;
            return;
        }

        switch (Mapper)
        {
            case SegaMapper.Sega when address >= 0x8000 && address < 0xC000 && (_control & 0x08) != 0:
                _ram[((_control & 0x04) << 12) | (address & 0x3FFF)] = value;
                break;
            case SegaMapper.Codemasters when address is 0x0000 or 0x4000 or 0x8000:
                _banks[address >> 14] = value;
                break;
            case SegaMapper.Korean when address == 0xA000:
                _banks[2] = value;
                break;
        }
    }

    /// <summary>The Sega board's registers at $FFFC–$FFFF (the console also stores the byte in RAM).</summary>
    public void WriteRegister(ushort address, byte value)
    {
        if (Mapper != SegaMapper.Sega)
        {
            return;
        }

        switch (address)
        {
            case 0xFFFC:
                _control = value;
                break;
            case 0xFFFD:
                _banks[0] = value;
                break;
            case 0xFFFE:
                _banks[1] = value;
                break;
            case 0xFFFF:
                _banks[2] = value;
                break;
        }
    }

    private byte RomAt(int bank, ushort address)
    {
        var offset = (bank % _bankCount) * 0x4000 + (address & 0x3FFF);
        return offset < _rom.Length ? _rom[offset] : (byte)0xFF;
    }

    /// <summary>Picks the board from the ROM: Codemasters games carry a checksum pair at $7FE6; anything bigger than 48 KB is a Sega board.</summary>
    public static SegaMapper DetectMapper(ReadOnlySpan<byte> rom)
    {
        if (rom.Length >= 0x8000)
        {
            var checksum = rom[0x7FE6] | rom[0x7FE7] << 8;
            var inverse = rom[0x7FE8] | rom[0x7FE9] << 8;
            if (checksum != 0 && checksum + inverse == 0x10000)
            {
                return SegaMapper.Codemasters;
            }
        }

        return rom.Length > 0xC000 ? SegaMapper.Sega : SegaMapper.None;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_banks);
        writer.WriteByte(_control);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_expansionRam);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_banks);
        _control = reader.ReadByte();
        reader.ReadBytes(_ram);
        reader.ReadBytes(_expansionRam);
        reader.EndChunk();
    }
}
