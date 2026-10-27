using GameVault.Library.Journal;
using GameVault.Library.Maps;
using GameVault.Platform.Emulation;
using GameVault.Platform.Video;

namespace GameVault.Platform.Storage;

/// <summary>
/// Automatic maps (SPEC §7.9): records the rooms the player enters and keeps an "Explored map" image per
/// area in the game's journal, redrawn whenever a new room is found.
/// </summary>
public sealed class MapService
{
    private const int Gap = 2;
    private const uint Unvisited = 0xFF202020;
    private const uint Background = 0xFF000000;
    private const uint Current = 0xFFFFD000;

    private readonly EmulatorHost _host;
    private readonly MapStore _maps;
    private readonly JournalService _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MapService(EmulatorHost host, MapStore maps, JournalService journal)
    {
        _host = host;
        _maps = maps;
        _journal = journal;
        _host.RoomEntered += async (_, room) => await OnRoomEnteredAsync(room);
    }

    private async Task OnRoomEnteredAsync(RoomEnteredEventArgs room)
    {
        if (room.GameKey is not { } key || _host.MemoryMap?.Map is not { } layout)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (!_maps.Visit(key, room.Area, room.Room, room.Tile, DateTimeOffset.Now))
                {
                    return;
                }

                var png = Render(_maps, key, room.Area, layout.Columns, room.Room);
                var name = layout.AreaNames.GetValueOrDefault(room.Area.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ?? (layout.Area is null ? null : $"Area {room.Area}");
                var caption = name is null ? "Explored map" : $"Explored map: {name}";
                var imageId = _journal.ReplaceImage(key, _maps.JournalImage(key, room.Area), JournalImageKind.Map, png, caption);
                _maps.SetJournalImage(key, room.Area, imageId);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A map that can't be saved this time is redrawn on the next new room. On Windows a map file another
            // process holds (or one being deleted) fails with UnauthorizedAccessException, which must not crash the
            // app from this event handler.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Draws an area's map as a PNG: each room at column room % columns, row room / columns, visited rooms as
    /// their picture, the rest dark, and the current room outlined.
    /// </summary>
    public static byte[] Render(MapStore maps, string gameKey, int area, int columns, int? currentRoom)
    {
        var rooms = maps.Rooms(gameKey, area);
        columns = Math.Max(1, columns);
        var rows = rooms.Count == 0 ? 1 : rooms.Max() / columns + 1;
        var cellWidth = MapStore.TileWidth + Gap;
        var cellHeight = MapStore.TileHeight + Gap;
        var width = columns * cellWidth + Gap;
        var height = rows * cellHeight + Gap;
        var pixels = new uint[width * height];
        Array.Fill(pixels, Background);
        for (var room = 0; room < columns * rows; room++)
        {
            var left = Gap + room % columns * cellWidth;
            var top = Gap + room / columns * cellHeight;
            var tile = rooms.Contains(room) ? maps.Tile(gameKey, area, room) : null;
            for (var y = 0; y < MapStore.TileHeight; y++)
            {
                for (var x = 0; x < MapStore.TileWidth; x++)
                {
                    var edge = x == 0 || y == 0 || x == MapStore.TileWidth - 1 || y == MapStore.TileHeight - 1;
                    pixels[(top + y) * width + left + x] = room == currentRoom && edge
                        ? Current
                        : tile?[y * MapStore.TileWidth + x] ?? Unvisited;
                }
            }
        }

        return PngEncoder.Encode(pixels, width, height);
    }
}
