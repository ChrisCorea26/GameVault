using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.PlayStation;
using GameVault.Core.Systems;
using GameVault.Core.Tests.PlayStation;
using GameVault.Library.Scanning;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>PlayStation games in the emulator host (SPEC §7.11, Phase 15), with programs, discs and a stand-in BIOS written here.</summary>
public sealed class PlayStationHostTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-psx-");

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A PS-EXE at $80010000 that turns the display on, fills it green and counts in RAM at $100 forever.</summary>
    private static byte[] CountingExe()
    {
        uint[] program =
        [
            0x3C041F80, // lui a0, $1F80
            0x3C050300, // lui a1, $0300: GP1 display on
            0xAC851814, // sw a1, $1814(a0)
            0x3C050800, // lui a1, $0800: GP1 display mode 256×240
            0xAC851814,
            0x3C050200, // lui a1, $0200
            0x34A5FF00, // ori a1, a1, $FF00: GP0 fill with green ($0200FF00)
            0xAC851810, // sw a1, $1810(a0)
            0xAC801810, // sw zero: at (0, 0)
            0x3C0500F0, // lui a1, $00F0: 240 lines
            0x34A50100, // ori a1, $0100: 256 wide
            0xAC851810,
            0x3C068000, // lui a2, $8000
            0x8CC70100, // loop: lw a3, $100(a2)
            0x00000000, // nop
            0x24E70001, // addiu a3, a3, 1
            0x1000FFFC, // b loop
            0xACC70100, // sw a3, $100(a2) (delay slot)
        ];
        var exe = new byte[PsExe.HeaderSize + program.Length * 4];
        "PS-X EXE"u8.CopyTo(exe);
        BitConverter.GetBytes(0x80010000u).CopyTo(exe, 0x10);
        BitConverter.GetBytes(0x80010000u).CopyTo(exe, 0x18);
        BitConverter.GetBytes((uint)(program.Length * 4)).CopyTo(exe, 0x1C);
        for (var i = 0; i < program.Length; i++)
        {
            BitConverter.GetBytes(program[i]).CopyTo(exe, PsExe.HeaderSize + i * 4);
        }

        return exe;
    }

    /// <summary>A stand-in BIOS whose reset code jumps to the shell entry, where fast boot takes over.</summary>
    private static byte[] TinyBios()
    {
        var bios = new byte[PlayStationConsole.BiosSize];
        BitConverter.GetBytes(0x3C088003u).CopyTo(bios, 0); // lui t0, $8003
        BitConverter.GetBytes(0x01000008u).CopyTo(bios, 4); // jr t0
        "System ROM Version 4.1 12/16/97 A"u8.CopyTo(bios.AsSpan(0x7FF00));
        return bios;
    }

    /// <summary>Writes a disc as .bin and .cue files and returns the .cue's path.</summary>
    private string WriteDisc(string name, byte[] exe)
    {
        var disc = TestDisc.Create(exe);
        var raw = new byte[disc.Tracks[0].Length * ICdImage.SectorSize];
        for (var i = 0; i < disc.Tracks[0].Length; i++)
        {
            disc.ReadSector(150 + i, raw.AsSpan(i * ICdImage.SectorSize, ICdImage.SectorSize));
        }

        File.WriteAllBytes(Path.Combine(_root.FullName, name + ".bin"), raw);
        var cue = Path.Combine(_root.FullName, name + ".cue");
        File.WriteAllText(cue, $"FILE \"{name}.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        return cue;
    }

    private async Task<EmulatorHost> HostWithBios(InputService input, bool fastBoot = true, AppPaths? paths = null)
    {
        var bios = Path.Combine(_root.FullName, "bios.bin");
        await File.WriteAllBytesAsync(bios, TinyBios());
        var settings = new InMemorySettingsStore(new AppSettings { PlayStationBiosPath = bios, PlayStationFastBoot = fastBoot });
        return new EmulatorHost(new FrameSink(), input, settings, paths: paths);
    }

    [Fact]
    public async Task A_ps_exe_runs_without_a_bios_with_the_playstation_pad_layout()
    {
        var path = Path.Combine(_root.FullName, "Counter.exe");
        await File.WriteAllBytesAsync(path, CountingExe());
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.PlayStation);
        input.Layout.Should().Be(PadLayout.PlayStation);
        await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x101)) > 0);
        await WaitFor(() => Task.FromResult(frames.TryGetVideoFrame(out var frame) && frame.Width == 256 && frame.Height == 240));

        // Square and Triangle on the turbo keys, the shoulders on the number row.
        input.KeyDown(VirtualKeys.A);
        input.KeyDown(VirtualKeys.Digit3);
        input.Sample(0).Should().Be(ControllerState.Y | ControllerState.L2);
        input.KeyUp(VirtualKeys.A);
        input.KeyUp(VirtualKeys.Digit3);

        host.Stop();
        input.Layout.Should().Be(PadLayout.Nes);
    }

    [Fact]
    public async Task At_2x_the_gpu_renderer_draws_the_picture_on_windows_and_the_software_gpu_elsewhere()
    {
        var path = Path.Combine(_root.FullName, "Counter.exe");
        await File.WriteAllBytesAsync(path, CountingExe());
        var frames = new FrameSink();
        var settings = new InMemorySettingsStore(new AppSettings { PlayStationInternalResolution = 2 });
        using var host = new EmulatorHost(frames, new InputService(KeyboardMapping.CreateDefault()), settings);

        await host.LoadAsync(path);

        // Direct3D only exists on Windows (CI's Windows runner has WARP); elsewhere the software picture stays.
        var (width, height) = OperatingSystem.IsWindows() ? (512, 480) : (256, 240);
        var green = 0u;
        await WaitFor(() => Task.FromResult(frames.TryGetVideoFrame(out var frame) && frame.Width == width && frame.Height == height
            && (green = Argb(frame, frames)[(width * height) / 2]) != 0xFF000000));
        ((green >> 8) & 0xFF).Should().BeGreaterThan(200, "the screen was filled green");
        ((green >> 16) & 0xFF).Should().BeLessThan(20);
        host.Stop();
    }

    private static uint[] Argb(VideoFrame frame, FrameSink frames)
    {
        var argb = new uint[frame.Width * frame.Height];
        frame.ToArgb(frames.Palette, argb);
        return argb;
    }

    [Fact]
    public async Task A_disc_without_a_bios_gets_a_clear_message()
    {
        var cue = WriteDisc("Game", CountingExe());
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));

        var act = () => host.LoadAsync(cue);

        (await act.Should().ThrowAsync<GameLoadException>()).Which.Message.Should().Contain("Settings → PlayStation");
    }

    [Fact]
    public async Task A_disc_boots_through_the_bios_and_its_memory_card_is_saved()
    {
        var cue = WriteDisc("Game", CountingExe());
        var paths = new AppPaths(Path.Combine(_root.FullName, "data"));
        var input = new InputService(KeyboardMapping.CreateDefault());
        using (var host = await HostWithBios(input, paths: paths))
        {
            await host.LoadAsync(cue);
            await WaitFor(async () => await host.InspectAsync(c => c.PeekMemory(0x101)) > 0);
            await host.InspectAsync(c => ((PlayStationConsole)c).Battery!.BatteryRam[0x2000] = 0x42);
            host.Stop();
        }

        var card = Directory.GetFiles(paths.SavesDirectory, "*.mcd").Should().ContainSingle().Which;
        File.ReadAllBytes(card)[0x2000].Should().Be(0x42);
        new FileInfo(card).Length.Should().Be(PsMemoryCard.Size, "a raw memory card image, as other emulators read");
    }

    [Fact]
    public async Task Multi_disc_games_change_discs_from_the_playlist()
    {
        WriteDisc("Disc A", CountingExe());
        WriteDisc("Disc B", CountingExe());
        var m3u = Path.Combine(_root.FullName, "Game.m3u");
        await File.WriteAllTextAsync(m3u, "Disc A.cue\nDisc B.cue\n");
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = await HostWithBios(input);
        var changes = 0;
        host.DiskChanged += (_, _) => changes++;

        await host.LoadAsync(m3u);

        host.DiskSideCount.Should().Be(2);
        host.DiscNames.Should().Equal("Disc A", "Disc B");
        host.InsertedDiskSide.Should().Be(0);
        host.InsertDiskSide(1);
        await WaitFor(() => Task.FromResult(host.InsertedDiskSide == 1));
        host.EjectDisk();
        await WaitFor(() => Task.FromResult(host.InsertedDiskSide is null));
        (await host.InspectAsync(c => ((PlayStationConsole)c).CdRom.ShellOpen)).Should().BeTrue();
    }

    [Fact]
    public void Discs_are_found_and_hashed_for_retroachievements_as_rcheevos_does()
    {
        var exe = CountingExe();
        var cue = WriteDisc("Game", exe);
        var rom = RomFiles.Read(cue)!;

        rom.System.Should().Be(GameSystem.PlayStation);

        // MD5 of the boot file's name, then the executable (its header size field + the 2 KB header).
        using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
        md5.AppendData(System.Text.Encoding.ASCII.GetBytes(TestDisc.BootName));
        md5.AppendData(exe);
        AchievementsHashing.ForSystem(rom.Data, GameSystem.PlayStation).Should().Be(Convert.ToHexStringLower(md5.GetHashAndReset()));
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
