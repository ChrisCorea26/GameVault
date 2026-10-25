using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using GameVault.Core.Cheats;

namespace GameVault.Library.Cheats;

/// <summary>A code from an imported collection, with where the collection says it came from.</summary>
public sealed record CollectionCode(string Code, string Description, string Source);

/// <summary>A game in a code collection: its codes and the notes that go with them.</summary>
public sealed record CollectionGame(string Title, IReadOnlyList<CollectionCode> Codes, IReadOnlyList<string> Notes);

/// <summary>How an import went.</summary>
public sealed record CollectionImport(string Name, int Games, int Codes, int Skipped, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// Code collections the player supplies (SPEC §7.8), such as a spreadsheet of published Game Genie codes.
/// Like ROMs and the Disk System BIOS, collections come from the player and stay in their own
/// <c>%LOCALAPPDATA%\GameVault\Cheats\Collections</c>; GameVault never includes them.
/// <list type="bullet">
/// <item>An .xlsx workbook: the sheet named "NES" (or the first sheet), with a header row holding "Game",
/// a code column ("Key in…" or "Code") and an effect column ("Effect…" or "Description"); a "Code Source"
/// column is optional.</item>
/// <item>A .csv file with the same headers.</item>
/// </list>
/// Rows whose code isn't valid become notes for that game ("Codes 8 and 9 work in 2-player mode only").
/// </summary>
public sealed class CodeCollections
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _folder;
    private Dictionary<string, List<(string Collection, CollectionGame Game)>>? _byKey;

    public CodeCollections(string folder) => _folder = folder;

    /// <summary>The imported collections by name, with their game and code counts.</summary>
    public IReadOnlyList<(string Name, int Games, int Codes)> List()
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(_folder, "*.json").Order(StringComparer.Ordinal)
            .Select(Load)
            .Where(c => c is not null)
            .Select(c => (c!.Name, c.Games.Count, c.Games.Sum(g => g.Codes.Count)))
            .ToList();
    }

    /// <summary>The games in every collection whose title matches <paramref name="title"/>.</summary>
    public IReadOnlyList<(string Collection, CollectionGame Game)> Find(string? title)
    {
        var index = Index();
        return index.TryGetValue(GameTitles.Key(title), out var games) ? games : [];
    }

    /// <summary>Titles across all collections containing <paramref name="text"/>, for choosing a game by hand.</summary>
    public IReadOnlyList<string> Search(string? text, int max = 20)
    {
        var key = GameTitles.Key(text);
        if (key.Length == 0)
        {
            return [];
        }

        return Index().Values.SelectMany(g => g).Select(g => g.Game.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => GameTitles.Key(t).Contains(key, StringComparison.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public void Remove(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _byKey = null;
    }

    /// <summary>Reads a collection file and saves it under its file name. Nothing is saved if it can't be read.</summary>
    public CollectionImport Import(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        List<string[]> rows;
        try
        {
            rows = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ReadCsv(File.ReadAllText(path)) : ReadXlsx(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return new CollectionImport(name, 0, 0, 0, ["GameVault couldn't read that file. " + ex.Message]);
        }

        var header = rows.FindIndex(r => r.Any(c => c.Equals("Game", StringComparison.OrdinalIgnoreCase)));
        if (header < 0)
        {
            return new CollectionImport(name, 0, 0, 0, ["The file needs a header row with Game, Code (or \"Key in…\") and Effect (or Description) columns."]);
        }

        var columns = rows[header];
        int Column(params string[] names) =>
            Array.FindIndex(columns, c => names.Any(n => c.Trim().TrimEnd('…', '.').Equals(n, StringComparison.OrdinalIgnoreCase)));
        var gameColumn = Column("Game");
        var codeColumn = Column("Key in", "Code", "Codes");
        var effectColumn = Column("Effect", "Description", "Effects");
        var sourceColumn = Column("Code Source", "Source");
        if (codeColumn < 0 || effectColumn < 0)
        {
            return new CollectionImport(name, 0, 0, 0, ["The file needs Code (or \"Key in…\") and Effect (or Description) columns."]);
        }

        var games = new Dictionary<string, (string Title, List<CollectionCode> Codes, List<string> Notes)>(StringComparer.Ordinal);
        var order = new List<string>();
        var skipped = 0;
        string Cell(string[] row, int column) => column >= 0 && column < row.Length ? row[column].Trim() : string.Empty;
        foreach (var row in rows.Skip(header + 1))
        {
            var title = Cell(row, gameColumn);
            var code = Cell(row, codeColumn);
            if (title.Length == 0 || code.Length == 0)
            {
                continue;
            }

            if (!games.TryGetValue(GameTitles.Key(title), out var game))
            {
                game = (title, [], []);
                games[GameTitles.Key(title)] = game;
                order.Add(GameTitles.Key(title));
            }

            if (CheatCode.TryParseList(code, out _, out _))
            {
                game.Codes.Add(new CollectionCode(CheatCode.Normalize(code), Cell(row, effectColumn), Cell(row, sourceColumn)));
            }
            else
            {
                game.Notes.Add(code);
                skipped++;
            }
        }

        var saved = new SavedCollection(name, order.Select(k => new CollectionGame(games[k].Title, games[k].Codes, games[k].Notes)).Where(g => g.Codes.Count > 0).ToList());
        if (saved.Games.Count == 0)
        {
            return new CollectionImport(name, 0, 0, skipped, ["No NES Game Genie or RAM codes were found in that file."]);
        }

        Directory.CreateDirectory(_folder);
        var target = PathFor(name);
        File.WriteAllBytes(target + ".tmp", JsonSerializer.SerializeToUtf8Bytes(saved, Json));
        File.Move(target + ".tmp", target, overwrite: true);
        _byKey = null;
        return new CollectionImport(name, saved.Games.Count, saved.Games.Sum(g => g.Codes.Count), skipped, []);
    }

    private string PathFor(string name) =>
        Path.Combine(_folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    private SavedCollection? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedCollection>(File.ReadAllBytes(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private Dictionary<string, List<(string Collection, CollectionGame Game)>> Index()
    {
        if (_byKey is { } cached)
        {
            return cached;
        }

        var index = new Dictionary<string, List<(string, CollectionGame)>>(StringComparer.Ordinal);
        if (Directory.Exists(_folder))
        {
            foreach (var collection in Directory.EnumerateFiles(_folder, "*.json").Order(StringComparer.Ordinal).Select(Load))
            {
                foreach (var game in collection?.Games ?? [])
                {
                    var key = GameTitles.Key(game.Title);
                    if (!index.TryGetValue(key, out var list))
                    {
                        index[key] = list = [];
                    }

                    list.Add((collection!.Name, game));
                }
            }
        }

        return _byKey = index;
    }

    // ------------------------------------------------------------------ file formats

    /// <summary>
    /// The cells of one sheet of an .xlsx workbook (Office Open XML: a zip of XML parts), as text. Picks the
    /// sheet named "NES", else the first.
    /// </summary>
    internal static List<string[]> ReadXlsx(string path)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        using var zip = ZipFile.OpenRead(path);
        XDocument Part(string name) =>
            XDocument.Load((zip.GetEntry(name) ?? throw new InvalidDataException($"The workbook has no {name}.")).Open());

        var strings = zip.GetEntry("xl/sharedStrings.xml") is null
            ? []
            : Part("xl/sharedStrings.xml").Root!.Elements(main + "si").Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList();

        var sheets = Part("xl/workbook.xml").Root!.Element(main + "sheets")!.Elements(main + "sheet").ToList();
        var sheet = sheets.FirstOrDefault(s => string.Equals((string?)s.Attribute("name"), "NES", StringComparison.OrdinalIgnoreCase)) ?? sheets.First();
        var id = (string?)sheet.Attribute(rel + "id");
        var target = Part("xl/_rels/workbook.xml.rels").Root!.Elements(pkg + "Relationship")
            .First(r => (string?)r.Attribute("Id") == id).Attribute("Target")!.Value.TrimStart('/');
        if (!target.StartsWith("xl/", StringComparison.Ordinal))
        {
            target = "xl/" + target;
        }

        var rows = new List<string[]>();
        foreach (var row in Part(target).Root!.Element(main + "sheetData")!.Elements(main + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(main + "c"))
            {
                var index = ColumnIndex((string?)cell.Attribute("r"), cells.Count);
                while (cells.Count < index)
                {
                    cells.Add(string.Empty);
                }

                var type = (string?)cell.Attribute("t");
                var value = (string?)cell.Element(main + "v") ?? string.Empty;
                cells.Add(type switch
                {
                    "s" when int.TryParse(value, out var s) && s < strings.Count => strings[s],
                    "inlineStr" => string.Concat(cell.Descendants(main + "t").Select(t => t.Value)),
                    _ => value,
                });
            }

            rows.Add([.. cells]);
        }

        return rows;
    }

    /// <summary>"C12" → 2 (zero-based column).</summary>
    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return fallback;
        }

        var column = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter))
        {
            column = column * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return column - 1;
    }

    /// <summary>RFC 4180 CSV: quoted fields may hold commas, quotes ("") and line breaks.</summary>
    internal static List<string[]> ReadCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }

    private sealed record SavedCollection(string Name, List<CollectionGame> Games);
}
