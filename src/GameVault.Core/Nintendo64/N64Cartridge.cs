using System.Buffers.Binary;
using System.Text;
using GameVault.Core.Cartridge;

namespace GameVault.Core.Nintendo64;

/// <summary>What a cartridge saves to (n64brew: "Game Pak").</summary>
public enum N64SaveType
{
    None,
    Eeprom4K,
    Eeprom16K,
    Sram,
    FlashRam,
}

/// <summary>The PIF's companion chip on the cartridge, which seeds the boot code's checksum (n64brew: "CIC-NUS").</summary>
public enum N64Cic
{
    Cic6101,
    Cic6102,
    Cic6103,
    Cic6105,
    Cic6106,
}

/// <summary>The TV system the cartridge was made for, from its region code.</summary>
public enum N64Region
{
    Ntsc,
    Pal,
    Mpal,
}

/// <summary>
/// An N64 cartridge (SPEC §7.11, Phase 16): the ROM in big-endian order, its header, its CIC and its save memory.
/// </summary>
/// <remarks>
/// ROM dumps come in three byte orders: <c>.z64</c> is big-endian (starts 80 37 12 40), <c>.v64</c> swaps each
/// halfword's bytes (37 80 40 12) and <c>.n64</c> is little-endian by word (40 12 37 80). All are turned into
/// big-endian here. The header (n64brew: "ROM Header") holds the boot address, the name, the game code and the
/// region; the boot code (IPL3) follows it up to $1000.
/// </remarks>
public sealed class N64Cartridge : IBatteryBacked
{
    public const int HeaderSize = 0x40;
    public const int BootCodeEnd = 0x1000;

    private readonly byte[] _save;

    public N64Cartridge(byte[] image, N64SaveType? saveType = null)
    {
        if (image.Length < BootCodeEnd)
        {
            throw new RomFormatException("This file is too small to be an N64 cartridge.");
        }

        Rom = ToBigEndian(image);
        Name = Encoding.ASCII.GetString(Rom, 0x20, 20).TrimEnd(' ', '\0');
        GameCode = Encoding.ASCII.GetString(Rom, 0x3B, 4).TrimEnd('\0');
        Region = RegionOf((char)Rom[0x3E]);
        Cic = DetectCic(Rom);
        SaveType = saveType ?? HomebrewSaveType(Rom) ?? N64SaveType.Eeprom4K;
        _save = new byte[SaveSize(SaveType)];
        Array.Fill(_save, SaveType is N64SaveType.FlashRam or N64SaveType.Sram ? (byte)0xFF : (byte)0);
    }

    /// <summary>The ROM, big-endian.</summary>
    public byte[] Rom { get; }

    public string Name { get; }

    /// <summary>The four-character game code: media type, two-letter id and region (such as "NSME").</summary>
    public string GameCode { get; }

    public N64Region Region { get; }

    public N64Cic Cic { get; }

    public N64SaveType SaveType { get; }

    /// <summary>The address the boot code jumps to (the header's entry point).</summary>
    public uint EntryPoint => BinaryPrimitives.ReadUInt32BigEndian(Rom.AsSpan(8));

    public bool HasBattery => SaveType != N64SaveType.None;

    /// <summary>The save memory, big-endian, in the raw form other emulators read (.eep, .sra, .fla).</summary>
    public Span<byte> BatteryRam => _save;

    internal byte[] Save => _save;

    /// <summary>True when the data starts like an N64 ROM in any of the three byte orders.</summary>
    public static bool IsN64Rom(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && (data[..4].SequenceEqual(stackalloc byte[] { 0x80, 0x37, 0x12, 0x40 })
            || data[..4].SequenceEqual(stackalloc byte[] { 0x37, 0x80, 0x40, 0x12 })
            || data[..4].SequenceEqual(stackalloc byte[] { 0x40, 0x12, 0x37, 0x80 }));

    /// <summary>Turns a ROM in any byte order into big-endian (a copy; unknown orders are taken as big-endian).</summary>
    public static byte[] ToBigEndian(ReadOnlySpan<byte> image)
    {
        var rom = image.ToArray();
        if (rom.Length < 4)
        {
            return rom;
        }

        if (rom[0] == 0x37 && rom[1] == 0x80)
        {
            for (var i = 0; i + 1 < rom.Length; i += 2)
            {
                (rom[i], rom[i + 1]) = (rom[i + 1], rom[i]);
            }
        }
        else if (rom[0] == 0x40 && rom[1] == 0x12)
        {
            for (var i = 0; i + 3 < rom.Length; i += 4)
            {
                (rom[i], rom[i + 1], rom[i + 2], rom[i + 3]) = (rom[i + 3], rom[i + 2], rom[i + 1], rom[i]);
            }
        }

        return rom;
    }

    public static int SaveSize(N64SaveType type) => type switch
    {
        N64SaveType.Eeprom4K => 512,
        N64SaveType.Eeprom16K => 2048,
        N64SaveType.Sram => 32 * 1024,
        N64SaveType.FlashRam => 128 * 1024,
        _ => 0,
    };

    /// <summary>The seed the CIC gives the boot code (n64brew: "CIC-NUS"), passed to it in s6.</summary>
    public static byte Seed(N64Cic cic) => cic switch
    {
        N64Cic.Cic6103 => 0x78,
        N64Cic.Cic6105 => 0x91,
        N64Cic.Cic6106 => 0x85,
        _ => 0x3F,
    };

    /// <summary>
    /// Finds the CIC from the boot code: the sum of its 32-bit words (as Mupen64Plus identifies them). Unknown boot
    /// code, such as homebrew's own, is taken as a 6102, the most common.
    /// </summary>
    public static N64Cic DetectCic(ReadOnlySpan<byte> rom)
    {
        ulong sum = 0;
        for (var i = HeaderSize; i < BootCodeEnd; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(rom[i..]);
        }

        return sum switch
        {
            0x0000_00A0_F26F_62FE => N64Cic.Cic6101,
            0x0000_00A3_16AD_C55A => N64Cic.Cic6102,
            0x0000_00A9_229D_7C45 => N64Cic.Cic6103,
            0x0000_00F8_B860_ED00 => N64Cic.Cic6105,
            0x0000_00BA_5BA4_B8CD => N64Cic.Cic6106,
            _ => N64Cic.Cic6102,
        };
    }

    /// <summary>
    /// The save type from libdragon's homebrew header: game code "ED" at $3C, and the save type in the top nibble
    /// of $3F (1 EEPROM 4K, 2 EEPROM 16K, 3 SRAM 256K, 5 FlashRAM; n64brew: "ROM Header", advanced homebrew).
    /// </summary>
    public static N64SaveType? HomebrewSaveType(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < HeaderSize || rom[0x3C] != (byte)'E' || rom[0x3D] != (byte)'D')
        {
            return null;
        }

        return (rom[0x3F] >> 4) switch
        {
            0 => N64SaveType.None,
            1 => N64SaveType.Eeprom4K,
            2 => N64SaveType.Eeprom16K,
            3 or 4 or 6 => N64SaveType.Sram,
            5 => N64SaveType.FlashRam,
            _ => null,
        };
    }

    private static N64Region RegionOf(char code) => code switch
    {
        'D' or 'F' or 'H' or 'I' or 'P' or 'S' or 'U' or 'W' or 'X' or 'Y' => N64Region.Pal,
        'B' => N64Region.Mpal,
        _ => N64Region.Ntsc,
    };
}
