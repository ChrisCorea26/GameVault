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
            (await host.InspectAsync(c => c.Bus.Peek(0x7FFF))).Should().Be(0x5A);
            await Task.Delay(100); // the test program writes its counter into PRG-RAM
            host.Stop();
        }

        var ram = new byte[0x2000];
        store.TryLoad(hash, ram).Should().BeTrue();
        ram.Take(0x100).Should().Contain(b => b != 0, "the game's writes were saved");
    }

    [Fact]
    public async Task Save_state_service_round_trips_through_the_host()
    {
        var rom = BankedRom.Build(4, 16, 128);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        var service = new SaveStateService(host, new SaveStateStore(_paths));
        var notices = new List<string>();
        service.Notice += (_, n) => notices.Add(n);
        host.Load(rom, "Game");
        await Task.Delay(50);

        await service.SaveAsync(2);
        var savedFrame = await host.InspectAsync(c => c.Ppu.FrameCount);
        await Task.Delay(100);
        (await service.LoadAsync(2)).Should().BeTrue();
        var frameAfterLoad = await host.InspectAsync(c => c.Ppu.FrameCount);

        frameAfterLoad.Should().BeLessThan(savedFrame + 3, "loading went back in time");
        (await service.LoadAsync(5)).Should().BeFalse();
        notices.Should().Equal("Saved to slot 3", "Loaded slot 3", "Slot 6 is empty");
        var slots = await service.GetSlotsAsync();
        slots[2].IsEmpty.Should().BeFalse();
        File.Exists(slots[2].ThumbnailPath).Should().BeTrue();
    }
}
