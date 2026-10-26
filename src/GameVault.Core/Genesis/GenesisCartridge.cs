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
/// Street Fighter II mapper for larger ROMs (512 KB banks set through $A130F3–$A130FF).
/// </summary>
/// <remarks>References: Sega Retro, "ROM header" and "Sega Mapper"; Plutiedev, "Saving with SRAM".</remarks>
public sealed class GenesisCartridge : IStateful, IBatteryBacked
{
    private static readonly StateTag Tag = StateTag.From("GCRT");

    private readonly byte[] _rom;
    private readonly byte[] _sram;
    private readonly byte[] _banks = [0, 1, 2, 3, 4, 5, 6, 7];
    private readonly bool _mapper;
    private bool _sramEnabled;

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
        if (_rom[0x1B0] == 'R' && _rom[0x1B1] == 'A')
        {
            var start = ReadLong(0x1B4);
            var end = ReadLong(0x1B8);
            if (end > start && end - start < 0x100000)
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

    public bool HasBattery => _sram.Length > 0;

    public Span<byte> BatteryRam => _sram;

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
    }

    public byte ReadByte(uint address)
    {
        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd && TrySram(address, out var index))
        {
            return _sram[index];
        }

        var offset = Map(address);
        return offset < (uint)_rom.Length ? _rom[offset] : (byte)0xFF;
    }

    public ushort ReadWord(uint address)
    {
        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd)
        {
            return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
        }

        var offset = Map(address);
        return offset + 1 < (uint)_rom.Length ? (ushort)((_rom[offset] << 8) | _rom[offset + 1]) : (ushort)0xFFFF;
    }

    public void WriteByte(uint address, byte value)
    {
        if (_sram.Length > 0 && _sramEnabled && address >= SramStart && address <= SramEnd && TrySram(address, out var index))
        {
            _sram[index] = value;
        }
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
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_banks);
        writer.WriteBool(_sramEnabled);
        writer.WriteBytes(_sram);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_banks);
        _sramEnabled = reader.ReadBool();
        reader.ReadBytes(_sram);
        reader.EndChunk();
    }
}
