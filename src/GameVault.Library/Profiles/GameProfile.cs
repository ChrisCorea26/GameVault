using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameVault.Library.Profiles;

/// <summary>How a game is played (SPEC §7.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InputType>))]
public enum InputType
{
    Controller,
    Zapper,
    FourScore,

    /// <summary>The Power Pad (Family Trainer) mat in port 2, played on the keyboard (SPEC §4.7).</summary>
    PowerPad,

    /// <summary>The Arkanoid (Vaus) paddle in port 2, turned with the mouse (SPEC §4.7).</summary>
    ArkanoidPaddle,
}

/// <summary>Corrections for a wrong or missing iNES header. Null fields keep the header's value.</summary>
public sealed record HeaderCorrection
{
    public int? Mapper { get; init; }

    public int? Submapper { get; init; }

    /// <summary>"horizontal", "vertical" or "four-screen".</summary>
    public string? Mirroring { get; init; }

    public bool? Battery { get; init; }

    public int? PrgRamSize { get; init; }
}

/// <summary>
/// A per-game profile from <c>data/gamedb/*.json</c>, keyed by headerless SHA-1, or the user's own
/// overrides (Game Properties). Every field is optional; a null field means "no opinion".
/// </summary>
public sealed record GameProfile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string? Sha1 { get; init; }

    public string? Title { get; init; }

    public HeaderCorrection? Header { get; init; }

    public InputType? Input { get; init; }

    /// <summary>True when run-ahead can't break the game (it uses no timing tricks run-ahead disturbs).</summary>
    public bool? RunAheadSafe { get; init; }

    /// <summary>True to recommend drawing more than 8 sprites per line (removes flicker).</summary>
    public bool? RemoveSpriteLimit { get; init; }

    /// <summary>"ntsc", "pal" or "dendy"; Genesis games also take "usa", "japan" and "europe".</summary>
    public string? Region { get; init; }

    /// <summary>Genesis games: "3" for the 3-button pad (a few older games misread the 6-button one), else "6".</summary>
    public string? Pad { get; init; }

    /// <summary>Apple II games: the machine to run on, "ii+", "iie" or "iie-enhanced" (the default for 16 KB ROMs).</summary>
    public string? Model { get; init; }

    /// <summary>File name of the password definition in <c>data/gamedb/passwords/</c> (Phase 6).</summary>
    public string? PasswordDefinition { get; init; }

    /// <summary>N64 games: what the cartridge saves to, "eeprom4k", "eeprom16k", "sram", "flash" or "none" (Phase 16).</summary>
    public string? Save { get; init; }

    /// <summary>Fields set in <paramref name="overrides"/> win over this profile's.</summary>
    public GameProfile Merge(GameProfile? overrides) => overrides is null ? this : this with
    {
        Header = overrides.Header ?? Header,
        Input = overrides.Input ?? Input,
        RunAheadSafe = overrides.RunAheadSafe ?? RunAheadSafe,
        RemoveSpriteLimit = overrides.RemoveSpriteLimit ?? RemoveSpriteLimit,
        Region = overrides.Region ?? Region,
        Model = overrides.Model ?? Model,
        Pad = overrides.Pad ?? Pad,
        PasswordDefinition = overrides.PasswordDefinition ?? PasswordDefinition,
        Save = overrides.Save ?? Save,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GameProfile? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GameProfile>(json, Json);

    internal static IReadOnlyList<GameProfile> ReadFile(string json)
    {
        var trimmed = json.TrimStart();
        return trimmed.StartsWith('[')
            ? JsonSerializer.Deserialize<List<GameProfile>>(json, Json) ?? []
            : [JsonSerializer.Deserialize<GameProfile>(json, Json)!];
    }
}
