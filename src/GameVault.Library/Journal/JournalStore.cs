using System.Text.Json;

namespace GameVault.Library.Journal;

public enum JournalImageKind
{
    Screenshot,
    Map,
}

/// <summary>A note in a game's journal (SPEC §7.4). The text is rich text (RTF) stored in its own file.</summary>
public sealed record JournalNoteInfo(string Id, string Title, DateTimeOffset Updated, IReadOnlyList<string> Attachments);

public sealed record JournalImageInfo(string Id, JournalImageKind Kind, DateTimeOffset Created, string? Caption);

/// <summary>
/// The game journal: notes (<c>notes/&lt;id&gt;.rtf</c>), screenshots taken with F12 and maps the player
/// imports (<c>images/&lt;id&gt;.png|jpg</c>), indexed in <c>journal.json</c> per game.
/// </summary>
public sealed class JournalStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<JournalNoteInfo> Notes(string gameKey) => Read(gameKey).Notes.OrderByDescending(n => n.Updated).ToList();

    public IReadOnlyList<JournalImageInfo> Images(string gameKey) => Read(gameKey).Images.OrderByDescending(i => i.Created).ToList();

    public JournalNoteInfo CreateNote(string gameKey, string title, DateTimeOffset now)
    {
        var index = Read(gameKey);
        var note = new JournalNoteInfo(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(title) ? "Untitled note" : title.Trim(), now, []);
        Write(gameKey, index with { Notes = [.. index.Notes, note] });
        File.WriteAllText(NotePath(gameKey, note.Id), string.Empty);
        return note;
    }

    public string LoadNote(string gameKey, string id)
    {
        var path = NotePath(gameKey, id);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public void SaveNote(string gameKey, string id, string title, string rtf, DateTimeOffset now)
    {
        var index = Read(gameKey);
        var note = index.Notes.FirstOrDefault(n => n.Id == id) ?? throw new KeyNotFoundException($"No note {id}.");
        var path = NotePath(gameKey, id);
        File.WriteAllText(path + ".tmp", rtf);
        File.Move(path + ".tmp", path, overwrite: true);
        Write(gameKey, index with
        {
            Notes = index.Notes.Select(n => n.Id == id ? note with { Title = string.IsNullOrWhiteSpace(title) ? note.Title : title.Trim(), Updated = now } : n).ToList(),
        });
    }

    public void DeleteNote(string gameKey, string id)
    {
        var index = Read(gameKey);
        Write(gameKey, index with { Notes = index.Notes.Where(n => n.Id != id).ToList() });
        File.Delete(NotePath(gameKey, id));
    }

    /// <summary>Adds a screenshot (PNG bytes) or an imported map file.</summary>
    public JournalImageInfo AddImage(string gameKey, JournalImageKind kind, ReadOnlySpan<byte> data, string extension, DateTimeOffset now, string? caption = null)
    {
        var index = Read(gameKey);
        var image = new JournalImageInfo(Guid.NewGuid().ToString("N") + NormalizeExtension(extension), kind, now, caption);
        var path = ImagePath(gameKey, image.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data.ToArray());
        Write(gameKey, index with { Images = [.. index.Images, image] });
        return image;
    }

    public void DeleteImage(string gameKey, string id)
    {
        var index = Read(gameKey);
        Write(gameKey, index with
        {
            Images = index.Images.Where(i => i.Id != id).ToList(),
            Notes = index.Notes.Select(n => n with { Attachments = n.Attachments.Where(a => a != id).ToList() }).ToList(),
        });
        File.Delete(ImagePath(gameKey, id));
    }

    /// <summary>Attaches an image to a note ("one click", SPEC §7.4). Attaching twice is harmless.</summary>
    public void Attach(string gameKey, string noteId, string imageId)
    {
        var index = Read(gameKey);
        Write(gameKey, index with
        {
            Notes = index.Notes.Select(n => n.Id == noteId && !n.Attachments.Contains(imageId)
                ? n with { Attachments = [.. n.Attachments, imageId] }
                : n).ToList(),
        });
    }

    public string ImagePath(string gameKey, string imageId) => Path.Combine(Folder(gameKey), "images", imageId);

    private string NotePath(string gameKey, string id)
    {
        var path = Path.Combine(Folder(gameKey), "notes", id + ".rtf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string NormalizeExtension(string extension)
    {
        var e = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return e is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" ? e : ".png";
    }

    private string Folder(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-'));

    private Index Read(string gameKey)
    {
        var path = Path.Combine(Folder(gameKey), "journal.json");
        if (!File.Exists(path))
        {
            return new Index([], []);
        }

        try
        {
            return JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? new Index([], []);
        }
        catch (JsonException)
        {
            return new Index([], []);
        }
    }

    private void Write(string gameKey, Index index)
    {
        var path = Path.Combine(Folder(gameKey), "journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(index, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed record Index(IReadOnlyList<JournalNoteInfo> Notes, IReadOnlyList<JournalImageInfo> Images);
}
