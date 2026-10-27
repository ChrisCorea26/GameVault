using GameVault.Core.Atari;
using GameVault.Core.Input;
using GameVault.Core.Systems;
using GameVault.Library.Scanning;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Atari 5200 games (phase 19, milestone 2) through the library's file reader, the host and the input.</summary>
public sealed class Atari5200HostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-a5200-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>An 8K cartridge that sets a background colour and loops; its header skips the BIOS's title screen.</summary>
    private static byte[] Game()
    {
        byte[] program = [0xA9, 0x84, 0x8D, 0x1A, 0xC0, 0x4C, 0x05, 0xA0]; // LDA #$84; STA COLBK; JMP *
        var rom = new byte[8192];
        program.CopyTo(rom, 0);
        rom[0x1FFD] = 0xFF;
        rom[0x1FFE] = 0x00;
        rom[0x1FFF] = 0xA0;
        return rom;
    }

    /// <summary>A stand-in BIOS: reset jumps through the cartridge's start address.</summary>
    private static byte[] Bios()
    {
        var bios = new byte[Atari5200Console.BiosSize];
        byte[] start = [0x6C, 0xFE, 0xBF];
        start.CopyTo(bios, 0);
        bios[0x7FC] = 0x00;
        bios[0x7FD] = 0xF8;
        return bios;
    }

    [Fact]
    public async Task An_a52_file_plays_with_the_players_bios()
    {
        var path = Path.Combine(_root.FullName, "Game.a52");
        await File.WriteAllBytesAsync(path, Game());
        RomFiles.SystemOf(path).Should().Be(GameSystem.Atari5200);
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        host.Load(await File.ReadAllBytesAsync(path), "Game", system: GameSystem.Atari5200, atari5200Bios: Bios());

        host.System.Should().Be(GameSystem.Atari5200);
        input.KeypadMode.Should().BeTrue();
        VideoFrame frame = null!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(frames.TryGetVideoFrame(out frame) && frame.Width == Gtia.Width && frame.Pixels[0] != 0) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        frame.Width.Should().Be(336);
        frame.Height.Should().Be(240);
        frame.Pixels[0].Should().Be(0x84);
        host.Stop();
    }

    [Fact]
    public async Task Without_a_bios_the_player_is_told_where_to_choose_one()
    {
        var path = Path.Combine(_root.FullName, "Game.a52");
        await File.WriteAllBytesAsync(path, Game());
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(path);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Settings → Atari 5200");
    }

    [Fact]
    public void In_keypad_mode_the_number_row_is_the_keypad()
    {
        var input = new InputService(KeyboardMapping.CreateDefault()) { KeypadMode = true };

        input.KeyDown(VirtualKeys.Digit5).Should().BeTrue();
        input.Sample(0).Key().Should().Be(Atari5200Key.Five);
        input.KeyDown(VirtualKeys.Minus);
        input.Sample(0).Key().Should().Be(Atari5200Key.Star, "the last key pressed wins");
        input.KeyUp(VirtualKeys.Minus);
        input.KeyUp(VirtualKeys.Digit5);
        input.Sample(0).Key().Should().Be(Atari5200Key.None);
        (input.Sample(0) & ControllerState.L).Should().Be(ControllerState.None, "1–4 no longer press the shoulders");

        input.KeypadMode = false;
        input.KeyDown(VirtualKeys.Digit1);
        (input.Sample(0) & ControllerState.L).Should().Be(ControllerState.L);
    }
}
