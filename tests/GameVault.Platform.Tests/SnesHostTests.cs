using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.Systems;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Super Nintendo games in the emulator host and the library (SPEC §7.11, Phase 14).</summary>
public sealed class SnesHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-snes-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A LoROM that turns the screen on with a green backdrop and counts NMIs in $10.</summary>
    internal static byte[] CountingRom(byte chip = 0)
    {
        var rom = new byte[0x10000];
        byte[] program =
        [
            0x18, 0xFB,                         // CLC; XCE
            0xA9, 0x00, 0x8D, 0x21, 0x21,       // CGADD 0
            0xA9, 0xE0, 0x8D, 0x22, 0x21,       // green: $03E0
            0xA9, 0x03, 0x8D, 0x22, 0x21,
            0xA9, 0x0F, 0x8D, 0x00, 0x21,       // full brightness
            0xA9, 0x80, 0x8D, 0x00, 0x42,       // NMI on
            0x80, 0xFE,                         // BRA *
        ];
        program.CopyTo(rom, 0);
        byte[] nmi = [0xE6, 0x10, 0x40];       // INC $10; RTI
        nmi.CopyTo(rom, 0x1000);
        "GAMEVAULT TEST       "u8.CopyTo(rom.AsSpan(0x7FC0));
        rom[0x7FD5] = 0x20;
        rom[0x7FD6] = chip;
        rom[0x7FD9] = 0x01;
        rom[0x7FDC] = 0xFF;
        rom[0x7FDD] = 0xFF;
        rom[0x7FEA] = 0x00; // native NMI vector $9000
        rom[0x7FEB] = 0x90;
        rom[0x7FFC] = 0x00; // reset vector $8000
        rom[0x7FFD] = 0x80;
        return rom;
    }

    [Fact]
    public async Task A_snes_file_loads_and_runs_with_the_snes_pad_layout()
    {
        var path = Path.Combine(_root.FullName, "Counter.sfc");
        await File.WriteAllBytesAsync(path, CountingRom());
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.Snes);
        input.Layout.Should().Be(PadLayout.Snes);
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x10)) > 20);
        uint color = 0;
        await WaitFor(() =>
        {
            if (frames.TryGetVideoFrame(out var frame) && frame.Width == 256 && frame.Height == 224)
            {
                color = frames.Palette[frame.Span[100]];
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        });
        color.Should().Be(0xFF00FF00u, "a green backdrop");

        // The keyboard's turbo-B key (A) is the SNES pad's Y.
        input.KeyDown(VirtualKeys.A);
        input.Sample(0).Should().Be(ControllerState.Y);
        input.KeyUp(VirtualKeys.A);

        host.Stop();
        input.Layout.Should().Be(PadLayout.Nes);
    }

    [Fact]
    public async Task Games_with_enhancement_chips_get_a_clear_message()
    {
        var path = Path.Combine(_root.FullName, "Chip.sfc");
        await File.WriteAllBytesAsync(path, CountingRom(chip: 0x34));
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(path);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("SA-1");
    }

    [Fact]
    public void The_library_finds_snes_files_and_headered_copies_hash_like_plain_ones()
    {
        var rom = CountingRom();
        var headered = new byte[512].Concat(rom).ToArray();
        RomFiles.SystemOf("Game.sfc").Should().Be(GameSystem.Snes);
        RomFiles.SystemOf("Game.smc").Should().Be(GameSystem.Snes);
        RomHasher.Hash(headered, GameSystem.Snes).Should().Be(RomHasher.Hash(rom, GameSystem.Snes));
        AchievementsHashing.ForSystem(headered, GameSystem.Snes).Should().Be(AchievementsHashing.ForSystem(rom, GameSystem.Snes));
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
