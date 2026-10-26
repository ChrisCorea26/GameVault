using GameVault.Core.Tests.Cartridge;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

public sealed class SaveStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultTests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SaveStorageTests()
    {
        _paths = new AppPaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Battery_saves_are_found_by_hash_even_after_a_rename()
    {
        var store = new BatterySaveStore(_paths);
        store.Save("Quest: Part 1?", 0xABCDEF, [1, 2, 3]);

        var path = store.PathFor("Renamed Game", 0xABCDEF);
        Path.GetFileName(path).Should().Be("Quest_ Part 1_ - 0000000000abcdef.sav");
        Path.GetDirectoryName(path).Should().Be(_paths.SavesDirectory);

        var ram = new byte[8];
        store.TryLoad(0xABCDEF, ram).Should().BeTrue();
        ram.Should().Equal(1, 2, 3, 0, 0, 0, 0, 0);
        store.TryLoad(0x1234, ram).Should().BeFalse();
        Directory.GetFiles(_paths.SavesDirectory, "*.tmp").Should().BeEmpty("writes go through a temp file and a replace");
    }

    [Fact]
    public void Save_state_slots_store_state_and_thumbnail()
    {
        var store = new SaveStateStore(_paths);

        store.Load(7, 3).Should().BeNull();
        store.SavedAt(7, 3).Should().BeNull();
        store.Save(7, 3, [9, 9], [0x89]);

        store.Load(7, 3).Should().Equal(9, 9);
        store.SavedAt(7, 3).Should().NotBeNull();
        File.Exists(store.ThumbnailPath(7, 3)).Should().BeTrue();
        var act = () => store.Save(7, 10, [], []);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Host_restores_battery_ram_and_writes_it_back_when_stopped()
    {
        var rom = BankedRom.Build(4, 16, 128, battery: true);
        var store = new BatterySaveStore(_paths);
        var hash = Core.NesConsole.FromBytes(rom).RomHash;
        var saved = new byte[0x2000];
        saved[0x1FFF] = 0x5A;
        store.Save("Battery Game", hash, saved);

        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), paths: _paths))
        {
            host.Load(rom, "Battery Game");
            (await host.InspectNesAsync(c => c.Bus.Peek(0x7FFF))).Should().Be(0x5A);
            await Task.Delay(100); // the test program writes its counter into PRG-RAM
            host.Stop();
        }

        var ram = new byte[0x2000];
        store.TryLoad(hash, ram).Should().BeTrue();
        ram.Take(0x100).Should().Contain(b => b != 0, "the game's writes were saved");
    }

    [Fact]
    public async Task Timeline_service_round_trips_through_the_host()
    {
        var rom = BankedRom.Build(4, 16, 128);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        var service = new TimelineService(host, new GameVault.Library.Timeline.TimelineStore(_paths.StatesDirectory));
        var notices = new List<string>();
        service.Notice += (_, n) => notices.Add(n);
        host.Load(rom, "Game");
        await Task.Delay(50);
        var key = host.GameKey!;

        await service.SaveAsync("Before the boss");
        var savedFrame = await host.InspectNesAsync(c => c.Ppu.FrameCount);
        var items = await service.GetAsync(key);
        items.Should().ContainSingle().Which.Label.Should().Be("Before the boss");
        File.Exists(items[0].ThumbnailPath).Should().BeTrue();

        // Let the game run past the save, then pause so no frame can run between loading and checking:
        // the result doesn't depend on how busy the machine is.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await host.InspectNesAsync(c => c.Ppu.FrameCount) <= savedFrame + 5 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        host.Pause();
        var frameBeforeLoad = await host.InspectNesAsync(c => c.Ppu.FrameCount);
        frameBeforeLoad.Should().BeGreaterThan(savedFrame);
        (await service.LoadAsync(key, items[0].Id)).Should().BeTrue();
        var frameAfterLoad = await host.InspectNesAsync(c => c.Ppu.FrameCount);
        frameAfterLoad.Should().BeLessThanOrEqualTo(savedFrame, "loading went back to the saved frame");
        host.Resume();
        (await service.LoadAsync("sha1:other", items[0].Id)).Should().BeFalse("it isn't the running game");

        await service.SaveOnExitAsync();
        (await service.GetResumePointAsync(key)).Should().NotBeNull();
        notices.Should().Equal("Saved", "Loaded");
    }

    [Fact]
    public async Task Phase_4_slots_are_imported_into_the_timeline_once()
    {
        var rom = BankedRom.Build(0, 4, 8);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        host.Load(rom, "Game");
        await Task.Delay(50);
        var slots = new SaveStateStore(_paths);
        slots.Save(host.RomHash!.Value, 2, (await host.CaptureAsync())!.State, [0x89]);
        var service = new TimelineService(host, new GameVault.Library.Timeline.TimelineStore(_paths.StatesDirectory), slots);

        var items = await service.GetAsync(host.GameKey!);

        items.Should().ContainSingle().Which.Label.Should().Be("Slot 3");
        slots.Load(host.RomHash!.Value, 2).Should().BeNull("the slot file moved to the timeline");
        (await service.GetAsync(host.GameKey!)).Should().ContainSingle();
    }
}
