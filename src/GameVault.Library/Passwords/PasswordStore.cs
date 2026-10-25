using System.Text.Json;

namespace GameVault.Library.Passwords;

/// <summary>A captured password (SPEC §7.3).</summary>
public sealed record PasswordEntry(string Id, string Password, DateTimeOffset SavedAt, bool Automatic, string? Note);

/// <summary>Captured passwords per game: <c>&lt;root&gt;/&lt;game&gt;/index.json</c> plus a PNG per password.</summary>
public sealed class PasswordStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<PasswordEntry> List(string gameKey) => Read(gameKey).OrderByDescending(e => e.SavedAt).ToList();

    public PasswordEntry Add(string gameKey, string password, ReadOnlySpan<byte> screenshotPng, bool automatic, DateTimeOffset savedAt, string? note = null)
    {
        var entries = Read(gameKey).ToList();
        var entry = new PasswordEntry(Guid.NewGuid().ToString("N"), password.Trim(), savedAt, automatic, note);
        var folder = Folder(gameKey);
        Directory.CreateDirectory(folder);
        if (!screenshotPng.IsEmpty)
        {
            File.WriteAllBytes(ScreenshotPath(gameKey, entry.Id), screenshotPng.ToArray());
        }

        entries.Add(entry);
        Write(gameKey, entries);
        return entry;
    }

    /// <summary>True when the newest saved password for the game is already this one (avoids duplicates).</summary>
    public bool IsLatest(string gameKey, string password) =>
        List(gameKey).FirstOrDefault()?.Password == password.Trim();

    public void Delete(string gameKey, string id)
    {
        Write(gameKey, Read(gameKey).Where(e => e.Id != id).ToList());
        File.Delete(ScreenshotPath(gameKey, id));
    }

    public string ScreenshotPath(string gameKey, string id) => Path.Combine(Folder(gameKey), id + ".png");

    private string Folder(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-'));

    private IReadOnlyList<PasswordEntry> Read(string gameKey)
    {
        var path = Path.Combine(Folder(gameKey), "index.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PasswordEntry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(string gameKey, List<PasswordEntry> entries)
    {
        var path = Path.Combine(Folder(gameKey), "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(entries, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
