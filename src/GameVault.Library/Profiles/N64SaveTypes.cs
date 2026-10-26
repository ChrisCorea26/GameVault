using System.Text.Json;
using GameVault.Core.Nintendo64;

namespace GameVault.Library.Profiles;

/// <summary>
/// What N64 cartridges save to, by the game's two-letter id from its header (the middle of the game code, so "SM"
/// for NSME), from <c>data/gamedb/n64/saves.json</c> (Phase 16). Cartridges don't say which chip they carry, so
/// emulators keep such a list. A broken file is reported and leaves the list empty.
/// </summary>
public sealed class N64SaveTypes
{
    private readonly Dictionary<string, N64SaveType> _byId = new(StringComparer.Ordinal);

    public N64SaveTypes(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Name.Length == 2 && entry.Value.ValueKind == JsonValueKind.String && Parse(entry.Value.GetString()) is { } type)
                {
                    _byId[entry.Name] = type;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Error = $"{Path.GetFileName(path)}: {ex.Message}";
        }
    }

    public int Count => _byId.Count;

    public string? Error { get; }

    /// <summary>The save type for a game code such as "NSME", or null when the list doesn't have it.</summary>
    public N64SaveType? For(string gameCode) =>
        gameCode.Length >= 3 && _byId.TryGetValue(gameCode.Substring(1, 2), out var type) ? type : null;

    /// <summary>"eeprom4k", "eeprom16k", "sram", "flash" or "none" (a profile's <c>save</c> field, too).</summary>
    public static N64SaveType? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "eeprom4k" or "eeprom" => N64SaveType.Eeprom4K,
        "eeprom16k" => N64SaveType.Eeprom16K,
        "sram" => N64SaveType.Sram,
        "flash" or "flashram" => N64SaveType.FlashRam,
        "none" => N64SaveType.None,
        _ => null,
    };
}
