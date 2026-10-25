using System.Text.Json;
using System.Text.Json.Serialization;
using GameVault.Library.Passwords;

namespace GameVault.Library.Memory;

/// <summary>
/// A named value in RAM (SPEC §7.9): 1 or 2 bytes (little-endian), binary or BCD, optionally masked and
/// shifted, plus an offset for display ("lives shown = RAM + 1").
/// </summary>
public sealed record MemoryValue
{
    [JsonConverter(typeof(HexConverter))]
    public required int Address { get; init; }

    public int Size { get; init; } = 1;

    /// <summary>"binary" (default) or "bcd" (each nibble a decimal digit).</summary>
    public string Encoding { get; init; } = "binary";

    [JsonConverter(typeof(HexConverter))]
    public int Mask { get; init; } = 0xFFFF;

    public int Shift { get; init; }

    public int Offset { get; init; }

    /// <summary>Shown as "of N" in overlays (for example the most health there can be).</summary>
    public int? Max { get; init; }

    public int Read(Func<int, byte> readRam)
    {
        var raw = readRam(Address) | (Size >= 2 ? readRam(Address + 1) << 8 : 0);
        raw = (raw & Mask) >> Shift;
        if (Encoding.Equals("bcd", StringComparison.OrdinalIgnoreCase))
        {
            var value = 0;
            var scale = 1;
            for (var digits = Size * 2; digits > 0; digits--, raw >>= 4, scale *= 10)
            {
                value += (raw & 0x0F) * scale;
            }

            raw = value;
        }

        return raw + Offset;
    }
}

/// <summary>
/// A test on RAM. Either a named value from the memory map (<c>{ "value": "health", "op": "&lt;=", "target": 2 }</c>)
/// or a raw byte (<c>{ "address": "0x0010", "mask": "0xFF", "op": "==", "target": 7 }</c>, where <c>value</c>
/// may stand in for <c>target</c> as in password definitions).
/// </summary>
public sealed record MemoryCondition
{
    [JsonConverter(typeof(NullableHexConverter))]
    public int? Address { get; init; }

    [JsonConverter(typeof(HexConverter))]
    public int Mask { get; init; } = 0xFF;

    /// <summary>A value name, or (with <see cref="Address"/>) the number to compare with.</summary>
    public JsonElement Value { get; init; }

    public string Op { get; init; } = "==";

    [JsonConverter(typeof(NullableHexConverter))]
    public int? Target { get; init; }

    public bool Holds(MemoryMap? map, Func<int, byte> readRam)
    {
        int actual, target;
        if (Address is { } address)
        {
            actual = readRam(address) & Mask;
            target = Target ?? (Value.ValueKind switch
            {
                JsonValueKind.Number => Value.GetInt32(),
                JsonValueKind.String => HexConverter.ParseNumber(Value.GetString()!),
                _ => throw new FormatException("A RAM condition needs a target."),
            });
        }
        else
        {
            var name = Value.ValueKind == JsonValueKind.String ? Value.GetString()! : throw new FormatException("A condition needs a value name or an address.");
            if (map?.Values.TryGetValue(name, out var value) != true)
            {
                throw new FormatException($"The memory map has no value \"{name}\".");
            }

            actual = value!.Read(readRam);
            target = Target ?? throw new FormatException($"The condition on \"{name}\" needs a target.");
        }

        return Op switch
        {
            "==" or "=" => actual == target,
            "!=" => actual != target,
            "<" => actual < target,
            "<=" => actual <= target,
            ">" => actual > target,
            ">=" => actual >= target,
            _ => throw new FormatException($"Unknown comparison \"{Op}\"."),
        };
    }

    /// <summary>"health ≤ 2" or "RAM $0010 = 7", for showing a goal to the player.</summary>
    public string Describe()
    {
        var op = Op switch { "==" or "=" => "=", "!=" => "≠", "<=" => "≤", ">=" => "≥", _ => Op };
        if (Address is { } address)
        {
            var target = Target ?? (Value.ValueKind == JsonValueKind.Number ? Value.GetInt32() : HexConverter.ParseNumber(Value.GetString() ?? "0"));
            return $"RAM ${address:X4} {op} {target}";
        }

        return $"{Value.GetString()} {op} {Target}";
    }

    public static MemoryCondition Named(string value, string op, int target) =>
        new() { Value = JsonSerializer.SerializeToElement(value), Op = op, Target = target };

    public static MemoryCondition Raw(int address, string op, int target, int mask = 0xFF) =>
        new() { Address = address, Mask = mask, Op = op, Target = target };
}

/// <summary>Where the explored map's rooms come from.</summary>
public sealed record MapLayout
{
    /// <summary>The value holding the current room or screen number.</summary>
    public required string Room { get; init; }

    /// <summary>The value telling areas apart (overworld, dungeon 1, …); without it there is one area.</summary>
    public string? Area { get; init; }

    /// <summary>Rooms per row: room N sits at column N % Columns, row N / Columns.</summary>
    public int Columns { get; init; } = 16;

    /// <summary>Area number → name ("0": "Overworld").</summary>
    public IReadOnlyDictionary<string, string> AreaNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>One value shown by the stat overlay.</summary>
public sealed record OverlayItem
{
    public required string Value { get; init; }

    public required string Label { get; init; }

    /// <summary>Shown only while these hold (for example while a boss is on screen).</summary>
    public IReadOnlyList<MemoryCondition> When { get; init; } = [];
}

/// <summary>
/// Where a game keeps facts in RAM (SPEC §7.9), documented in docs/memory-maps.md: named values, when real
/// gameplay is on, the lives counter, the room for automatic maps, and the stat overlay. Addresses and names
/// only, never game data.
/// </summary>
public sealed record MemoryMap
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    public string? Sha1 { get; init; }

    public string? Title { get; init; }

    public IReadOnlyDictionary<string, MemoryValue> Values { get; init; } = new Dictionary<string, MemoryValue>();

    /// <summary>All hold during real gameplay (not the title screen, demo or a pause menu).</summary>
    public IReadOnlyList<MemoryCondition> Playing { get; init; } = [];

    /// <summary>The value holding the number of lives.</summary>
    public string? Lives { get; init; }

    public MapLayout? Map { get; init; }

    public IReadOnlyList<OverlayItem> Overlay { get; init; } = [];

    public bool IsPlaying(Func<int, byte> readRam) => Playing.All(c => c.Holds(this, readRam));

    public int Read(string value, Func<int, byte> readRam) => Values[value].Read(readRam);

    /// <summary>Parses and checks that every name used is defined.</summary>
    public static MemoryMap Parse(string json)
    {
        var map = JsonSerializer.Deserialize<MemoryMap>(json, Json) ?? throw new JsonException("Empty memory map.");
        var names = new List<string?> { map.Lives, map.Map?.Room, map.Map?.Area };
        names.AddRange(map.Overlay.Select(o => o.Value));
        names.AddRange(map.Playing.Concat(map.Overlay.SelectMany(o => o.When)).Where(c => c.Address is null).Select(c => c.Value.GetString()));
        foreach (var name in names.Where(n => n is not null))
        {
            if (!map.Values.ContainsKey(name!))
            {
                throw new FormatException($"\"{name}\" is used but not defined under \"values\".");
            }
        }

        return map;
    }
}

/// <summary>The memory maps in <c>data/gamedb/memory</c>, keyed by headerless SHA-1. Broken files are skipped and reported.</summary>
public sealed class MemoryMaps
{
    private readonly Dictionary<string, MemoryMap> _bySha1 = new(StringComparer.OrdinalIgnoreCase);

    public MemoryMaps(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var map = MemoryMap.Parse(File.ReadAllText(file));
                if (map.Sha1 is { Length: 40 } sha1)
                {
                    _bySha1[sha1] = map;
                }
                else
                {
                    Errors.Add($"{Path.GetFileName(file)}: missing \"sha1\".");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException)
            {
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public List<string> Errors { get; } = [];

    public int Count => _bySha1.Count;

    public MemoryMap? For(string? sha1) => sha1 is not null && _bySha1.TryGetValue(sha1, out var map) ? map : null;
}

/// <summary>Like <see cref="HexConverter"/>, for optional numbers.</summary>
public sealed class NullableHexConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => null,
        JsonTokenType.Number => reader.GetInt32(),
        _ => HexConverter.ParseNumber(reader.GetString() ?? "0"),
    };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
