using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameVault.Library.Timeline;

[JsonConverter(typeof(JsonStringEnumConverter<TimelineKind>))]
public enum TimelineKind
{
    /// <summary>Saved from the timeline or the Save button.</summary>
    Manual,

    /// <summary>Saved with F5.</summary>
    Quick,

    /// <summary>Saved automatically when the game was closed; only the newest is kept.</summary>
    AutoExit,

    /// <summary>A few seconds before a life was lost (SPEC §7.9); the newest five are kept.</summary>
    BeforeDeath,
}

/// <summary>One save state on a game's timeline (SPEC §7.1).</summary>
public sealed record TimelineEntry(
    string Id,
    DateTimeOffset SavedAt,
    TimeSpan Playtime,
    string? Label,
    bool Pinned,
    TimelineKind Kind,
    bool Deleted = false);

/// <summary>
/// Unlimited save states per game: <c>&lt;root&gt;/&lt;game&gt;/&lt;id&gt;.state</c> and <c>.png</c>, listed in
/// <c>index.json</c>. Deleting is soft, so it can be undone; <see cref="PurgeDeleted"/> removes the files
/// (on the next start). Not thread-safe: use from one thread or under the caller's lock.
/// </summary>
public sealed class TimelineStore(string root)
{
    /// <summary>"Before you lost a life" states kept per game (pinned ones aren't counted).</summary>
    public const int MaxBeforeDeath = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<TimelineEntry> List(string gameKey) =>
        ReadIndex(gameKey).Where(e => !e.Deleted).OrderBy(e => e.SavedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

    /// <summary>The newest state, optionally of one kind. F8 loads this.</summary>
    public TimelineEntry? Latest(string gameKey, TimelineKind? kind = null) =>
        List(gameKey).LastOrDefault(e => kind is null || e.Kind == kind);

    public TimelineEntry Add(
        string gameKey,
        ReadOnlySpan<byte> state,
        ReadOnlySpan<byte> thumbnailPng,
        TimeSpan playtime,
        TimelineKind kind,
        DateTimeOffset savedAt,
        string? label = null)
    {
        var entries = ReadIndex(gameKey).ToList();
        if (kind == TimelineKind.AutoExit)
        {
            foreach (var old in entries.Where(e => e.Kind == TimelineKind.AutoExit).ToList())
            {
                entries.Remove(old);
                DeleteFiles(gameKey, old.Id);
            }
        }

        if (kind == TimelineKind.BeforeDeath)
        {
            var older = entries.Where(e => e.Kind == TimelineKind.BeforeDeath && !e.Pinned).ToList();
            foreach (var old in older.Take(Math.Max(0, older.Count - (MaxBeforeDeath - 1))))
            {
                entries.Remove(old);
                DeleteFiles(gameKey, old.Id);
            }
        }

        var entry = new TimelineEntry(Guid.NewGuid().ToString("N"), savedAt, playtime, label, Pinned: false, kind);
        WriteAtomically(ThumbnailPath(gameKey, entry.Id), thumbnailPng);
        WriteAtomically(StatePath(gameKey, entry.Id), state);
        entries.Add(entry);
        WriteIndex(gameKey, entries);
        return entry;
    }

    public byte[]? LoadState(string gameKey, string id)
    {
        var path = StatePath(gameKey, id);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public string ThumbnailPath(string gameKey, string id) => Path.Combine(Folder(gameKey), id + ".png");

    public void Rename(string gameKey, string id, string? label) =>
        Update(gameKey, id, e => e with { Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() });

    public void SetPinned(string gameKey, string id, bool pinned) => Update(gameKey, id, e => e with { Pinned = pinned });

    /// <summary>Hides a state. <see cref="Undelete"/> brings it back until <see cref="PurgeDeleted"/> runs.</summary>
    public void Delete(string gameKey, string id) => Update(gameKey, id, e => e with { Deleted = true });

    public void Undelete(string gameKey, string id) => Update(gameKey, id, e => e with { Deleted = false });

    /// <summary>Removes the files of deleted states for every game.</summary>
    public void PurgeDeleted()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var gameKey = Path.GetFileName(folder);
            var entries = ReadIndexFile(Path.Combine(folder, "index.json"));
            var deleted = entries.Where(e => e.Deleted).ToList();
            if (deleted.Count == 0)
            {
                continue;
            }

            foreach (var entry in deleted)
            {
                DeleteFiles(gameKey, entry.Id, folderName: true);
            }

            WriteIndexFile(Path.Combine(folder, "index.json"), entries.Except(deleted).ToList());
        }
    }

    private void Update(string gameKey, string id, Func<TimelineEntry, TimelineEntry> change)
    {
        var entries = ReadIndex(gameKey).ToList();
        var index = entries.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No save state {id}.");
        }

        entries[index] = change(entries[index]);
        WriteIndex(gameKey, entries);
    }

    private string StatePath(string gameKey, string id) => Path.Combine(Folder(gameKey), id + ".state");

    /// <summary>"sha1:abc…" becomes the folder "sha1-abc…" (colons aren't allowed in Windows file names).</summary>
    private string Folder(string gameKey) => Path.Combine(root, FolderName(gameKey));

    private static string FolderName(string gameKey) => gameKey.Replace(':', '-');

    private IReadOnlyList<TimelineEntry> ReadIndex(string gameKey) => ReadIndexFile(Path.Combine(Folder(gameKey), "index.json"));

    private void WriteIndex(string gameKey, List<TimelineEntry> entries) => WriteIndexFile(Path.Combine(Folder(gameKey), "index.json"), entries);

    private static IReadOnlyList<TimelineEntry> ReadIndexFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TimelineEntry>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            return []; // a damaged index hides states rather than crashing; the files stay on disk
        }
    }

    private static void WriteIndexFile(string path, List<TimelineEntry> entries) =>
        WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(entries, Json));

    private void DeleteFiles(string gameKey, string id, bool folderName = false)
    {
        var folder = Path.Combine(root, folderName ? gameKey : FolderName(gameKey));
        File.Delete(Path.Combine(folder, id + ".state"));
        File.Delete(Path.Combine(folder, id + ".png"));
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data.ToArray());
        File.Move(temp, path, overwrite: true);
    }
}
