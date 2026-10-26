namespace GameVault.Core.Apple;

/// <summary>The video soft switches that pick what the Apple II shows.</summary>
public struct AppleVideoMode
{
    public bool Text;
    public bool Mixed;
    public bool Page2;
    public bool HiRes;

    /// <summary>IIe: 80STORE makes PAGE2 pick auxiliary memory instead of page 2.</summary>
    public bool Store80;

    public bool Col80;
    public bool AltCharset;
    public bool DoubleHiRes;

    /// <summary>The II+ and the unenhanced IIe have no lowercase characters.</summary>
    public bool Lowercase;
}

/// <summary>
/// Draws the Apple II screen (SPEC §7.11, Phase 12) into a 560×384 frame: each of the 192 lines twice, each of
/// the 280 pixels as two dots (so 80-column text and double hi-res keep every dot). Hi-res colours come from the
/// NTSC artifact rules: lit pixels next to each other are white, a lone pixel is violet or green by column (blue or
/// orange with the byte's high bit), and the high bit also shifts its pixels by half a pixel.
/// </summary>
/// <remarks>Reference: "Apple II Reference Manual" (1979) and "Apple IIe Technical Reference Manual", chapter 2.</remarks>
public static class AppleVideo
{
    public const int Width = 560;
    public const int Height = 384;

    public const int Black = 0, Violet = 3, Green = 12, Blue = 6, Orange = 9, White = 15;

    /// <summary>The 16 lo-res colours, which hi-res and double hi-res use too.</summary>
    public static ReadOnlySpan<uint> Palette =>
    [
        0xFF000000, 0xFFDD0033, 0xFF000099, 0xFFDD22DD, 0xFF007722, 0xFF555555, 0xFF2222FF, 0xFF66AAFF,
        0xFF885500, 0xFFFF6600, 0xFFAAAAAA, 0xFFFF9988, 0xFF11DD00, 0xFFFFFF00, 0xFF44FF99, 0xFFFFFFFF,
    ];

    /// <summary>The address of text/lo-res row <paramref name="row"/> (0–23) on page 1.</summary>
    public static int TextRowAddress(int row) => 0x400 + (row & 7) * 0x80 + (row >> 3) * 0x28;

    /// <summary>The address of hi-res line <paramref name="line"/> (0–191) on page 1.</summary>
    public static int HiResLineAddress(int line) => 0x2000 + (line & 7) * 0x400 + ((line >> 3) & 7) * 0x80 + (line >> 6) * 0x28;

    /// <param name="main">Main memory (64 KB).</param>
    /// <param name="aux">Auxiliary memory (IIe 80-column card), or empty.</param>
    /// <param name="flashOn">The flashing-character phase.</param>
    public static void Render(Span<ushort> frame, ReadOnlySpan<byte> main, ReadOnlySpan<byte> aux, AppleVideoMode mode, bool flashOn)
    {
        Span<byte> dots = stackalloc byte[Width];
        var page2 = mode.Page2 && !mode.Store80;
        for (var line = 0; line < 192; line++)
        {
            var text = mode.Text || (mode.Mixed && line >= 160);
            if (text)
            {
                TextLine(dots, main, aux, line, page2, mode, flashOn);
            }
            else if (mode.HiRes && mode.DoubleHiRes && mode.Col80 && !aux.IsEmpty)
            {
                DoubleHiResLine(dots, main, aux, line, page2);
            }
            else if (mode.HiRes)
            {
                HiResLine(dots, main, line, page2);
            }
            else
            {
                LoResLine(dots, main, line, page2);
            }

            var upper = frame.Slice(line * 2 * Width, Width);
            for (var x = 0; x < Width; x++)
            {
                upper[x] = dots[x];
            }

            upper.CopyTo(frame.Slice((line * 2 + 1) * Width, Width));
        }
    }

    private static void TextLine(Span<byte> dots, ReadOnlySpan<byte> main, ReadOnlySpan<byte> aux, int line, bool page2, AppleVideoMode mode, bool flashOn)
    {
        var row = line >> 3;
        var glyphRow = line & 7;
        var address = TextRowAddress(row) + (page2 ? 0x400 : 0);
        if (mode.Col80 && !aux.IsEmpty)
        {
            // 80 columns: auxiliary memory holds the even columns, main memory the odd ones; 7 dots each.
            for (var column = 0; column < 80; column++)
            {
                var code = (column & 1) == 0 ? aux[address + column / 2] : main[address + column / 2];
                var bits = CharacterDots(code, glyphRow, mode, flashOn);
                for (var d = 0; d < 7; d++)
                {
                    dots[column * 7 + d] = (byte)(((bits >> (6 - d)) & 1) != 0 ? White : Black);
                }
            }

            return;
        }

        for (var column = 0; column < 40; column++)
        {
            var bits = CharacterDots(main[address + column], glyphRow, mode, flashOn);
            for (var d = 0; d < 7; d++)
            {
                var color = (byte)(((bits >> (6 - d)) & 1) != 0 ? White : Black);
                dots[column * 14 + d * 2] = color;
                dots[column * 14 + d * 2 + 1] = color;
            }
        }
    }

    /// <summary>The 7 dots of a character row, bit 6 leftmost, with inverse and flashing applied.</summary>
    private static int CharacterDots(byte code, int row, AppleVideoMode mode, bool flashOn)
    {
        int ascii;
        bool inverse;
        if (code >= 0x80)
        {
            ascii = code & 0x7F;
            inverse = false;
            if (ascii < 0x20)
            {
                ascii += 0x40;
            }
        }
        else if (code >= 0x40 && mode.AltCharset)
        {
            // IIe alternate set: $60–$7F is inverse lowercase; $40–$5F (MouseText) shows as inverse capitals here.
            ascii = code >= 0x60 ? code : (code & 0x1F) + 0x40;
            inverse = true;
        }
        else
        {
            var index = code & 0x3F;
            ascii = index < 0x20 ? index + 0x40 : index;
            inverse = code < 0x40 || flashOn; // $40–$7F flash
        }

        if (!mode.Lowercase && ascii >= 0x60)
        {
            ascii -= 0x20;
        }

        var dots = AppleFont.Row(ascii, row);
        return inverse ? ~dots & 0x7F : dots;
    }

    private static void LoResLine(Span<byte> dots, ReadOnlySpan<byte> main, int line, bool page2)
    {
        var address = TextRowAddress(line >> 3) + (page2 ? 0x400 : 0);
        var lowerHalf = (line & 4) != 0;
        for (var column = 0; column < 40; column++)
        {
            var value = main[address + column];
            var color = (byte)(lowerHalf ? value >> 4 : value & 0x0F);
            dots.Slice(column * 14, 14).Fill(color);
        }
    }

    private static void HiResLine(Span<byte> dots, ReadOnlySpan<byte> main, int line, bool page2)
    {
        Span<bool> on = stackalloc bool[282];
        Span<bool> shifted = stackalloc bool[282];
        var address = HiResLineAddress(line) + (page2 ? 0x2000 : 0);
        for (var b = 0; b < 40; b++)
        {
            var value = main[address + b];
            for (var bit = 0; bit < 7; bit++)
            {
                on[1 + b * 7 + bit] = ((value >> bit) & 1) != 0;
                shifted[1 + b * 7 + bit] = (value & 0x80) != 0;
            }
        }

        dots.Clear();
        for (var x = 0; x < 280; x++)
        {
            var i = x + 1;
            byte color;
            if (on[i])
            {
                color = on[i - 1] || on[i + 1] ? (byte)White : ColorAt(x, shifted[i]);
            }
            else if (on[i - 1] && on[i + 1] && x > 0 && x < 279 && shifted[i - 1] == shifted[i + 1])
            {
                color = ColorAt(x - 1, shifted[i - 1]); // NTSC fills the gap between same-coloured pixels
            }
            else
            {
                color = Black;
            }

            var start = x * 2 + (shifted[i] ? 1 : 0);
            dots[start] = color;
            if (start + 1 < Width)
            {
                dots[start + 1] = color;
            }
        }
    }

    private static byte ColorAt(int x, bool shifted) =>
        (byte)((x & 1) == 0 ? (shifted ? Blue : Violet) : (shifted ? Orange : Green));

    private static void DoubleHiResLine(Span<byte> dots, ReadOnlySpan<byte> main, ReadOnlySpan<byte> aux, int line, bool page2)
    {
        Span<bool> bits = stackalloc bool[Width];
        var address = HiResLineAddress(line) + (page2 ? 0x2000 : 0);
        for (var b = 0; b < 40; b++)
        {
            for (var bit = 0; bit < 7; bit++)
            {
                bits[b * 14 + bit] = ((aux[address + b] >> bit) & 1) != 0;
                bits[b * 14 + 7 + bit] = ((main[address + b] >> bit) & 1) != 0;
            }
        }

        // Each group of four dots is one of the 16 colours (dot n is colour bit n).
        for (var cell = 0; cell < Width / 4; cell++)
        {
            var color = 0;
            for (var d = 0; d < 4; d++)
            {
                if (bits[cell * 4 + d])
                {
                    color |= 1 << d;
                }
            }

            dots.Slice(cell * 4, 4).Fill((byte)color);
        }
    }
}
