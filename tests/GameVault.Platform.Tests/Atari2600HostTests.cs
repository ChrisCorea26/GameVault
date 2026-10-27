using GameVault.Core.Systems;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Atari 2600 games (phase 19) through the library's file reader and the host.</summary>
public sealed class Atari2600HostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-atari-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A 4K program that runs VSYNC frames forever with a coloured background.</summary>
    private static byte[] Game()
    {
        byte[] program =
        [
            0x78, 0xD8,             // SEI; CLD
            0xA9, 0x02, 0x85, 0x00, // frame: LDA #2; STA VSYNC
            0x85, 0x02, 0x85, 0x02, 0x85, 0x02,
            0xA9, 0x00, 0x85, 0x00, // LDA #0; STA VSYNC
            0xA9, 0x84, 0x85, 0x09, // LDA #$84; STA COLUBK
            0xA2, 0xF0,             // LDX #240
            0x85, 0x02, 0xCA, 0xD0, 0xFB, // line: STA WSYNC; DEX; BNE line
            0x4C, 0x02, 0xF0,       // JMP frame
        ];
        var rom = new byte[4096];
        program.CopyTo(rom, 0);
        rom[0xFFC] = 0x00;
        rom[0xFFD] = 0xF0;
        return rom;
    }

    [Fact]
    public async Task An_a26_file_plays_as_an_atari_2600_game()
    {
        var path = Path.Combine(_root.FullName, "Game.a26");
        await File.WriteAllBytesAsync(path, Game());
        RomFiles.SystemOf(path).Should().Be(GameSystem.Atari2600);
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()));

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.Atari2600);
        VideoFrame frame = null!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(frames.TryGetVideoFrame(out frame) && frame.Width == 160) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        frame.Width.Should().Be(160);
        frame.Height.Should().Be(210);
        frame.PixelAspect.Should().BeGreaterThan(1.5, "the 2600's pixels are wide");
        host.Stop();
    }

    [Fact]
    public async Task A_cartridge_of_an_unknown_size_is_explained()
    {
        var path = Path.Combine(_root.FullName, "Odd.a26");
        await File.WriteAllBytesAsync(path, new byte[5000]);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(path);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Atari 2600");
    }
}
