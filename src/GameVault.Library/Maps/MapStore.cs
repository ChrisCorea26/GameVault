using System.Text.Json;

namespace GameVault.Library.Maps;

/// <summary>
/// The rooms a player has visited, per game and area, for automatic maps (SPEC §7.9):
/// <c>&lt;root&gt;/&lt;game&gt;/map.json</c> plus a small picture of each room from the first visit
/// (<c>&lt;area&gt;-&lt;room&gt;.bin</c>, <see cref="TileWidth"/>×<see cref="TileHeight"/> ARGB pixels).
/// </summary>
public sealed class MapStore(string root)
{
    public const int TileWidth = 64;
    public const int TileHeight = 60;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Records a visit. Returns true the first time a room is seen.</summary>
    public bool Visit(string gameKey, int area, int room, ReadOnlySpan<uint> tile, DateTimeOffset now)
    {
        var index = Read(gameKey);
        var rooms = index.Areas.TryGetValue(area, out var list) ? list : index.Areas[area] = [];
        if (rooms.ContainsKey(room))
        {
            return false;
        }

        rooms[room] = now;
        var folder = Folder(gameKey);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{area}-{room}.bin"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(tile).ToArray());
        Write(gameKey, index);
        return true;
    }

    public IReadOnlyList<int> Areas(string gameKey) => Read(gameKey).Areas.Keys.Order().ToList();

    public IReadOnlyCollection<int> Rooms(string gameKey, int area) =>
        Read(gameKey).Areas.TryGetValue(area, out var rooms) ? rooms.Keys : [];

    /// <summary>A room's picture, or null if it is missing.</summary>
    public uint[]? Tile(string gameKey, int area, int room)
    {
        var path = Path.Combine(Folder(gameKey), $"{area}-{room}.bin");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return bytes.Length == TileWidth * TileHeight * 4 ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray() : null;
    }

    /// <summary>The journal image showing this area's map, so a new render can replace it.</summary>
    public string? JournalImage(string gameKey, int area) => Read(gameKey).JournalImages.GetValueOrDefault(area);

    public void SetJournalImage(string gameKey, int area, string imageId)
    {
        var index = Read(gameKey);
        index.JournalImages[area] = imageId;
        Write(gameKey, index);
    }

    private string Folder(string gameKey) => Path.Combine(root, gameKey.Replace(':', '-'));

    private Index Read(string gameKey)
    {
        var path = Path.Combine(Folder(gameKey), "map.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? new Index() : new Index();
        }
        catch (JsonException)
        {
            return new Index();
        }
    }

    private void Write(string gameKey, Index index)
    {
        var path = Path.Combine(Folder(gameKey), "map.json");
        Directory.CreateDirectory(Folder(gameKey));
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(index, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed class Index
    {
        public Dictionary<int, Dictionary<int, DateTimeOffset>> Areas { get; set; } = [];

        public Dictionary<int, string> JournalImages { get; set; } = [];
    }
}
