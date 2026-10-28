using System.Text;

namespace GameVault.Core.Genesis;

/// <summary>How a cartridge board joins its serial EEPROM to the 68000's bus (genesis-gaps.md §1).</summary>
public enum GenesisEepromWiring
{
    /// <summary>
    /// Sega's boards (171-5878 and later): $200000–$3FFFFF, SDA on D0 and SCL on D1 of the odd byte (or a word),
    /// SDA read back on D0.
    /// </summary>
    Sega,

    /// <summary>Electronic Arts' boards (PWA P10003/P10004): as Sega's, with SDA on D7 and SCL on D6.</summary>
    ElectronicArts,

    /// <summary>
    /// Acclaim's 16 Mbit board (670120): $200000–$3FFFFF, any write sets SDA from D0 and SCL from D1; SDA reads back
    /// on D1.
    /// </summary>
    Acclaim16M,

    /// <summary>
    /// Acclaim's 32 Mbit boards (670125/670127): in $200000–$2FFFFF a byte write to an odd address sets SDA from D0,
    /// to an even address SCL from D0, and a word write chooses what reads there (D0 set: the ROM; clear: the
    /// EEPROM, SDA on D0). The ROM shows there after a reset.
    /// </summary>
    Acclaim32M,

    /// <summary>
    /// Codemasters' boards (SRJCV2): writes to $300000–$37FFFF set SDA from D0 and SCL from D1; SDA reads back on D7
    /// of $380000–$3FFFFF.
    /// </summary>
    Codemasters,
}

/// <summary>A board's EEPROM: its wiring, its addressing, its size and its write page.</summary>
public sealed record GenesisEepromBoard(GenesisEepromWiring Wiring, I2cEepromKind Kind, int Size, int PageSize)
{
    /// <summary>Sega's boards with an X24C01, the layout the header's "RA" $E8 $40 declares.</summary>
    public static GenesisEepromBoard SegaDefault { get; } = new(GenesisEepromWiring.Sega, I2cEepromKind.X24C01, 128, 4);
}

/// <summary>
/// The cartridges whose EEPROM the header doesn't describe (or describes as Sega's when it's wired otherwise), by the
/// header's product code and, where Codemasters left it blank, the header checksum and initial stack pointer.
/// </summary>
/// <remarks>
/// The list and each board's wiring are the ones Genesis Plus GX's eeprom_i2c.c documents (read for behaviour only);
/// sizes and write pages are the chips' data sheets: X24C01 128 bytes/4, X24C02 256/4, 24C02 256/8, 24C04 512/16,
/// 24C08 1 KB/16, 24C16 2 KB/16, 24C65 8 KB/64.
/// </remarks>
public static class GenesisEepromBoards
{
    private sealed record Entry(string Product, uint StackPointer, ushort Checksum, bool SkipIfSramHack, GenesisEepromBoard Board);

    private static readonly GenesisEepromBoard X24C01Sega = GenesisEepromBoard.SegaDefault;
    private static readonly GenesisEepromBoard X24C01Ea = new(GenesisEepromWiring.ElectronicArts, I2cEepromKind.X24C01, 128, 4);
    private static readonly GenesisEepromBoard X24C02Acclaim = new(GenesisEepromWiring.Acclaim16M, I2cEepromKind.Standard, 256, 4);

    private static GenesisEepromBoard Acclaim(int size, int page) =>
        new(GenesisEepromWiring.Acclaim32M, size > 2048 ? I2cEepromKind.TwoByteAddress : I2cEepromKind.Standard, size, page);

    private static GenesisEepromBoard Codemasters(int size, int page) =>
        new(GenesisEepromWiring.Codemasters, size > 2048 ? I2cEepromKind.TwoByteAddress : I2cEepromKind.Standard, size, page);

    private static readonly Entry[] Games =
    [
        // Electronic Arts
        new("T-50176", 0, 0, false, X24C01Ea),   // Rings of Power
        new("T-50396", 0, 0, false, X24C01Ea),   // NHLPA Hockey '93
        new("T-50446", 0, 0, false, X24C01Ea),   // John Madden Football '93
        new("T-50516", 0, 0, false, X24C01Ea),   // John Madden Football '93: Championship Edition
        new("T-50606", 0, 0, false, X24C01Ea),   // Bill Walsh College Football (its header claims SRAM)

        // Sega, on games whose header doesn't say so (patched copies with SRAM in the header are left alone)
        new(" T-12046", 0, 0, true, X24C01Sega), // Mega Man: The Wily Wars
        new(" T-12053", 0, 0, true, X24C01Sega), // Rockman Mega World
        new("MK-1215", 0, 0, false, X24C01Sega), // Evander Holyfield's "Real Deal" Boxing
        new("MK-1228", 0, 0, false, X24C01Sega), // Greatest Heavyweights (USA, Europe)
        new("G-5538", 0, 0, false, X24C01Sega),  // Greatest Heavyweights (Japan)
        new("PR-1993", 0, 0, false, X24C01Sega), // Greatest Heavyweights (prototype)
        new(" G-4060", 0, 0, true, X24C01Sega),  // Wonder Boy in Monster World
        new("00001211", 0, 0, false, X24C01Sega), // Sports Talk Baseball
        new("00004076", 0, 0, false, X24C01Sega), // Honoo no Toukyuuji Dodge Danpei
        new("G-4524", 0, 0, false, X24C01Sega),  // Ninja Burai Densetsu
        new("00054503", 0, 0, false, X24C01Sega), // Game Toshokan

        // Acclaim
        new("T-81033", 0, 0, false, X24C02Acclaim),  // NBA Jam (Japan)
        new("T-081326", 0, 0, false, X24C02Acclaim), // NBA Jam (USA, Europe)
        new("T-081276", 0, 0, false, Acclaim(256, 8)),    // NFL Quarterback Club
        new("T-81406", 0, 0, false, Acclaim(512, 16)),    // NBA Jam Tournament Edition
        new("T-081586", 0, 0, false, Acclaim(2048, 16)),  // NFL Quarterback Club '96
        new("T-81476", 0, 0, false, Acclaim(8192, 64)),   // Frank Thomas Big Hurt Baseball
        new("T-81576", 0, 0, false, Acclaim(8192, 64)),   // College Slam

        // Codemasters
        new("T-120106", 0, 0, false, Codemasters(1024, 16)),                // Brian Lara Cricket
        new("00000000", 0x444E4C44, 0x168B, false, Codemasters(1024, 16)), // Micro Machines Military
        new("00000000", 0x444E4C44, 0x165E, false, Codemasters(2048, 16)), // Micro Machines Turbo Tournament '96
        new("T-120096", 0, 0, false, Codemasters(2048, 16)),                // Micro Machines 2: Turbo Tournament
        new("T-120146", 0, 0, false, Codemasters(8192, 64)),                // Brian Lara Cricket '96, Shane Warne Cricket
    ];

    /// <summary>
    /// The board for a ROM listed here, or null. <paramref name="sramInHeader"/> says the header declares more than
    /// two bytes of SRAM (the sign of a copy patched to save in SRAM instead).
    /// </summary>
    public static GenesisEepromBoard? Find(ReadOnlySpan<byte> rom, bool sramInHeader)
    {
        if (rom.Length < 0x200)
        {
            return null;
        }

        var product = Encoding.ASCII.GetString(rom.Slice(0x180, 14));
        var checksum = (ushort)((rom[0x18E] << 8) | rom[0x18F]);
        var stack = (uint)((rom[0] << 24) | (rom[1] << 16) | (rom[2] << 8) | rom[3]);
        foreach (var game in Games)
        {
            if (!product.Contains(game.Product, StringComparison.Ordinal))
            {
                continue;
            }

            if (game.SkipIfSramHack && sramInHeader)
            {
                return null;
            }

            if ((game.Checksum == 0 || game.Checksum == checksum) && (game.StackPointer == 0 || game.StackPointer == stack))
            {
                return game.Board;
            }
        }

        return null;
    }
}
