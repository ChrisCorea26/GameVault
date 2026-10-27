using FluentAssertions;
using GameVault.Core.Apple;
using GameVault.Core.Systems;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Apple II disks in the emulator host (SPEC §7.11, Phase 12), with test ROMs written here.</summary>
public sealed class AppleHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-apple-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A 12 KB "ROM" whose program copies the keyboard latch to $0400 forever (LDA $C000; STA $0400; JMP).</summary>
    private string WriteRoms()
    {
        var rom = new byte[0x3000];
        Array.Fill(rom, (byte)0xEA);
        byte[] program = [0xAD, 0x00, 0xC0, 0x8D, 0x00, 0x04, 0x4C, 0x00, 0xD0];
        program.CopyTo(rom, 0);
        rom[^4] = 0x00;
        rom[^3] = 0xD0;
        var path = Path.Combine(_root.FullName, "test.rom");
        File.WriteAllBytes(path, rom);
        return path;
    }

    [Fact]
    public async Task A_disk_needs_the_players_roms()
    {
        var disk = Path.Combine(_root.FullName, "Game.dsk");
        await File.WriteAllBytesAsync(disk, new byte[AppleDisk.DiskBytes]);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), new InMemorySettingsStore());

        var act = () => host.LoadAsync(disk);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Settings → Apple II");
    }

    [Fact]
    public async Task Without_the_disk_boot_rom_the_player_is_told_where_to_choose_it()
    {
        var disk = Path.Combine(_root.FullName, "Game.dsk");
        await File.WriteAllBytesAsync(disk, new byte[AppleDisk.DiskBytes]);
        var settings = new InMemorySettingsStore(new AppSettings { AppleIIRomPath = WriteRoms() });
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings);
        var notices = new List<string>();
        host.Notice += (_, notice) => notices.Add(notice);

        await host.LoadAsync(disk);

        notices.Should().Contain(EmulatorHost.MissingDiskRomNotice);
        EmulatorHost.MissingDiskRomNotice.Should().Contain("Settings → Apple II");
    }

    [Fact]
    public async Task A_disk_runs_with_the_roms_and_the_keyboard_types_into_it()
    {
        var disk = Path.Combine(_root.FullName, "Game.dsk");
        await File.WriteAllBytesAsync(disk, new byte[AppleDisk.DiskBytes]);
        var settings = new InMemorySettingsStore(new AppSettings { AppleIIRomPath = WriteRoms() });
        var input = new InputService(KeyboardMapping.CreateDefault());
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, input, settings);

        await host.LoadAsync(disk);

        host.System.Should().Be(GameSystem.AppleII);
        input.TypingMode.Should().BeTrue();
        input.KeyDown(0x47); // G
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x0400)) == 0xC7);

        await WaitFor(() => Task.FromResult(frames.TryGetVideoFrame(out var frame) && frame.Width == 560 && frame.Height == 384));

        host.Stop();
        input.TypingMode.Should().BeFalse();
    }

    [Fact]
    public async Task The_monitor_setting_reaches_the_machine_and_a_wrong_character_rom_is_explained()
    {
        var disk = Path.Combine(_root.FullName, "Game.dsk");
        await File.WriteAllBytesAsync(disk, new byte[AppleDisk.DiskBytes]);
        var roms = WriteRoms();
        var settings = new InMemorySettingsStore(new AppSettings { AppleIIRomPath = roms, AppleIIMonitor = (int)AppleMonitor.Green });
        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings))
        {
            await host.LoadAsync(disk);
            (await host.InspectAsync(c => ((AppleConsole)c).Monitor)).Should().Be(AppleMonitor.Green);
        }

        var notAFont = Path.Combine(_root.FullName, "font.rom");
        await File.WriteAllBytesAsync(notAFont, new byte[2048]);
        var bad = new InMemorySettingsStore(new AppSettings { AppleIIRomPath = roms, AppleIICharacterRomPath = notAFont });
        using var badHost = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), bad);

        var act = () => badHost.LoadAsync(disk);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("character ROM");
    }

    [Fact]
    public async Task The_disk_menu_fills_and_empties_drive_2_and_writes_are_kept_for_the_next_session()
    {
        var game = Path.Combine(_root.FullName, "Game.dsk");
        var second = Path.Combine(_root.FullName, "Side 2.dsk");
        await File.WriteAllBytesAsync(game, new byte[AppleDisk.DiskBytes]);
        await File.WriteAllBytesAsync(second, new byte[AppleDisk.DiskBytes]);
        var settings = new InMemorySettingsStore(new AppSettings { AppleIIRomPath = WriteRoms() });
        var paths = new GameVault.Platform.Storage.AppPaths(Path.Combine(_root.FullName, "data"));

        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings, paths: paths))
        {
            await host.LoadAsync(game);
            await WaitFor(() => Task.FromResult(host.AppleDriveNames[0] == "Game"));
            host.AppleDriveNames[1].Should().BeNull();

            await host.InsertAppleDiskAsync(1, second);
            await WaitFor(() => Task.FromResult(host.AppleDriveNames[1] == "Side 2"));

            // The computer writes a byte to drive 2, as DOS would: select drive 2, motor on, write mode.
            await host.InspectAsync(c =>
            {
                var disk = ((AppleConsole)c).Disk;
                disk.Access(0xB, false, 0, 0);
                disk.Access(0x9, false, 0, 0);
                disk.Access(0xD, false, 0, 0);
                disk.Access(0xF, true, 0xD5, 0);
                disk.Access(0xE, false, 0, 32);
                return disk.DiskIn(1)!.IsModified;
            });

            host.EjectAppleDisk(1);
            await WaitFor(() => Task.FromResult(host.AppleDriveNames[1] is null));
            host.Stop();
        }

        // Ejecting saves the disk's writes on a background task.
        await WaitFor(() => Task.FromResult(
            Directory.Exists(paths.SavesDirectory) && Directory.EnumerateFiles(paths.SavesDirectory, "Side 2 - *.a2save").Any()));
        Directory.EnumerateFiles(paths.SavesDirectory, "Side 2 - *.a2save").Should().ContainSingle("ejecting saves the disk's writes");

        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings, paths: paths))
        {
            await host.LoadAsync(game);
            await host.InsertAppleDiskAsync(1, second);
            await WaitFor(() => Task.FromResult(host.AppleDriveNames[1] == "Side 2"));
            var firstBits = await host.InspectAsync(c => ((AppleConsole)c).Disk.DiskIn(1)!.TrackData[0].Bits.Take(4).ToArray());
            var plain = AppleDisk.FromSectors(new byte[AppleDisk.DiskBytes], proDosOrder: false).TrackData[0].Bits.Take(4).ToArray();
            firstBits.Should().NotEqual(plain, "the saved write is back on the disk");
            host.Stop();
        }
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }
}
