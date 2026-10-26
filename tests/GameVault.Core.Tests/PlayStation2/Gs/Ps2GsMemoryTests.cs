using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>
/// Local memory swizzling: every format maps a page one to one onto its 8 KB, round trips, and the formats alias each
/// other as the hardware lays them out (column tables as the GS User's Manual draws them and PCSX2's GSLocalMemory
/// lists them).
/// </summary>
public sealed class Ps2GsMemoryTests
{
    public static TheoryData<int, int, int, int> Formats => new()
    {
        // psm, page width, page height, bits per pixel
        { Ps2Gs.PsmCt32, 64, 32, 32 },
        { Ps2Gs.PsmZ32, 64, 32, 32 },
        { Ps2Gs.PsmCt16, 64, 64, 16 },
        { Ps2Gs.PsmCt16S, 64, 64, 16 },
        { Ps2Gs.PsmZ16, 64, 64, 16 },
        { Ps2Gs.PsmZ16S, 64, 64, 16 },
        { Ps2Gs.PsmT8, 128, 64, 8 },
        { Ps2Gs.PsmT4, 128, 128, 4 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_page_maps_each_pixel_to_its_own_bytes(int psm, int pageWidth, int pageHeight, int bits)
    {
        var seen = new bool[8192 * 8 / bits];
        for (var y = 0; y < pageHeight; y++)
        {
            for (var x = 0; x < pageWidth; x++)
            {
                var address = Ps2Gs.PixelAddress(psm, 0, pageWidth / 64 * 2, x, y);
                var unit = bits == 4 ? address : address / (bits / 8);
                unit.Should().BeLessThan(seen.Length);
                seen[unit].Should().BeFalse($"({x}, {y}) shares its storage");
                seen[unit] = true;
            }
        }
    }

    [Theory]
    [InlineData(Ps2Gs.PsmCt32, 0xFFFFFFFFu)]
    [InlineData(Ps2Gs.PsmCt24, 0xFFFFFFu)]
    [InlineData(Ps2Gs.PsmCt16, 0xFFFFu)]
    [InlineData(Ps2Gs.PsmCt16S, 0xFFFFu)]
    [InlineData(Ps2Gs.PsmZ32, 0xFFFFFFFFu)]
    [InlineData(Ps2Gs.PsmZ24, 0xFFFFFFu)]
    [InlineData(Ps2Gs.PsmZ16, 0xFFFFu)]
    [InlineData(Ps2Gs.PsmZ16S, 0xFFFFu)]
    [InlineData(Ps2Gs.PsmT8, 0xFFu)]
    [InlineData(Ps2Gs.PsmT4, 0xFu)]
    [InlineData(Ps2Gs.PsmT8H, 0xFFu)]
    [InlineData(Ps2Gs.PsmT4HL, 0xFu)]
    [InlineData(Ps2Gs.PsmT4HH, 0xFu)]
    public void Pixels_round_trip_across_pages_and_buffer_widths(int psm, uint mask)
    {
        var gs = new Ps2Gs();
        const int bp = 0x40;
        const int bw = 6;
        for (var y = 0; y < 150; y += 3)
        {
            for (var x = 0; x < 384; x += 5)
            {
                gs.WritePixel(psm, bp, bw, x, y, Value(x, y));
            }
        }

        for (var y = 0; y < 150; y += 3)
        {
            for (var x = 0; x < 384; x += 5)
            {
                gs.ReadPixel(psm, bp, bw, x, y).Should().Be(Value(x, y) & mask, $"pixel ({x}, {y})");
            }
        }
    }

    private static uint Value(int x, int y) => (uint)((x * 0x9E3779B1) ^ (y * 0x85EBCA77));

    [Fact]
    public void Psmct32_columns_interleave_pairs_of_pixels()
    {
        // A PSMCT32 column is 8×2 pixels: words 0 1 4 5 8 9 12 13 on the first row, 2 3 6 7 ... on the second.
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 0, 0).Should().Be(0);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 1, 0).Should().Be(4);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 2, 0).Should().Be(16);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 0, 1).Should().Be(8);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 0, 2).Should().Be(64, "the next column");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 8, 0).Should().Be(256, "block 1 is to the right of block 0");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 0, 8).Should().Be(512, "block 2 is below block 0");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, 16, 0).Should().Be(4 * 256, "block 4");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 2, 64, 0).Should().Be(8192, "the next page across");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 2, 0, 32).Should().Be(2 * 8192, "a two-page-wide buffer's second row of pages");
        Ps2Gs.PixelAddress(Ps2Gs.PsmZ32, 0, 1, 0, 0).Should().Be(24 * 256, "PSMZ32 starts with block 24");
    }

    [Fact]
    public void Psmct16_keeps_pixels_x_and_x_plus_8_in_one_word()
    {
        var gs = new Ps2Gs();
        gs.WritePixel(Ps2Gs.PsmCt16, 0, 1, 0, 0, 0x1111);
        gs.WritePixel(Ps2Gs.PsmCt16, 0, 1, 8, 0, 0x2222);
        gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 0, 0).Should().Be(0x2222_1111);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt16, 0, 1, 1, 0).Should().Be(4);
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt16, 0, 1, 16, 0).Should().Be(2 * 256, "a PSMCT16 block is 16 pixels wide and block 2 is next to block 0");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt16S, 0, 1, 32, 0).Should().Be(16 * 256, "PSMCT16S puts block 16 third across");
        Ps2Gs.PixelAddress(Ps2Gs.PsmCt16, 0, 1, 32, 0).Should().Be(8 * 256, "PSMCT16 puts block 8 there");
    }

    [Fact]
    public void Psmt8_column_matches_the_hardware_layout()
    {
        // Byte offsets inside the first column of a PSMT8 block for rows 0–3 (GS User's Manual, PSMT8 column;
        // the same values as PCSX2's columnTable8).
        int[,] expected =
        {
            { 0, 4, 16, 20, 32, 36, 48, 52, 2, 6, 18, 22, 34, 38, 50, 54 },
            { 8, 12, 24, 28, 40, 44, 56, 60, 10, 14, 26, 30, 42, 46, 58, 62 },
            { 33, 37, 49, 53, 1, 5, 17, 21, 35, 39, 51, 55, 3, 7, 19, 23 },
            { 41, 45, 57, 61, 9, 13, 25, 29, 43, 47, 59, 63, 11, 15, 27, 31 },
        };
        var gs = new Ps2Gs();
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Ps2Gs.PixelAddress(Ps2Gs.PsmT8, 0, 2, x, y).Should().Be(expected[y, x], $"pixel ({x}, {y})");
                gs.WritePixel(Ps2Gs.PsmT8, 0, 2, x, y, (uint)expected[y, x]);
            }
        }

        // Read back as PSMCT32 the first column holds bytes 0..63 in order.
        for (var word = 0; word < 16; word++)
        {
            var (x, y) = Ct32ColumnWord(word);
            var value = gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, x, y);
            var b = (uint)(word * 4);
            value.Should().Be(b | ((b + 1) << 8) | ((b + 2) << 16) | ((b + 3) << 24), $"word {word}");
        }
    }

    [Fact]
    public void Psmt4_column_matches_the_hardware_layout()
    {
        // Nibble offsets of the first row of a PSMT4 column (PCSX2's columnTable4, row 0).
        int[] expected = [0, 8, 32, 40, 64, 72, 96, 104, 2, 10, 34, 42, 66, 74, 98, 106, 4, 12, 36, 44, 68, 76, 100, 108, 6, 14, 38, 46, 70, 78, 102, 110];
        for (var x = 0; x < 32; x++)
        {
            Ps2Gs.PixelAddress(Ps2Gs.PsmT4, 0, 2, x, 0).Should().Be(expected[x], $"pixel {x}");
        }

        Ps2Gs.PixelAddress(Ps2Gs.PsmT4, 0, 2, 32, 0).Should().Be(2 * 256 * 2, "PSMT4 blocks are 32 pixels wide and laid out like PSMCT16's");
    }

    [Fact]
    public void Upper_byte_and_nibble_formats_share_the_alpha_byte_of_psmct32()
    {
        var gs = new Ps2Gs();
        gs.WritePixel(Ps2Gs.PsmCt24, 0, 1, 3, 5, 0x00123456);
        gs.WritePixel(Ps2Gs.PsmT8H, 0, 1, 3, 5, 0xA7);
        gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 3, 5).Should().Be(0xA712_3456);
        gs.WritePixel(Ps2Gs.PsmT4HL, 0, 1, 3, 5, 0x1);
        gs.WritePixel(Ps2Gs.PsmT4HH, 0, 1, 3, 5, 0xC);
        gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 3, 5).Should().Be(0xC112_3456);
        gs.WritePixel(Ps2Gs.PsmCt24, 0, 1, 3, 5, 0xFFABCDEF);
        gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 3, 5).Should().Be(0xC1AB_CDEF, "PSMCT24 leaves the upper byte alone");
    }

    private static (int X, int Y) Ct32ColumnWord(int word)
    {
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (Ps2Gs.PixelAddress(Ps2Gs.PsmCt32, 0, 1, x, y) == word * 4)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException();
    }
}
