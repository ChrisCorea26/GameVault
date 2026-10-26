using GameVault.Library.Journal;
using GameVault.Library.Maps;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

/// <summary>Automatic maps (SPEC §7.9): visited rooms, the rendered map and its journal image.</summary>
public sealed class MapTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gamevault-maps-");

    public void Dispose() => _root.Delete(recursive: true);

    private static uint[] Tile(uint color) => Enumerable.Repeat(color, MapStore.TileWidth * MapStore.TileHeight).ToArray();

    [Fact]
    public void Rooms_are_recorded_once_per_area()
    {
        var store = new MapStore(_root.FullName);

        store.Visit("sha1:aa", 0, 5, Tile(0xFFFF0000), DateTimeOffset.Now).Should().BeTrue();
        store.Visit("sha1:aa", 0, 5, Tile(0xFF00FF00), DateTimeOffset.Now).Should().BeFalse("only the first visit is kept");
        store.Visit("sha1:aa", 1, 5, Tile(0xFF0000FF), DateTimeOffset.Now).Should().BeTrue("another area");

        var reopened = new MapStore(_root.FullName);
        reopened.Areas("sha1:aa").Should().Equal(0, 1);
        reopened.Rooms("sha1:aa", 0).Should().Equal(5);
        reopened.Tile("sha1:aa", 0, 5)![0].Should().Be(0xFFFF0000);
        reopened.Tile("sha1:aa", 0, 6).Should().BeNull();
    }

    [Fact]
    public void The_map_places_rooms_on_the_grid_and_outlines_the_current_one()
    {
        var store = new MapStore(_root.FullName);
        store.Visit("sha1:aa", 0, 0, Tile(0xFFFF0000), DateTimeOffset.Now);
        store.Visit("sha1:aa", 0, 5, Tile(0xFF0000FF), DateTimeOffset.Now);

        var png = MapService.Render(store, "sha1:aa", 0, columns: 4, currentRoom: 5);

        // 4 columns × 2 rows of 64×60 rooms with 2-pixel gaps.
        ReadSize(png).Should().Be((4 * 66 + 2, 2 * 62 + 2));
        var pixels = PngTestDecoder.Decode(png);
        int At(int room, int x, int y) => (2 + y + room / 4 * 62) * (4 * 66 + 2) + 2 + x + room % 4 * 66;
        pixels[At(0, 30, 30)].Should().Be(0xFFFF0000, "room 0 is visited");
        pixels[At(5, 30, 30)].Should().Be(0xFF0000FF, "room 5 is column 1, row 1");
        pixels[At(5, 0, 0)].Should().Be(0xFFFFD000, "the current room is outlined");
        pixels[At(1, 30, 30)].Should().Be(0xFF202020, "room 1 hasn't been visited");
    }

    [Fact]
    public async Task Rooms_entered_while_playing_become_an_explored_map_in_the_journal()
    {
        var journalStore = new JournalStore(Path.Combine(_root.FullName, "journal"));
        var journal = new JournalService(journalStore);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault())) { MemoryMapResolver = _ => GameWatcherTests.Map() };
        var maps = new MapStore(Path.Combine(_root.FullName, "maps"));
        _ = new MapService(host, maps, journal);

        host.Load(GameWatcherTests.LivesAndRoomsRom(), "Rooms");
        var key = host.GameKey!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (maps.Rooms(key, 0).Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        host.Stop();
        maps.Rooms(key, 0).Count.Should().BeGreaterThanOrEqualTo(3);
        await Task.Delay(200);
        var images = journalStore.Images(key);
        images.Should().ContainSingle("each redraw replaces the previous map").Which.Caption.Should().Be("Explored map");
        images[0].Kind.Should().Be(JournalImageKind.Map);
    }

    private static (int Width, int Height) ReadSize(byte[] png) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
}

/// <summary>Decodes the PNGs <see cref="PngEncoder"/> writes (8-bit RGBA, one IDAT, filter 0) back to ARGB.</summary>
internal static class PngTestDecoder
{
    public static uint[] Decode(byte[] png)
    {
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        using var idat = new MemoryStream();
        var at = 8;
        while (at < png.Length)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IDAT")
            {
                idat.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var bpp = (bytes.Length / height - 1) / width;
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * bpp + 1);
            bytes[row].Should().Be(0, "filter type 0");
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * bpp;
                var a = bpp == 4 ? bytes[p + 3] : (byte)0xFF;
                pixels[y * width + x] = (uint)(a << 24 | bytes[p] << 16 | bytes[p + 1] << 8 | bytes[p + 2]);
            }
        }

        return pixels;
    }
}
