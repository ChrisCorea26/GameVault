namespace GameVault.Core.Tests.Cartridge;

/// <summary>
/// Builds synthetic iNES images for mapper tests. Every 8 KB PRG bank is filled with its own bank number,
/// except its last page, which holds the same small program and vectors in every bank, so the code keeps
/// running whatever is switched in. Every 1 KB CHR bank is filled with its bank number.
/// </summary>
internal static class BankedRom
{
    /// <summary>
    /// Turns rendering on, enables the MMC3 IRQ, then loops: increment a counter, store it in PRG-RAM and
    /// write it to every register range. The IRQ handler acknowledges and counts. On other boards those
    /// writes just switch banks.
    /// </summary>
    private static readonly byte[] Program =
    [
        0x78,                   // $FF00 SEI
        0xD8,                   //       CLD
        0xA2, 0xFF, 0x9A,       //       LDX #$FF; TXS
        0xA9, 0x08, 0x8D, 0x00, 0x20, // LDA #$08; STA $2000 (sprites at $1000)
        0xA9, 0x1E, 0x8D, 0x01, 0x20, // LDA #$1E; STA $2001 (rendering on)
        0xA9, 0x10, 0x8D, 0x00, 0xC0, // LDA #$10; STA $C000 (IRQ latch)
        0x8D, 0x01, 0xC0,       //       STA $C001 (reload)
        0x8D, 0x01, 0xE0,       //       STA $E001 (IRQ on)
        0x58,                   //       CLI
        0xE6, 0x00,             // loop: INC $00           ($FF1B)
        0xA5, 0x00,             //       LDA $00
        0xA6, 0x00,             //       LDX $00
        0x9D, 0x00, 0x60,       //       STA $6000,X
        0x8D, 0x00, 0x80,       //       STA $8000
        0x8D, 0x01, 0x80,       //       STA $8001
        0x8D, 0x00, 0xA0,       //       STA $A000
        0x4C, 0x1B, 0xFF,       //       JMP loop
        0x8D, 0x00, 0xE0,       // irq:  STA $E000         ($FF33)
        0x8D, 0x01, 0xE0,       //       STA $E001
        0xE6, 0x01,             //       INC $01
        0x40,                   //       RTI
    ];

    public static byte[] Build(int mapper, int prgBanks8K, int chrBanks1K, int submapper = 0, bool battery = false)
    {
        var prgSize = prgBanks8K * 0x2000;
        var chrSize = chrBanks1K * 0x400;
        var image = new byte[16 + prgSize + chrSize];
        "NES\u001A"u8.CopyTo(image);
        image[4] = (byte)(prgSize / 0x4000);
        image[5] = (byte)(chrSize / 0x2000);
        image[6] = (byte)(((mapper & 0x0F) << 4) | (battery ? 0x02 : 0));
        image[7] = (byte)((mapper & 0xF0) | 0x08); // NES 2.0
        image[8] = (byte)(submapper << 4);
        image[10] = 0x07; // 8 KB PRG-RAM
        if (chrSize == 0)
        {
            image[11] = 0x07; // 8 KB CHR-RAM
        }

        for (var bank = 0; bank < prgBanks8K; bank++)
        {
            var start = 16 + bank * 0x2000;
            image.AsSpan(start, 0x1F00).Fill((byte)bank);
            Program.CopyTo(image, start + 0x1F00);
            image[start + 0x1FFA] = 0x33; // NMI -> RTI area is fine: NMI is off
            image[start + 0x1FFB] = 0xFF;
            image[start + 0x1FFC] = 0x00; // reset
            image[start + 0x1FFD] = 0xFF;
            image[start + 0x1FFE] = 0x33; // IRQ
            image[start + 0x1FFF] = 0xFF;
        }

        for (var bank = 0; bank < chrBanks1K; bank++)
        {
            image.AsSpan(16 + prgSize + bank * 0x400, 0x400).Fill((byte)bank);
        }

        return image;
    }
}
