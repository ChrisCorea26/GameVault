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
