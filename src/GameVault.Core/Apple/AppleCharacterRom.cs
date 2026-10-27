using GameVault.Core.Cartridge;

namespace GameVault.Core.Apple;

/// <summary>
/// The player's own Apple II character ROM (outstanding-work plan §9), used for text instead of GameVault's own font.
/// Dumps come in several layouts: the II/II+'s 2 KB 2513 generator, the IIe's 4 KB and 8 KB video ROMs, with the
/// glyphs indexed by ASCII, by its low six bits or by screen code, the dots lit high or low, the leftmost dot in bit
/// 0 or bit 6, and the glyph starting on its first or second line. GameVault picks the layout whose capitals and
/// digits look most like its own font's, so no setting is needed.
/// </summary>
public sealed class AppleCharacterRom
{
    private readonly byte[] _rom;
    private readonly Layout _layout;

    /// <exception cref="RomFormatException">The file isn't a character ROM GameVault can read.</exception>
    public AppleCharacterRom(byte[] rom)
    {
        if (rom.Length is not (2048 or 4096 or 8192))
        {
            throw new RomFormatException("An Apple II character ROM is 2, 4 or 8 KB.");
        }

        _rom = rom;
        Layout? best = null;
        var bestScore = -1;
        foreach (var layout in Layouts())
        {
            var score = Score(rom, layout);
            if (score > bestScore)
            {
                bestScore = score;
                best = layout;
            }
        }

        // 36 glyphs of 7 rows of 7 dots: a real character ROM agrees with GameVault's font on most of them.
        if (best is null || bestScore < 36 * 7 * 7 * 85 / 100)
        {
            throw new RomFormatException("That file doesn't look like an Apple II character ROM.");
        }

        _layout = best.Value;
    }

    /// <summary>A character row's 7 dots, bit 6 leftmost (as <see cref="AppleFont.Row"/>), for ASCII $20–$7F.</summary>
    public int Row(int ascii, int row) => row >= 7 ? 0 : Read(_rom, _layout, ascii, row);

    private enum Index : byte
    {
        Ascii,
        LowSixBits,
        ScreenCode, // the normal set at $80–$FF (screen code = ASCII | $80)
    }

    private readonly record struct Layout(Index Index, bool Inverted, bool LeftInBit0, int FirstLine);

    private static IEnumerable<Layout> Layouts()
    {
        foreach (var index in Enum.GetValues<Index>())
        {
            foreach (var inverted in new[] { false, true })
            {
                foreach (var leftInBit0 in new[] { false, true })
                {
                    foreach (var firstLine in new[] { 0, 1 })
                    {
                        yield return new Layout(index, inverted, leftInBit0, firstLine);
                    }
                }
            }
        }
    }

    private static int Read(byte[] rom, Layout layout, int ascii, int row)
    {
        var glyph = layout.Index switch
        {
            Index.Ascii => ascii,
            Index.LowSixBits => ascii & 0x3F,
            _ => ascii | 0x80,
        };
        var at = glyph * 8 + row + layout.FirstLine;
        if (at >= rom.Length)
        {
            return 0;
        }

        var value = layout.Inverted ? ~rom[at] : rom[at];
        var dots = 0;
        for (var d = 0; d < 7; d++)
        {
            // Dot d from the left.
            var bit = layout.LeftInBit0 ? d : 6 - d;
            if (((value >> bit) & 1) != 0)
            {
                dots |= 1 << (6 - d);
            }
        }

        return dots;
    }

    /// <summary>How many dots of A–Z and 0–9 match GameVault's font.</summary>
    private static int Score(byte[] rom, Layout layout)
    {
        var score = 0;
        for (var ascii = 0x30; ascii <= 0x5A; ascii++)
        {
            if (ascii is > 0x39 and < 0x41)
            {
                continue;
            }

            for (var row = 0; row < 7; row++)
            {
                var same = ~(Read(rom, layout, ascii, row) ^ AppleFont.Row(ascii, row)) & 0x7F;
                score += System.Numerics.BitOperations.PopCount((uint)same);
            }
        }

        return score;
    }
}
