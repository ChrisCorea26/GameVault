using System.Globalization;

namespace GameVault.Core.Cheats;

public enum CheatKind : byte
{
    /// <summary>Replaces a byte the CPU reads from $8000-$FFFF, like a Game Genie.</summary>
    Rom,

    /// <summary>Writes a value to RAM ($0000-$07FF or cartridge RAM $6000-$7FFF) before every frame.</summary>
    Ram,
}

/// <summary>
/// One code (SPEC §7.8). ROM codes are written as Game Genie letters; RAM codes as <c>AAAA:VV</c> or, with a
/// compare value, <c>AAAA?CC:VV</c> (the value is written only while the byte there equals CC).
/// </summary>
public readonly record struct CheatCode(CheatKind Kind, ushort Address, byte Value, byte? Compare = null)
{
    /// <summary>The Game Genie's letters stand for 0-15 in this order.</summary>
    private const string Letters = "APZLGITYEOXUKSVN";

    /// <summary>
    /// Reads a Game Genie code (6 or 8 letters) or a RAM code. Spaces and dashes are ignored, and letters may
    /// be lower case. Returns false with a plain-language <paramref name="error"/> otherwise.
    /// </summary>
    public static bool TryParse(string? text, out CheatCode code, out string error)
    {
        code = default;
        var trimmed = new string((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (trimmed.Length == 0)
        {
            error = "Type a Game Genie code (6 or 8 letters) or a RAM code like 075A:09.";
            return false;
        }

        if (trimmed.Contains(':'))
        {
            return TryParseRam(trimmed, out code, out error);
        }

        if (trimmed.Length is not (6 or 8))
        {
            error = "Game Genie codes have 6 or 8 letters.";
            return false;
        }

        var digits = new int[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            digits[i] = Letters.IndexOf(trimmed[i]);
            if (digits[i] < 0)
            {
                error = $"\"{trimmed[i]}\" isn't a Game Genie letter. They are A E G I K L N O P S T U V X Y Z.";
                return false;
            }
        }

        code = DecodeGameGenie(digits);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Reads one code or several that work together, joined with "+" ("OZTLLX + AATLGZ + SZLIVO"), as code
    /// lists often write them. All must be valid.
    /// </summary>
    public static bool TryParseList(string? text, out IReadOnlyList<CheatCode> codes, out string error)
    {
        codes = [];
        var parts = (text ?? string.Empty).Split('+');
        var parsed = new List<CheatCode>(parts.Length);
        foreach (var part in parts)
        {
            if (!TryParse(part, out var code, out error))
            {
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(part))
                {
                    error = $"{part.Trim()}: {error}";
                }

                return false;
            }

            parsed.Add(code);
        }

        codes = parsed;
        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<CheatCode> ParseList(string text) =>
        TryParseList(text, out var codes, out var error) ? codes : throw new FormatException(error);

    /// <summary>
    /// How a valid code (or "+" list) is stored and shown: Game Genie letters as the player typed them (upper
    /// case, without spaces or dashes), RAM codes in the standard AAAA:VV form, joined with " + ". Throws
    /// <see cref="FormatException"/> for bad codes.
    /// </summary>
    public static string Normalize(string text)
    {
        _ = ParseList(text);
        return string.Join(" + ", text.Split('+').Select(NormalizeOne));
    }

    private static string NormalizeOne(string text)
    {
        var code = Parse(text);
        var letters = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return letters.Contains(':') ? code.ToString() : letters;
    }

    /// <summary>What a code list does: each code's <see cref="Describe()"/>, separated by semicolons.</summary>
    public static string Describe(IReadOnlyList<CheatCode> codes) => string.Join("; ", codes.Select(c => c.Describe()));

    public static CheatCode Parse(string text) =>
        TryParse(text, out var code, out var error) ? code : throw new FormatException(error);

    /// <summary>
    /// Game Genie decoding (NES Game Genie Code Format DOC, "nesgg.txt", referenced by the NESdev Wiki's
    /// "Game Genie" page). Bit 3 of the third letter tells the device to expect 8 letters; this reader goes by
    /// the code's length, as most emulators do.
    /// </summary>
    private static CheatCode DecodeGameGenie(int[] d)
    {
        var address = 0x8000 | ((d[3] & 7) << 12) | ((d[5] & 7) << 8) | ((d[4] & 8) << 8) | ((d[2] & 7) << 4)
            | ((d[1] & 8) << 4) | (d[4] & 7) | (d[3] & 8);
        if (d.Length == 6)
        {
            var value6 = ((d[1] & 7) << 4) | ((d[0] & 8) << 4) | (d[0] & 7) | (d[5] & 8);
            return new CheatCode(CheatKind.Rom, (ushort)address, (byte)value6);
        }

        var value = ((d[1] & 7) << 4) | ((d[0] & 8) << 4) | (d[0] & 7) | (d[7] & 8);
        var compare = ((d[7] & 7) << 4) | ((d[6] & 8) << 4) | (d[6] & 7) | (d[5] & 8);
        return new CheatCode(CheatKind.Rom, (ushort)address, (byte)value, (byte)compare);
    }

    private static bool TryParseRam(string text, out CheatCode code, out string error)
    {
        code = default;
        var parts = text.Split(':');
        var target = parts[0].TrimStart('$');
        byte? compare = null;
        if (target.Contains('?'))
        {
            var pieces = target.Split('?');
            if (pieces.Length != 2 || !TryHex(pieces[1].TrimStart('$'), 0xFF, out var c))
            {
                error = "Write a compare value as AAAA?CC:VV, for example 075A?03:09.";
                return false;
            }

            target = pieces[0];
            compare = (byte)c;
        }

        if (parts.Length != 2 || !TryHex(target, 0xFFFF, out var address) || !TryHex(parts[1].TrimStart('$'), 0xFF, out var value))
        {
            error = "Write RAM codes as a hex address and value, for example 075A:09.";
            return false;
        }

        if (address >= 0x8000)
        {
            code = new CheatCode(CheatKind.Rom, (ushort)address, (byte)value, compare);
            error = string.Empty;
            return true;
        }

        if (address is >= 0x0800 and < 0x6000)
        {
            error = "RAM codes work on RAM: $0000-$07FF, or cartridge RAM at $6000-$7FFF.";
            return false;
        }

        code = new CheatCode(CheatKind.Ram, (ushort)address, (byte)value, compare);
        error = string.Empty;
        return true;
    }

    private static bool TryHex(string text, int max, out int value) =>
        int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
        && text.Length is > 0 and <= 4 && value <= max;

    /// <summary>The code as players write it: Game Genie letters for ROM codes, AAAA:VV for RAM codes.</summary>
    public override string ToString()
    {
        if (Kind == CheatKind.Ram)
        {
            return Compare is { } c ? $"{Address:X4}?{c:X2}:{Value:X2}" : $"{Address:X4}:{Value:X2}";
        }

        var a = Address;
        var v = Value;
        var eight = Compare.HasValue;
        var c8 = Compare ?? 0;
        Span<int> d = stackalloc int[eight ? 8 : 6];
        d[0] = (v & 7) | ((v >> 4) & 8);
        d[1] = ((v >> 4) & 7) | ((a >> 4) & 8);
        d[2] = ((a >> 4) & 7) | (eight ? 8 : 0);
        d[3] = ((a >> 12) & 7) | (a & 8);
        d[4] = (a & 7) | ((a >> 8) & 8);
        d[5] = ((a >> 8) & 7) | ((eight ? c8 : v) & 8);
        if (eight)
        {
            d[6] = (c8 & 7) | ((c8 >> 4) & 8);
            d[7] = ((c8 >> 4) & 7) | (v & 8);
        }

        var letters = new char[d.Length];
        for (var i = 0; i < d.Length; i++)
        {
            letters[i] = Letters[d[i]];
        }

        return new string(letters);
    }

    /// <summary>What the code does, for the code list: "$91D9 = $AD", "$91D9 = $AD if it was $CE", "RAM $075A = $09".</summary>
    public string Describe() => Kind == CheatKind.Ram
        ? $"RAM ${Address:X4} = ${Value:X2}" + (Compare is { } rc ? $" while it is ${rc:X2}" : string.Empty)
        : $"${Address:X4} = ${Value:X2}" + (Compare is { } c ? $" if it was ${c:X2}" : string.Empty);
}
