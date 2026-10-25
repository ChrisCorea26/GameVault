using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameVault.Library.Passwords;

/// <summary>One RAM test: (RAM[Address] &amp; Mask) == Value. Addresses are CPU addresses ($0000-$07FF, $6000-$7FFF).</summary>
public sealed record RamCondition
{
    [JsonConverter(typeof(HexConverter))]
    public required int Address { get; init; }

    [JsonConverter(typeof(HexConverter))]
    public int Mask { get; init; } = 0xFF;

    [JsonConverter(typeof(HexConverter))]
    public required int Value { get; init; }

    public bool Holds(Func<int, byte> readRam) => (readRam(Address) & Mask) == (Value & Mask);
}

/// <summary>Where the password's characters are read from.</summary>
public sealed record PasswordSource
{
    /// <summary>"ram" (one byte per character) or "nametable" (one tile per character).</summary>
    public required string From { get; init; }

    /// <summary>A CPU address for "ram", a PPU address ($2000-$2FFF) for "nametable".</summary>
    [JsonConverter(typeof(HexConverter))]
    public required int Address { get; init; }

    public required int Length { get; init; }

    /// <summary>For "nametable": characters per row before jumping to the next row (default: all on one row).</summary>
    public int Columns { get; init; }

    /// <summary>For "nametable": bytes between rows (32 for consecutive rows, 64 for every other row).</summary>
    public int RowStride { get; init; } = 32;

    /// <summary>For "ram": bytes between characters (default 1).</summary>
    public int Stride { get; init; } = 1;
}

/// <summary>
/// How to recognize a game's password screen and read the password off it (SPEC §7.3). The format is
/// documented in docs/password-definitions.md. Definitions contain addresses and character tables only,
/// never game data.
/// </summary>
public sealed record PasswordDefinition
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public string? Sha1 { get; init; }

    public string? Title { get; init; }

    /// <summary>All must hold for <see cref="StableFrames"/> frames in a row.</summary>
    public required IReadOnlyList<RamCondition> Detect { get; init; }

    public int StableFrames { get; init; } = 30;

    public required PasswordSource Source { get; init; }

    /// <summary>
    /// Value → character. Either a string, where the value is the index ("ABC…"), or a map of hex values to
    /// characters ({ "0x0A": "A" }). Unknown values become '?'.
    /// </summary>
    public JsonElement Alphabet { get; init; }

    /// <summary>Characters per group when displayed, e.g. [6, 6, 6, 6] → "ABCDEF GHIJKL …". Empty: one group.</summary>
    public IReadOnlyList<int> Groups { get; init; } = [];

    public static PasswordDefinition Parse(string json) =>
        JsonSerializer.Deserialize<PasswordDefinition>(json, Json) ?? throw new JsonException("Empty password definition.");

    public bool IsPasswordScreen(Func<int, byte> readRam) => Detect.All(c => c.Holds(readRam));

    /// <summary>Reads and formats the password.</summary>
    /// <param name="readRam">CPU memory (RAM and PRG-RAM).</param>
    /// <param name="readNametable">PPU nametable memory ($2000-$2FFF), without side effects.</param>
    public string Decode(Func<int, byte> readRam, Func<int, byte> readNametable)
    {
        var map = BuildAlphabet();
        var chars = new char[Source.Length];
        for (var i = 0; i < Source.Length; i++)
        {
            byte value;
            if (Source.From.Equals("nametable", StringComparison.OrdinalIgnoreCase))
            {
                var columns = Source.Columns > 0 ? Source.Columns : Source.Length;
                value = readNametable(Source.Address + (i / columns * Source.RowStride) + (i % columns));
            }
            else
            {
                value = readRam(Source.Address + i * Source.Stride);
            }

            chars[i] = map.TryGetValue(value, out var c) ? c : '?';
        }

        return Format(new string(chars));
    }

    private string Format(string password)
    {
        if (Groups.Count == 0)
        {
            return password;
        }

        var parts = new List<string>();
        var start = 0;
        foreach (var size in Groups)
        {
            if (start >= password.Length)
            {
                break;
            }

            parts.Add(password.Substring(start, Math.Min(size, password.Length - start)));
            start += size;
        }

        if (start < password.Length)
        {
            parts.Add(password[start..]);
        }

        return string.Join(' ', parts);
    }

    private Dictionary<byte, char> BuildAlphabet()
    {
        var map = new Dictionary<byte, char>();
        if (Alphabet.ValueKind == JsonValueKind.String)
        {
            var text = Alphabet.GetString() ?? string.Empty;
            for (var i = 0; i < text.Length && i < 256; i++)
            {
                map[(byte)i] = text[i];
            }
        }
        else if (Alphabet.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in Alphabet.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (value is { Length: > 0 })
                {
                    map[(byte)HexConverter.ParseNumber(property.Name)] = value[0];
                }
            }
        }

        return map;
    }
}

/// <summary>Reads numbers written as JSON numbers or as "0x…" / "$…" hex strings.</summary>
public sealed class HexConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : ParseNumber(reader.GetString() ?? "0");

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"0x{value:X4}");

    public static int ParseNumber(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (text.StartsWith('$'))
        {
            return int.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return int.Parse(text, CultureInfo.InvariantCulture);
    }
}
