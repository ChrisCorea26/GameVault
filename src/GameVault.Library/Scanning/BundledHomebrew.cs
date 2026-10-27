using System.Text.Json;
using GameVault.Library.Models;

namespace GameVault.Library.Scanning;

/// <summary>One game in <c>data/homebrew/&lt;game&gt;/game.json</c>: its file and the details the library shows.</summary>
public sealed record BundledGame
{
    /// <summary>The game file, next to <c>game.json</c>.</summary>
    public required string File { get; init; }

    public required string Title { get; init; }

    public string? Developer { get; init; }

    public string? Publisher { get; init; }

    public int? Year { get; init; }

    public string? Genre { get; init; }

    public int Players { get; init; } = 1;

    public GameRegion Region { get; init; } = GameRegion.World;

    /// <summary>The license, for people reading the file (the text is in LICENSE beside it).</summary>
    public string? License { get; init; }
}

/// <summary>
/// The freely licensed homebrew GameVault ships (SPEC §6.4), so a new player has games to play on first launch. Each
/// game has its own folder under <c>data/homebrew</c> with the game file, its LICENSE and a <c>game.json</c>.
/// </summary>
public static class BundledHomebrew
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The bundled games with their files' full paths. Folders without a readable game.json are skipped.</summary>
    public static IReadOnlyList<(string Path, BundledGame Game)> Read(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var games = new List<(string, BundledGame)>();
        foreach (var json in Directory.EnumerateFiles(folder, "game.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<BundledGame>(File.ReadAllText(json), Json) is { } game
                    && Path.Combine(Path.GetDirectoryName(json)!, game.File) is var path
                    && File.Exists(path))
                {
                    games.Add((path, game));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A damaged entry shouldn't stop the others.
            }
        }

        return games;
    }

    /// <summary>The library entry with the bundled details filled in, unless the player already named the game.</summary>
    public static GameEntry Describe(GameEntry entry, BundledGame game) =>
        entry.IsCataloged || entry.Title != Path.GetFileNameWithoutExtension(entry.RomPath)
            ? entry
            : entry with
            {
                Title = game.Title,
                Developer = game.Developer,
                Publisher = game.Publisher,
                ReleaseYear = game.Year,
                Genre = game.Genre,
                Players = Math.Clamp(game.Players, 1, 4),
                Region = game.Region,
            };
}
