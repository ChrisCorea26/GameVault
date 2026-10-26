using System.Text.RegularExpressions;
using System.Xml;
using GameVault.Library.Data;
using GameVault.Library.Models;

namespace GameVault.Library.Catalog;

/// <summary>One game as read from a DAT file.</summary>
public sealed record CatalogGame(
    string Title,
    GameRegion Region,
    bool IsLicensed,
    string Crc32,
    string? Sha1,
    string? Publisher = null,
    string? Developer = null,
    int? Year = null,
    string? Genre = null,
    int Players = 1,
    GameVault.Core.Systems.GameSystem System = GameVault.Core.Systems.GameSystem.Nes);

public sealed record CatalogImportResult(string Source, int Games, int Skipped);

/// <summary>
/// Imports Logiqx XML DAT files, the format No-Intro publishes (SPEC §6.1). The user supplies the DAT;
/// GameVault ships only this importer. Importing the same DAT again replaces its earlier entries.
/// </summary>
public sealed partial class CatalogImporter(LibraryDatabase database)
{
    public CatalogImportResult Import(Stream dat, string sourceName)
    {
        var (games, skipped) = Parse(dat);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        LibraryDatabase.Execute(connection, "DELETE FROM catalog WHERE source = $source;", ("$source", sourceName));

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO catalog (key, title, region, publisher, developer, year, genre, players, crc32, sha1, licensed, source, system)
            VALUES ($key, $title, $region, $publisher, $developer, $year, $genre, $players, $crc32, $sha1, $licensed, $source, $system);
            """;
        var parameters = new[] { "$key", "$title", "$region", "$publisher", "$developer", "$year", "$genre", "$players", "$crc32", "$sha1", "$licensed", "$source", "$system" }
            .Select(name => insert.Parameters.Add(name, Microsoft.Data.Sqlite.SqliteType.Text))
            .ToArray();
        insert.Prepare();

        foreach (var game in games)
        {
            object?[] values =
            [
                KeyFor(game), game.Title, (int)game.Region, game.Publisher, game.Developer, game.Year, game.Genre,
                game.Players, game.Crc32, game.Sha1, game.IsLicensed ? 1 : 0, sourceName, (int)game.System,
            ];
            for (var i = 0; i < values.Length; i++)
            {
                parameters[i].Value = values[i] ?? DBNull.Value;
            }

            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new CatalogImportResult(sourceName, games.Count, skipped);
    }

    private static GameVault.Core.Systems.GameSystem SystemOfDat(string datName) =>
        datName.Contains("Master System", StringComparison.OrdinalIgnoreCase) || datName.Contains("Mark III", StringComparison.OrdinalIgnoreCase)
            ? GameVault.Core.Systems.GameSystem.MasterSystem
            : datName.Contains("SG-1000", StringComparison.OrdinalIgnoreCase) || datName.Contains("SC-3000", StringComparison.OrdinalIgnoreCase)
                ? GameVault.Core.Systems.GameSystem.Sg1000
                : datName.Contains("Apple II", StringComparison.OrdinalIgnoreCase)
                    ? GameVault.Core.Systems.GameSystem.AppleII
                    : datName.Contains("Mega Drive", StringComparison.OrdinalIgnoreCase) || datName.Contains("Genesis", StringComparison.OrdinalIgnoreCase)
                        ? GameVault.Core.Systems.GameSystem.Genesis
                        : datName.Contains("Super Nintendo", StringComparison.OrdinalIgnoreCase) || datName.Contains("Super Famicom", StringComparison.OrdinalIgnoreCase)
                            ? GameVault.Core.Systems.GameSystem.Snes
                            : GameVault.Core.Systems.GameSystem.Nes;

    public static string KeyFor(CatalogGame game) => game.Sha1 is { } sha1 ? "sha1:" + sha1 : "crc:" + game.Crc32;

    /// <summary>Reads a DAT without touching the database (the import wizard's preview).</summary>
    public static (IReadOnlyList<CatalogGame> Games, int Skipped) Parse(Stream dat)
    {
        var games = new List<CatalogGame>();
        var skipped = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
        using var reader = XmlReader.Create(dat, settings);
        string? name = null;
        string? publisher = null, developer = null, genre = null;
        int? year = null;
        var players = 1;
        string? crc = null, sha1 = null;
        var depth = -1;
        var datSystem = GameVault.Core.Systems.GameSystem.Nes;
        GameVault.Core.Systems.GameSystem? romSystem = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name is "game" or "machine")
            {
                name = reader.GetAttribute("name");
                publisher = developer = genre = crc = sha1 = null;
                romSystem = null;
                year = null;
                players = 1;
                depth = reader.Depth;
                if (reader.IsEmptyElement)
                {
                    skipped++;
                    name = null;
                }

                continue;
            }

            if (name is null)
            {
                // The DAT header names the system ("Sega - Master System - Mark III", "Sega - SG-1000").
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "name" && reader.Depth == 2)
                {
                    datSystem = SystemOfDat(reader.ReadElementContentAsString());
                }

                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "rom" when crc is null:
                        crc = reader.GetAttribute("crc")?.ToLowerInvariant();
                        sha1 = reader.GetAttribute("sha1")?.ToLowerInvariant();
                        romSystem = reader.GetAttribute("name") is { } romName ? GameVault.Library.Scanning.RomFiles.SystemOf(romName) : null;
                        break;
                    case "publisher" or "manufacturer":
                        publisher = reader.ReadElementContentAsString().Trim();
                        break;
                    case "developer":
                        developer = reader.ReadElementContentAsString().Trim();
                        break;
                    case "year":
                        year = int.TryParse(reader.ReadElementContentAsString(), out var y) ? y : null;
                        break;
                    case "genre":
                        genre = reader.ReadElementContentAsString().Trim();
                        break;
                    case "players":
                        players = int.TryParse(reader.ReadElementContentAsString(), out var p) ? Math.Max(1, p) : 1;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth && reader.Name is "game" or "machine")
            {
                if (crc is { Length: 8 })
                {
                    games.Add(new CatalogGame(
                        CleanTitle(name), RegionOf(name), IsLicensed(name), crc, sha1, publisher, developer, year, genre, players, romSystem ?? datSystem));
                }
                else
                {
                    skipped++;
                }

                name = null;
            }
        }

        return (games, skipped);
    }

    /// <summary>"Quest Saga (USA) (Rev 1)" → "Quest Saga".</summary>
    public static string CleanTitle(string name)
    {
        var index = name.IndexOf(" (", StringComparison.Ordinal);
        return (index > 0 ? name[..index] : name).Trim();
    }

    public static GameRegion RegionOf(string name)
    {
        var tags = Tags(name);
        if (tags.Any(t => t.Contains("World")))
        {
            return GameRegion.World;
        }

        if (tags.Any(t => t.Contains("USA") || t.Contains("Canada")))
        {
            return GameRegion.NorthAmerica;
        }

        if (tags.Any(t => t.Contains("Japan")))
        {
            return GameRegion.Japan;
        }

        return tags.Any(t => t.Contains("Europe") || t.Contains("Germany") || t.Contains("France") || t.Contains("Spain")
                             || t.Contains("Italy") || t.Contains("Australia") || t.Contains("Sweden") || t.Contains("United Kingdom"))
            ? GameRegion.Europe
            : GameRegion.Unknown;
    }

    /// <summary>Unlicensed, pirate, prototype, beta, demo and homebrew releases aren't "licensed".</summary>
    public static bool IsLicensed(string name) =>
        !Tags(name).Any(t => t is "Unl" or "Pirate" or "Proto" or "Beta" or "Demo" or "Sample" or "Homebrew" || t.StartsWith("Proto ", StringComparison.Ordinal) || t.StartsWith("Beta ", StringComparison.Ordinal));

    private static IEnumerable<string> Tags(string name) => TagPattern().Matches(name).Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex TagPattern();
}
