using System.Text;
using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Genesis;

/// <summary>Which TV standard and console region a cartridge's header asks for.</summary>
public enum GenesisRegion
{
    /// <summary>NTSC, overseas (Genesis): USA.</summary>
    Americas,

    /// <summary>NTSC, domestic: the Japanese Mega Drive.</summary>
    Japan,

    /// <summary>PAL, overseas: the European Mega Drive.</summary>
    Europe,
}

/// <summary>
/// A Genesis / Mega Drive cartridge (SPEC §7.11, Phase 13): up to 4 MB of ROM in the 68000's first 4 MB, battery
/// SRAM where the header's "RA" block puts it (byte-wide on odd or even addresses, or word-wide), and the Super
/// Street Fighter II mapper for larger ROMs (512 KB banks set through $A130F3–$A130FF), and serial EEPROMs: the one
/// Sega's own boards declare in the header ("RA" $E8 $40: an X24C01, SDA in bit 0 and SCL in bit 1 of $200001), and
/// the EA, Acclaim and Codemasters boards <see cref="GenesisEepromBoards"/> lists.
/// </summary>
/// <remarks>References: Sega Retro, "ROM header" and "Sega Mapper"; Plutiedev, "Saving with SRAM"; Genesis Plus GX's
/// eeprom_i2c.c for the EEPROM boards' wiring.</remarks>
public sealed class GenesisCartridge : IStateful, IBatteryBacked
{
    private static readonly StateTag Tag = StateTag.From("GCRT");

    private readonly byte[] _rom;
    private readonly byte[] _sram;
    private readonly byte[] _banks = [0, 1, 2, 3, 4, 5, 6, 7];
    private readonly bool _mapper;
    private readonly I2cEeprom? _eeprom;
    private bool _sramEnabled;
    private bool _scl = true;
    private bool _sda = true;
    private bool _eepromReadable; // Acclaim's 32M boards: the EEPROM, not the ROM, reads at $200000–$2FFFFF

    /// <param name="image">The file: a raw ROM, or an .smd image (512-byte header and interleaved 16 KB blocks).</param>
    public GenesisCartridge(byte[] image)
    {
        _rom = IsInterleaved(image) ? Deinterleave(image) : image;
        if (_rom.Length < 0x200)
        {
            throw new RomFormatException("A Genesis cartridge image is at least 512 bytes.");
        }

        Region = ReadRegion();
        Title = ReadTitle();
        _mapper = _rom.Length > 0x400000;

        // "RA", type, $20, then the start and end addresses (big-endian) at $1B4 and $1B8.
        var declared = _rom[0x1B0] == 'R' && _rom[0x1B1] == 'A';
        var start = declared ? ReadLong(0x1B4) : 0;
        var end = declared ? ReadLong(0x1B8) : 0;
        var board = GenesisEepromBoards.Find(_rom, sramInHeader: declared && end > start && end - start > 2);

        // Type $E8, or a "range" of under two bytes, is Sega's EEPROM.
        if (board is null && declared && (_rom[0x1B2] == 0xE8 || end < start + 2))
        {
            board = GenesisEepromBoard.SegaDefault;
        }

        if (board is not null)
        {
            EepromBoard = board;
            _eeprom = new I2cEeprom(board.Kind, board.Size, board.PageSize);
            _sram = [];
        }
        else if (declared && end > start && end - start < 0x100000)
        {
            SramStart = start & ~1u;
            SramEnd = end | 1;
            SramOddBytes = (start & 1) != 0;
            SramWordWide = !SramOddBytes && (_rom[0x1B2] & 0x18) is 0x00 or 0x08;
            var span = (int)(SramEnd - SramStart + 1);
            _sram = new byte[SramWordWide ? span : span / 2];
        }
        else
        {
            _sram = [];
        }

        Array.Fill(_sram, (byte)0xFF);
        _sramEnabled = _rom.Length <= SramStart;
    }

    /// <summary>The ROM as the 68000 sees it (de-interleaved).</summary>
    public ReadOnlySpan<byte> Rom => _rom;

    public GenesisRegion Region { get; }

    /// <summary>The overseas name from the header, trimmed.</summary>
    public string Title { get; }

    public uint SramStart { get; }

    public uint SramEnd { get; }

    /// <summary>Byte-wide SRAM on the odd addresses (the common case); otherwise on the even ones or word-wide.</summary>
    public bool SramOddBytes { get; }

    public bool SramWordWide { get; }

    public bool HasBattery => _sram.Length > 0 || _eeprom is not null;

    /// <summary>The battery SRAM, or the EEPROM's contents.</summary>
    public Span<byte> BatteryRam => _eeprom is { } eeprom ? eeprom.Data : _sram;

    /// <summary>The cartridge's serial EEPROM, when it has one.</summary>
    public I2cEeprom? Eeprom => _eeprom;

    /// <summary>How the EEPROM is wired, when there is one.</summary>
    public GenesisEepromBoard? EepromBoard { get; }

    /// <summary>True for .smd copier images: a 512-byte header whose bytes 8 and 9 are $AA $BB, and 16 KB blocks.</summary>
    public static bool IsInterleaved(ReadOnlySpan<byte> image) =>
        image.Length >= 0x4200 && (image.Length - 0x200) % 0x4000 == 0 && image[8] == 0xAA && image[9] == 0xBB;

    /// <summary>
    /// .smd blocks hold each 16 KB with the odd bytes in the first half and the even bytes in the second.
    /// </summary>
    public static byte[] Deinterleave(ReadOnlySpan<byte> image)
    {
        var blocks = (image.Length - 0x200) / 0x4000;
        var rom = new byte[blocks * 0x4000];
        for (var b = 0; b < blocks; b++)
        {
            var block = image.Slice(0x200 + b * 0x4000, 0x4000);
            for (var i = 0; i < 0x2000; i++)
            {
                rom[b * 0x4000 + i * 2 + 1] = block[i];
                rom[b * 0x4000 + i * 2] = block[0x2000 + i];
            }
        }

        return rom;
    }

    /// <summary>A raw .bin is a Genesis ROM when its header says "SEGA" at $100 (or $101).</summary>
    public static bool LooksLikeGenesis(ReadOnlySpan<byte> image) =>
        image.Length >= 0x200 && (image.Slice(0x100, 4).SequenceEqual("SEGA"u8) || image.Slice(0x101, 4).SequenceEqual("SEGA"u8));

    public void Reset()
    {
        for (var i = 0; i < 8; i++)
        {
            _banks[i] = (byte)i;
        }

        _sramEnabled = _rom.Length <= SramStart;
        _eepromReadable = false;
    }

    public byte ReadByte(uint address)
    {
        if (_eeprom is not null && EepromReads(address))
        {
            // Only the odd byte carries SDA; the rest of the bus reads as zeros.
            return (address & 1) != 0 ? EepromBit() : RomByte(address);
        }

        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd && TrySram(address, out var index))
        {
            return _sram[index];
        }

        return RomByte(address);
    }

    public ushort ReadWord(uint address)
    {
        if (_eeprom is not null && EepromReads(address))
        {
            return EepromBoard!.Wiring == GenesisEepromWiring.Codemasters ? (ushort)(RomByte(address) << 8 | EepromBit()) : EepromBit();
        }

        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd)
        {
            return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
        }

        var offset = Map(address);
        return offset + 1 < (uint)_rom.Length ? (ushort)((_rom[offset] << 8) | _rom[offset + 1]) : (ushort)0xFFFF;
    }

    private byte RomByte(uint address)
    {
        var offset = Map(address);
        return offset < (uint)_rom.Length ? _rom[offset] : (byte)0xFF;
    }

    /// <summary>Where the board lets SDA be read.</summary>
    private bool EepromReads(uint address) => EepromBoard!.Wiring switch
    {
        GenesisEepromWiring.Acclaim32M => _eepromReadable && address is >= 0x200000 and < 0x300000,
        GenesisEepromWiring.Codemasters => address is >= 0x380000 and < 0x400000,
        _ => address is >= 0x200000 and < 0x400000,
    };

    /// <summary>SDA as the board puts it on the data bus (the J-Cart's pad lines on Codemasters' boards read idle).</summary>
    private byte EepromBit()
    {
        var sda = _eeprom!.Sda ? 1 : 0;
        return EepromBoard!.Wiring switch
        {
            GenesisEepromWiring.ElectronicArts => (byte)(sda << 7),
            GenesisEepromWiring.Acclaim16M => (byte)(sda << 1),
            GenesisEepromWiring.Codemasters => (byte)((sda << 7) | 0x7F),
            _ => (byte)sda,
        };
    }

    public void WriteByte(uint address, byte value)
    {
        if (_eeprom is not null && WriteEeprom(address, value, word: false))
        {
            return;
        }

        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd && TrySram(address, out var index))
        {
            _sram[index] = value;
        }
    }

    /// <summary>A word write: the high byte to the even address and the low byte to the odd one (SRAM, the EEPROM's lines).</summary>
    public void WriteWord(uint address, ushort value)
    {
        if (_eeprom is not null && WriteEeprom(address, (byte)value, word: true))
        {
            return;
        }

        WriteByte(address & ~1u, (byte)(value >> 8));
        WriteByte(address | 1, (byte)value);
    }

    /// <summary>A write the board routes to the EEPROM's lines (or its read switch); false when it doesn't.</summary>
    private bool WriteEeprom(uint address, byte value, bool word)
    {
        var odd = (address & 1) != 0;
        switch (EepromBoard!.Wiring)
        {
            case GenesisEepromWiring.Sega when address is >= 0x200000 and < 0x400000:
                if (odd || word)
                {
                    DriveEeprom(scl: (value & 2) != 0, sda: (value & 1) != 0);
                }

                return true;
            case GenesisEepromWiring.ElectronicArts when address is >= 0x200000 and < 0x400000:
                if (odd || word)
                {
                    DriveEeprom(scl: (value & 0x40) != 0, sda: (value & 0x80) != 0);
                }

                return true;
            case GenesisEepromWiring.Acclaim16M when address is >= 0x200000 and < 0x400000:
                DriveEeprom(scl: (value & 2) != 0, sda: (value & 1) != 0);
                return true;
            case GenesisEepromWiring.Acclaim32M when address is >= 0x200000 and < 0x300000:
                if (word)
                {
                    _eepromReadable = (value & 1) == 0;
                }
                else if (odd)
                {
                    DriveEeprom(_scl, sda: (value & 1) != 0);
                }
                else
                {
                    DriveEeprom(scl: (value & 1) != 0, _sda);
                }

                return true;
            case GenesisEepromWiring.Codemasters when address is >= 0x300000 and < 0x380000:
                DriveEeprom(scl: (value & 2) != 0, sda: (value & 1) != 0);
                return true;
            case GenesisEepromWiring.Codemasters when address is >= 0x380000 and < 0x400000:
                return true; // the J-Cart's pad select (not emulated)
            default:
                return false;
        }
    }

    private void DriveEeprom(bool scl, bool sda)
    {
        _scl = scl;
        _sda = sda;
        _eeprom!.Drive(scl, sda);
    }

    /// <summary>$A130F1: SRAM enable (bit 0); $A130F3–$A130FF: the SSF2 banks for 512 KB windows 1–7.</summary>
    public void WriteControl(uint address, byte value)
    {
        var register = (int)(address & 0xF) >> 1;
        if (register == 0)
        {
            _sramEnabled = (value & 1) != 0 || _rom.Length <= SramStart;
            return;
        }

        if (_mapper)
        {
            _banks[register] = (byte)(value & 0x3F);
        }
    }

    private bool TrySram(uint address, out int index)
    {
        var offset = (int)(address - SramStart);
        if (SramWordWide)
        {
            index = offset;
            return true;
        }

        index = offset >> 1;
        return ((address & 1) != 0) == SramOddBytes;
    }

    private uint Map(uint address)
    {
        if (!_mapper || address >= 0x400000)
        {
            return address;
        }

        return ((uint)_banks[address >> 19] << 19) | (address & 0x7FFFF);
    }

    private uint ReadLong(int offset) =>
        (uint)(_rom[offset] << 24 | _rom[offset + 1] << 16 | _rom[offset + 2] << 8 | _rom[offset + 3]);

    private GenesisRegion ReadRegion()
    {
        // Old style: letters J, U, E at $1F0–$1F2. New style: one hex digit (bit 0 Japan, bit 2 USA, bit 3 Europe).
        var field = Encoding.ASCII.GetString(_rom, 0x1F0, 3);
        if (field.Contains('U'))
        {
            return GenesisRegion.Americas;
        }

        // A lone "E" is the old style's Europe, not the hex digit.
        if (field.Trim().Length == 1 && "0123456789ABCDF".Contains(field[0]))
        {
            var bits = Convert.ToInt32(field[..1], 16);
            return (bits & 4) != 0 ? GenesisRegion.Americas : (bits & 1) != 0 ? GenesisRegion.Japan : (bits & 8) != 0 ? GenesisRegion.Europe : GenesisRegion.Americas;
        }

        if (field.Contains('J'))
        {
            return GenesisRegion.Japan;
        }

        return field.Contains('E') ? GenesisRegion.Europe : GenesisRegion.Americas;
    }

    private string ReadTitle()
    {
        var text = Encoding.ASCII.GetString(_rom, 0x150, 48);
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('\0');
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        writer.WriteBytes(_banks);
        writer.WriteBool(_sramEnabled);
        writer.WriteBytes(_sram);
        if (_eeprom is not null)
        {
            // Only EEPROM cartridges have these, so other games' states are unchanged.
            _eeprom.SaveState(writer);
            writer.WriteBool(_scl);
            writer.WriteBool(_sda);
            writer.WriteBool(_eepromReadable);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        reader.ReadBytes(_banks);
        _sramEnabled = reader.ReadBool();
        reader.ReadBytes(_sram);
        if (_eeprom is not null)
        {
            _eeprom.LoadState(reader);
            if (version >= 2)
            {
                _scl = reader.ReadBool();
                _sda = reader.ReadBool();
                _eepromReadable = reader.ReadBool();
            }
        }

        reader.EndChunk();
    }
}
