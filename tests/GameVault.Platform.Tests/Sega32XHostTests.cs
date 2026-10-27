using GameVault.Core.Sega32X;
using GameVault.Core.Systems;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>32X cartridges (sega-cd-32x.md, milestone 2) through the library's file reader and the host.</summary>
public sealed class Sega32XHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-32x-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A 64 KB cartridge with a Genesis header whose 68000 loops; SH-2 boot ROMs that loop.</summary>
    private string Game()
    {
        var rom = new byte[0x10000];
        rom[0] = 0xFF; rom[1] = 0xFF; rom[2] = 0xFE; rom[3] = 0x00;
        rom[6] = 0x02;
        "SEGA 32X        "u8.CopyTo(rom.AsSpan(0x100));
        rom[0x200] = 0x60;
        rom[0x201] = 0xFE;
        var path = Path.Combine(_root.FullName, "Game.32x");
        File.WriteAllBytes(path, rom);
        return path;
    }

    private static Sega32XBios Bios()
    {
        byte[] Boot(int size)
        {
            var boot = new byte[size];
            boot[3] = 0x10;
            boot[0x10] = 0xAF; // BRA *
            boot[0x11] = 0xFE;
            boot[0x12] = 0x00; // NOP
            boot[0x13] = 0x09;
            return boot;
        }

        return new Sega32XBios(new byte[256], Boot(2048), Boot(1024));
    }

    [Fact]
    public void A_32x_file_plays_with_the_players_bios()
    {
        var path = Game();
        RomFiles.SystemOf(path).Should().Be(GameSystem.Sega32X);
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()));

        host.Load(File.ReadAllBytes(path), "Game", system: GameSystem.Sega32X, sega32XBios: Bios());

        host.System.Should().Be(GameSystem.Sega32X);
        VideoFrame frame = null!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!frames.TryGetVideoFrame(out frame) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        frame.Should().NotBeNull();
        host.Stop();
    }

    [Fact]
    public async Task Without_the_bios_files_the_player_is_told_where_to_choose_them()
    {
        var path = Game();
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(path);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Settings → 32X");
    }
}
