using System.Text.Json;

namespace GameVault.Library.Sessions;

/// <summary>One play session: when it started and how long the game actually ran (pauses not counted).</summary>
public sealed record SessionRecord(DateTimeOffset Start, TimeSpan Played);

/// <summary>Recent play sessions per game, <c>&lt;root&gt;/&lt;game&gt;.json</c>, for the "Previously on…" recap (SPEC §7.9).</summary>
public sealed class SessionStore(string root)
{
    public const int Kept = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(string gameKey, SessionRecord session)
    {
        var sessions = List(gameKey).ToList();
        sessions.Add(session);
        Directory.CreateDirectory(root);
        var path = PathFor(gameKey);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(sessions.TakeLast(Kept).ToList(), Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public IReadOnlyList<SessionRecord> List(string gameKey)
    {
        var path = PathFor(gameKey);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public SessionRecord? Last(string gameKey) => List(gameKey).LastOrDefault();

    private string PathFor(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-') + ".json");
}
