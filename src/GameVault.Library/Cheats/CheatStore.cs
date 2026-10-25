using System.Text.Json;
using GameVault.Core.Cheats;

namespace GameVault.Library.Cheats;

/// <summary>A code in the player's list for a game (SPEC §7.8).</summary>
public sealed record SavedCode(string Id, string Code, string Description, bool Enabled)
{
    /// <summary>The parsed code, or codes that work together ("A + B"); checked before saving, so this always succeeds.</summary>
    public IReadOnlyList<CheatCode> Parsed => CheatCode.ParseList(Code);
}

/// <summary>
/// The player's codes, one JSON file per game: <c>&lt;root&gt;/&lt;game&gt;.json</c>
/// (<c>%LOCALAPPDATA%\GameVault\Cheats</c>). Enabled codes are remembered per game.
/// </summary>
public sealed class CheatStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<SavedCode> List(string gameKey) => Read(gameKey);

    /// <summary>Adds a code; the text is normalized ("sxio-po" becomes "SXIOPO"). Throws <see cref="FormatException"/> for bad codes.</summary>
    public SavedCode Add(string gameKey, string code, string description, bool enabled = true)
    {
        var entry = new SavedCode(Guid.NewGuid().ToString("N"), CheatCode.Normalize(code), description.Trim(), enabled);
        Write(gameKey, [.. Read(gameKey), entry]);
        return entry;
    }

    /// <summary>Adds many codes at once (an import). Codes already in the list are skipped.</summary>
    public int AddRange(string gameKey, IEnumerable<(string Code, string Description, bool Enabled)> codes)
    {
        var entries = Read(gameKey).ToList();
        var known = entries.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var (code, description, enabled) in codes)
        {
            var text = CheatCode.Normalize(code);
            if (known.Add(text))
            {
                entries.Add(new SavedCode(Guid.NewGuid().ToString("N"), text, description.Trim(), enabled));
                added++;
            }
        }

        Write(gameKey, entries);
        return added;
    }

    public void Update(string gameKey, SavedCode code) =>
        Write(gameKey, Read(gameKey).Select(e => e.Id == code.Id ? code with { Code = CheatCode.Normalize(code.Code) } : e).ToList());

    public void Delete(string gameKey, string id) => Write(gameKey, Read(gameKey).Where(e => e.Id != id).ToList());

    public void SetAllEnabled(string gameKey, bool enabled) =>
        Write(gameKey, Read(gameKey).Select(e => e with { Enabled = enabled }).ToList());

    /// <summary>The collection game the player chose for <paramref name="gameKey"/>, when titles don't match by themselves.</summary>
    public string? GetCollectionTitle(string gameKey) => ReadTitles().GetValueOrDefault(gameKey);

    public void SetCollectionTitle(string gameKey, string title)
    {
        var titles = ReadTitles();
        titles[gameKey] = title;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "collection-titles.json");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(titles, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private Dictionary<string, string> ReadTitles()
    {
        var path = Path.Combine(root, "collection-titles.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string PathFor(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-') + ".json");

    private List<SavedCode> Read(string gameKey)
    {
        var path = PathFor(gameKey);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            // A code edited by hand into something unreadable is dropped rather than breaking the list.
            return (JsonSerializer.Deserialize<List<SavedCode>>(File.ReadAllText(path), Json) ?? [])
                .Where(e => CheatCode.TryParseList(e.Code, out _, out _))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(string gameKey, List<SavedCode> entries)
    {
        var path = PathFor(gameKey);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(entries, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
