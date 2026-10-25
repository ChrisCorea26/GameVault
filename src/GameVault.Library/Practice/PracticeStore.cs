using System.Text.Json;

namespace GameVault.Library.Practice;

/// <summary>Personal bests per practice segment, in frames (SPEC §7.6). Stored as one JSON file per game.</summary>
public sealed class PracticeStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Best frames for each segment number (1-based), or empty.</summary>
    public IReadOnlyDictionary<int, long> Bests(string gameKey)
    {
        var path = PathFor(gameKey);
        if (!File.Exists(path))
        {
            return new Dictionary<int, long>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, long>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<int, long>();
        }
    }

    /// <summary>Records a segment time; returns true if it is a new personal best.</summary>
    public bool Record(string gameKey, int segment, long frames)
    {
        var bests = new Dictionary<int, long>(Bests(gameKey));
        if (bests.TryGetValue(segment, out var best) && best <= frames)
        {
            return false;
        }

        bests[segment] = frames;
        var path = PathFor(gameKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(bests, Json));
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }

    public void Reset(string gameKey) => File.Delete(PathFor(gameKey));

    private string PathFor(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-') + ".json");
}
