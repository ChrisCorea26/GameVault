using FluentAssertions;
using GameVault.Core.Sega;
using GameVault.Core.Systems;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Master System games in the emulator host (SPEC §7.11, Phase 11).</summary>
public sealed class SegaHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-sega-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>Counts frames in $C000 from the frame interrupt, with Mode 4 on and a red background.</summary>
    internal static byte[] CountingRom()
    {
        var rom = new byte[0x8000];
        Array.Fill(rom, (byte)0xFF);
        rom[0] = 0xC3;
        rom[1] = 0x00;
        rom[2] = 0x01;
        byte[] handler = [0xDB, 0xBF, 0x21, 0x00, 0xC0, 0x34, 0xFB, 0xED, 0x4D];
        handler.CopyTo(rom, 0x38);
        byte[] main =
        [
            0xF3, 0xED, 0x56,
            0x3E, 0x04, 0xD3, 0xBF, 0x3E, 0x80, 0xD3, 0xBF, // reg0 = Mode 4
            0x3E, 0xFF, 0xD3, 0xBF, 0x3E, 0x82, 0xD3, 0xBF, // reg2: name table $3800
            0x3E, 0x60, 0xD3, 0xBF, 0x3E, 0x81, 0xD3, 0xBF, // reg1: display + frame interrupts
            0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0xC0, 0xD3, 0xBF, // CRAM 0
            0x3E, 0x03, 0xD3, 0xBE,                         // red
            0xFB, 0x76, 0x18, 0xFD,
        ];
        main.CopyTo(rom, 0x100);
        return rom;
    }

    [Fact]
    public async Task A_master_system_file_loads_runs_and_restores_states()
    {
        var path = Path.Combine(_root.FullName, "Counter.sms");
        await File.WriteAllBytesAsync(path, CountingRom());
        var frames = new FrameSink();
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()));

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.MasterSystem);
        host.GameKey.Should().Be("sha1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(CountingRom())));
        host.AchievementsHash.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(CountingRom())));
        host.UsesZapper.Should().BeFalse();
        host.DiskSideCount.Should().Be(0);
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0xC000)) > 30);

        frames.TryGetFrame(out var frame).Should().BeTrue();
        SegaVdp.Palette[frame[(24 + 96) * 256 + 128]].Should().Be(0xFFFF0000);

        host.Pause();
        var state = await host.CaptureStateAsync();
        var counted = await host.InspectAsync(c => c.PeekMemory(0xC000));
        host.Resume();
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0xC000)) > counted + 20);
        host.Pause();
        (await host.RestoreAsync(state!)).Should().BeTrue();
        (await host.InspectAsync(c => c.PeekMemory(0xC000))).Should().Be(counted);
    }

    [Fact]
    public async Task Sega_games_report_retroachievements_memory_in_its_layout()
    {
        var path = Path.Combine(_root.FullName, "Counter.sms");
        await File.WriteAllBytesAsync(path, CountingRom());
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        await host.LoadAsync(path);
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0xC000)) > 5);
        host.Pause();

        // RetroAchievements' address 0 is the Master System's $C000.
        var (system, ra) = await host.InspectAsync(c => (c.PeekMemory(0xC000), ((SegaConsole)c).ReadAchievementMemory(0)));
        ra.Should().Be(system);
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
