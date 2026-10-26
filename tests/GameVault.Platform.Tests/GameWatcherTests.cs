using GameVault.Core;
using GameVault.Library.Memory;
using GameVault.Library.Timeline;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Game-aware features (SPEC §7.9): the memory-map watcher, before-death states and F2.</summary>
public sealed class GameWatcherTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gamevault-watch-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>
    /// NROM. Counts frames in $00 (polling vblank). Every 256 frames a life is lost ($10, from 3); every 64
    /// frames the room ($40) goes up. $50 = 1 means "playing".
    /// </summary>
    internal static byte[] LivesAndRoomsRom()
    {
        byte[] program =
        [
            0x78, 0xD8, 0xA2, 0xFF, 0x9A,       // C000 SEI; CLD; LDX #$FF; TXS
            0xA9, 0x03, 0x85, 0x10,             // C005 LDA #3; STA $10 (lives)
            0xA9, 0x01, 0x85, 0x50,             // C009 LDA #1; STA $50 (playing)
            0xA9, 0x00, 0x85, 0x40,             // C00D LDA #0; STA $40 (room)
            0x85, 0x00,                         // C011 STA $00
            0x2C, 0x02, 0x20,                   // C013 BIT $2002
            0x10, 0xFB,                         // C016 BPL $C013
            0xE6, 0x00,                         // C018 INC $00
            0xD0, 0x02,                         // C01A BNE $C01E
            0xC6, 0x10,                         // C01C DEC $10 (every 256 frames)
            0xA5, 0x00,                         // C01E LDA $00
            0x29, 0x3F,                         // C020 AND #$3F
            0xD0, 0x02,                         // C022 BNE $C026
            0xE6, 0x40,                         // C024 INC $40 (every 64 frames)
            0x4C, 0x13, 0xC0,                   // C026 JMP $C013
        ];
        var image = new byte[16 + 0x4000 + 0x2000];
        "NES\u001A"u8.CopyTo(image);
        image[4] = 1;
        image[5] = 1;
        program.CopyTo(image, 16);
        image[16 + 0x3FFC] = 0x00;
        image[16 + 0x3FFD] = 0xC0;
        return image;
    }

    internal static MemoryMap Map() => MemoryMap.Parse("""
        {
          "values": { "lives": { "address": "0x10" }, "room": { "address": "0x40" }, "mode": { "address": "0x50" } },
          "playing": [ { "value": "mode", "op": "==", "target": 1 } ],
          "lives": "lives",
          "map": { "room": "room", "columns": 4 },
          "overlay": [ { "value": "lives", "label": "Lives" } ]
        }
        """);

    private static NesConsole Console()
    {
        var console = NesConsole.FromBytes(LivesAndRoomsRom());
        console.Power();
        return console;
    }

    [Fact]
    public void Lives_lost_rooms_entered_and_overlay_values_are_reported()
    {
        var console = Console();
        var watcher = new GameWatcher { Map = Map() };
        var deaths = new List<int>();
        var rooms = new List<int>();
        var overlays = new List<string>();
        // The ROM polls $2002 for vblank, which misses some frames, so run until two lives are gone.
        for (var frame = 0; frame < 3000 && deaths.Count < 2; frame++)
        {
            console.RunFrame();
            var result = watcher.OnFrame(console);
            if (result.BeforeDeathState is { } state)
            {
                var past = NesConsole.FromBytes(LivesAndRoomsRom());
                past.LoadSnapshot(state);
                deaths.Add(past.Bus.Peek(0x10));
                result.BeforeDeathFrame.Should().HaveCount(256 * 240);
            }

            if (result.NewRoom is { } room)
            {
                rooms.Add(room.Room);
            }

            if (result.Overlay is { } values)
            {
                overlays.Add(values.Single().Text);
            }
        }

        deaths.Should().Equal([3, 2], "each saved state is from before that life was lost");
        rooms.Should().StartWith([1, 2, 3, 4]);
        overlays.Should().Equal("3", "2");
        rooms.Count.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public void Nothing_counts_outside_play_or_right_after_a_jump_in_time()
    {
        var console = Console();
        var watcher = new GameWatcher { Map = Map() };
        for (var frame = 0; frame < 30; frame++)
        {
            console.RunFrame();
            watcher.OnFrame(console);
        }

        // A load or rewind lands on fewer lives: not a death.
        console.Bus.PokeRam(0x10, 1);
        watcher.Resync();
        watcher.OnFrame(console).BeforeDeathState.Should().BeNull();

        // Lives dropping on the title screen (not playing) don't count either.
        console.Bus.PokeRam(0x50, 0);
        console.Bus.PokeRam(0x10, 0);
        watcher.OnFrame(console).BeforeDeathState.Should().BeNull();

        new GameWatcher().OnFrame(console).IsEmpty.Should().BeTrue("no memory map, no watching");
    }

    [Fact]
    public void Only_the_newest_five_before_death_states_are_kept()
    {
        var store = new TimelineStore(_root.FullName);
        for (var i = 0; i < 7; i++)
        {
            store.Add("sha1:aa", [(byte)i], [], TimeSpan.Zero, TimelineKind.BeforeDeath, DateTimeOffset.Now.AddMinutes(i));
        }

        store.Add("sha1:aa", [9], [], TimeSpan.Zero, TimelineKind.Manual, DateTimeOffset.Now);

        var entries = store.List("sha1:aa");
        entries.Count(e => e.Kind == TimelineKind.BeforeDeath).Should().Be(TimelineStore.MaxBeforeDeath);
        store.LoadState("sha1:aa", store.Latest("sha1:aa", TimelineKind.BeforeDeath)!.Id).Should().Equal(6);
    }

    [Fact]
    public async Task The_host_saves_before_death_states_and_F2_goes_back()
    {
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(new FrameSink(), input) { MemoryMapResolver = _ => Map() };
        var timeline = new TimelineService(host, new TimelineStore(_root.FullName));
        var lost = 0;
        timeline.Changed += (_, _) => lost++;
        var stats = new System.Collections.Concurrent.ConcurrentQueue<string>();
        host.StatValuesChanged += (_, _) => stats.Enqueue(host.StatValues.Single().Text);

        host.Load(LivesAndRoomsRom(), "Lives");
        host.HasMemoryMap.Should().BeTrue();
        await WaitFor(() => lost >= 1, 30);

        var key = host.GameKey!;
        var items = await timeline.GetAsync(key);
        items.Should().Contain(i => i.Kind == TimelineItemKind.BeforeDeath);
        // The overlay is refreshed a few times a second, so its "2" can trail the life-lost save.
        await WaitFor(() => stats.Contains("2"), 30);

        host.Pause();
        // On a slow machine more lives can be lost before F2; the newest before-death state has one more life
        // than the game has now. Pausing in the notice handler stops the game from losing another one after it.
        var livesNow = await host.InspectNesAsync(c => c.Bus.Peek(0x10));
        var loaded = 0;
        timeline.Notice += (_, n) =>
        {
            if (n == "Loaded")
            {
                host.Pause();
                Interlocked.Exchange(ref loaded, 1);
            }
        };
        input.KeyDown(VirtualKeys.F2);
        host.Resume();
        await WaitFor(() => Volatile.Read(ref loaded) == 1, 30);
        input.KeyUp(VirtualKeys.F2);
        (await host.InspectNesAsync(c => c.Bus.Peek(0x10))).Should().Be((byte)(livesNow + 1), "back to before the life was lost");
    }

    private static async Task WaitFor(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }
}
