using System.Diagnostics;
using GameVault.Core.Input;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using GameVault.Core.Systems;
using GameVault.Library.Discs;
using GameVault.Library.Profiles;
using GameVault.Library.Tests.Discs;
using GameVault.Platform.Achievements;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;
using GameVault.Core.Tests.Cartridge;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;
using Xunit.Abstractions;

namespace GameVault.Platform.Tests;

/// <summary>
/// PlayStation 2 games in the emulator host (SPEC §7.11, Phase 17): GameVault's homebrew ELFs through the whole host,
/// discs and the BIOS, the memory card, save states, input, and the rewind, run-ahead and netplay budgets
/// (docs/design/host-budgets.md).
/// </summary>
public sealed class PlayStation2HostTests(ITestOutputHelper output) : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-ps2-");

    public void Dispose() => _root.Delete(recursive: true);

    private static string ElfPath(string name) => Path.Combine(FindRepoRoot(), "tests", "Ps2Homebrew", "elf", name + ".elf");

    /// <summary>A stand-in BIOS of zeros (no Sony code): enough to build a disc console, which then runs NOPs.</summary>
    private static PlayStation2Setup ZeroBios() => new(new byte[Ps2Console.BiosSize]);

    /// <summary>A small PS2 DVD image (SYSTEM.CNF with BOOT2 and a boot ELF), padded past the 32 sectors the console hashes.</summary>
    private string WriteIso(string name = "Game.iso")
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllBytes(path, Ps2TestImages.Iso(padSectors: 32));
        return path;
    }

    [Fact]
    public async Task A_homebrew_elf_loads_runs_and_shows_its_640x448_picture_with_the_dualshock_2()
    {
        var path = Path.Combine(_root.FullName, "gs-basic.elf");
        File.Copy(ElfPath("gs-basic"), path);
        var frames = new FrameSink();
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(frames, input);

        await host.LoadAsync(path);

        host.System.Should().Be(GameSystem.PlayStation2);
        input.Layout.Should().Be(PadLayout.PlayStation2);
        host.AchievementsHash.Should().HaveLength(32, "an ELF hashes whole");
        await WaitFor(async () => await host.InspectAsync(c => BitConverter.ToUInt32(((Ps2Console)c).Rdram, 0x1F_FF00)) == 0x42485647u);
        ushort[] pixels = [];
        await WaitFor(() =>
        {
            if (frames.TryGetVideoFrame(out var frame) && frame.Width == 640 && frame.Height == 448)
            {
                pixels = frame.Span.ToArray();
                return Task.FromResult(pixels.Distinct().Count() > 8);
            }

            return Task.FromResult(false);
        });
        pixels.Distinct().Count().Should().BeGreaterThan(8, "gs-basic draws many colours");

        // Achievements read RDRAM, then the scratchpad at $02000000, and nothing past it.
        var read = await host.InspectAsync(c =>
        {
            ((Ps2Console)c).Cpu.Scratchpad[5] = 0x5A;
            var buffer = new byte[8];
            var signature = host.ReadForAchievements(0x1F_FF00, buffer.AsSpan(0, 4));
            var scratch = host.ReadForAchievements(AchievementsHashing.PlayStation2ScratchpadAddress + 5, buffer.AsSpan(4, 1));
            var past = host.ReadForAchievements(AchievementsHashing.PlayStation2MemoryLimit, buffer.AsSpan(5, 3));
            return (signature, scratch, past, buffer);
        });
        read.signature.Should().Be(4u);
        read.buffer[..4].Should().Equal("GVHB"u8.ToArray());
        read.scratch.Should().Be(1u);
        read.buffer[4].Should().Be(0x5A);
        read.past.Should().Be(1u, "the scratchpad's last byte is the last one");

        host.Stop();
        input.Layout.Should().Be(PadLayout.Nes);
    }

    [Fact]
    public async Task Ps2_input_reaches_the_consoles_pad()
    {
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var host = new EmulatorHost(new FrameSink(), input);
        await host.LoadAsync(ElfPath("gs-basic"));

        input.KeyDown(VirtualKeys.Z); // Cross
        input.KeyDown(VirtualKeys.Digit5); // L3
        input.KeyDown(VirtualKeys.L); // right stick right
        Ps2PadInput pad = default;
        await WaitFor(async () =>
        {
            pad = await host.InspectAsync(c => ((Ps2Console)c).Iop.Sio2.Pads[0].Input);
            return (pad.Buttons & Ps2PadInput.Cross) != 0;
        });

        (pad.Buttons & Ps2PadInput.L3).Should().NotBe(0);
        pad.RightX.Should().Be(127);
        pad.LeftX.Should().Be(0);
        host.Stop();
    }

    [Fact]
    public async Task A_disc_without_a_bios_says_to_choose_one()
    {
        var iso = WriteIso();
        var paths = new AppPaths(Path.Combine(_root.FullName, "data"));
        var settings = new JsonSettingsStore(paths);
        await settings.SaveAsync(new AppSettings());
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings, paths: paths);

        await FluentActions.Awaiting(() => host.LoadAsync(iso)).Should().ThrowAsync<GameLoadException>()
            .WithMessage(PlayStation2Setup.NeedsBiosMessage);
        host.Status.Should().Be(EmulatorStatus.Idle);

        // A BIOS of the wrong size is caught before the console is built.
        FluentActions.Invoking(() => host.Load(Ps2Discs.Reference(iso), "Game", system: GameSystem.PlayStation2, playStation2: new PlayStation2Setup(new byte[1024])))
            .Should().Throw<GameLoadException>().WithMessage("*4 MB*");
        FluentActions.Invoking(() => host.Load([1, 2, 3, 4], "Junk", system: GameSystem.PlayStation2))
            .Should().Throw<GameLoadException>().WithMessage("*isn't a PlayStation 2 program*");
    }

    [Fact]
    public async Task Save_states_load_with_the_disc_still_in_the_drive_and_into_a_new_console()
    {
        var iso = WriteIso();
        var data = Ps2Discs.Reference(iso);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        host.Load(data, "Game", system: GameSystem.PlayStation2, playStation2: ZeroBios());
        await WaitFor(async () => await host.InspectAsync(c => c.FrameCount) > 3);

        var capture = (await host.CaptureAsync())!;
        capture.State.Length.Should().BeInRange(40_000_000, 60_000_000, "RDRAM, GS memory, IOP RAM, sound RAM and the 8.6 MB card");
        await WaitFor(async () => await host.InspectAsync(c => c.FrameCount) > 8);
        (await host.RestoreAsync(capture.State)).Should().BeTrue();
        var sector = await host.InspectAsync(c =>
        {
            var buffer = new byte[IPs2Disc.SectorSize];
            ((Ps2Console)c).Iop.Disc!.ReadSector(16, buffer);
            return buffer;
        });
        sector![1..6].Should().Equal("CD001"u8.ToArray(), "the disc stays in the drive: states don't hold it");

        // A new console (a challenge check, a restart) opens its own copy of the disc; the state loads over it.
        var fresh = (Ps2Console)ConsoleFactory.Create(data, GameSystem.PlayStation2, new GameProfile { Sha1 = "x" }, null, playStation2: ZeroBios());
        try
        {
            fresh.LoadSnapshot(capture.State);
            fresh.Iop.Disc.Should().NotBeNull();
            fresh.RunFrame();
            fresh.RomHash.Should().Be(capture.RomHash);
        }
        finally
        {
            (fresh.Iop.Disc as IDisposable)?.Dispose();
        }

        host.Stop();
    }

    [Fact]
    public async Task Each_game_gets_a_formatted_memory_card_saved_as_a_ps2_image_and_loaded_next_time()
    {
        var paths = new AppPaths(Path.Combine(_root.FullName, "data"));
        var elf = ElfPath("gs-basic");
        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), paths: paths))
        {
            await host.LoadAsync(elf);
            var superblock = await host.InspectAsync(c => System.Text.Encoding.ASCII.GetString(((Ps2Console)c).MemoryCard.Data, 0, 28));
            superblock.Should().Be("Sony PS2 Memory Card Format ", "a new card comes formatted");
            host.Pause(); // flushes saves
            await host.InspectAsync(c => true);
            (Directory.Exists(paths.SavesDirectory) ? Directory.GetFiles(paths.SavesDirectory, "*.ps2") : [])
                .Should().BeEmpty("an unchanged card isn't written");
            host.Resume();

            await host.InspectAsync(c =>
            {
                var card = ((Ps2Console)c).MemoryCard;
                card.Data[0x4000] = 0x42;
                card.Dirty = true;
                return true;
            });
            host.Stop();
        }

        var file = Directory.GetFiles(paths.SavesDirectory, "*.ps2").Should().ContainSingle().Which;
        Path.GetFileName(file).Should().StartWith("gs-basic - ");
        new FileInfo(file).Length.Should().Be(Ps2MemoryCardStore.Size);
        File.ReadAllBytes(file)[0x4000].Should().Be(0x42);

        using (var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), paths: paths))
        {
            await host.LoadAsync(elf);
            (await host.InspectAsync(c => ((Ps2Console)c).MemoryCard.Data[0x4000])).Should().Be(0x42, "the saved card is back in slot 1");
            host.Stop();
        }
    }

    [Fact]
    public async Task Rewind_captures_every_half_second_in_the_background_and_run_ahead_and_netplay_are_off()
    {
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        host.RunAheadFrames = 2;
        await host.LoadAsync(ElfPath("gs-basic"));

        var budgets = await host.InspectAsync(c => (host.RewindHistory.CaptureInterval, host.RewindHistory.CompressInBackground, host.RunAheadState.Allowed));
        budgets.Should().Be((SystemBudgets.PlayStation2RewindInterval, true, false));
        await WaitFor(async () => await host.InspectAsync(c => host.RewindHistory.Count) >= 2);
        await FluentActions.Awaiting(() => host.HostNetplayAsync()).Should().ThrowAsync<NetplayException>().WithMessage("*PlayStation 2*");
        var netplay = new NetplayViewModel(host, new InMemoryClipboard());
        netplay.StatusText.Should().Contain("PlayStation 2");

        // Other systems keep the defaults.
        await host.LoadAsync(Path.Combine(_root.FullName, WriteNes()));
        (await host.InspectAsync(c => (host.RewindHistory.CaptureInterval, host.RewindHistory.CompressInBackground, host.RunAheadState.Allowed)))
            .Should().Be((RewindBuffer.Interval, false, true));
        host.Stop();
    }

    [Fact]
    public void A_ps2_challenge_result_is_checked_by_replaying_it_on_a_new_console()
    {
        // The goal: the homebrew has signed its status block ("G" of "GVHB" at $1FFF00), counted from power-on.
        var elf = File.ReadAllBytes(ElfPath("gs-basic"));
        var console = new Ps2Console(Ps2Boot.FromElf(elf), null, Ps2MemoryCard.CreateFormatted());
        var start = Convert.ToBase64String(console.SaveSnapshot());
        var frames = 0;
        while (console.PeekMemory(0x1F_FF00) != (byte)'G')
        {
            console.SetControllerState(0, ControllerState.B);
            console.RunFrame();
            frames++;
        }

        var sha1 = GameVault.Library.Hashing.RomHasher.Hash(elf, GameSystem.PlayStation2).Sha1;
        var challenge = new GameVault.Library.Challenges.ChallengeFile
        {
            Id = "ps2", RomSha1 = sha1, Name = "Sign", Goal = [GameVault.Library.Memory.MemoryCondition.Raw(0x1F_FF00, "==", 'G')], StartState = start,
        };
        var inputs = Enumerable.Repeat(((ulong)ControllerState.B, 0UL), frames).ToList();
        var result = new GameVault.Library.Challenges.ChallengeResult
        {
            ChallengeId = "ps2", RomSha1 = sha1, Frames = frames,
            Inputs = Convert.ToBase64String(GameVault.Library.Challenges.InputRecording.Encode(inputs)),
        };

        ChallengeVerifier.Verify(elf, challenge, result, GameSystem.PlayStation2).Verified.Should().BeTrue();
        ChallengeVerifier.Verify(elf, challenge, result with { Frames = frames + 1, Inputs = Convert.ToBase64String(
            GameVault.Library.Challenges.InputRecording.Encode([.. inputs, inputs[0]])) }, GameSystem.PlayStation2).Verified.Should().BeFalse();
    }

    [Fact]
    public async Task Under_sample_data_the_sample_ps2_game_is_a_ps2_with_a_640x448_picture()
    {
        var frames = new FrameSink();
        using var host = new SampleEmulatorHost(frames);
        var game = SampleLibraryService.Games.Single(g => g.System == GameSystem.PlayStation2);

        await host.LoadAsync(game.RomPath!);

        host.System.Should().Be(GameSystem.PlayStation2);
        await WaitFor(() => Task.FromResult(frames.TryGetVideoFrame(out var frame) && frame.Width == 640 && frame.Height == 448 && frame.Span.ToArray().Distinct().Count() > 10));
        (await host.CaptureScreenshotAsync()).Should().NotBeNull();
        new NetplayViewModel(host, new InMemoryClipboard()).StatusText.Should().Contain("PlayStation 2");

        host.Stop();
        host.System.Should().BeNull();
        await host.LoadAsync("anything.nes");
        host.System.Should().Be(GameSystem.Nes);
        host.Stop();
    }

    private string WriteNes()
    {
        var rom = BankedRom.Build(mapper: 0, prgBanks8K: 4, chrBanks1K: 8);
        File.WriteAllBytes(Path.Combine(_root.FullName, "Game.nes"), rom);
        return "Game.nes";
    }

    /// <summary>
    /// The numbers behind <see cref="SystemBudgets"/>, on the homebrew: a state's size, saving and loading it, a rewind
    /// capture on the emulation thread (the snapshot only) and in all, and a frame with one frame of run-ahead.
    /// </summary>
    [Fact]
    public void Ps2_state_rewind_and_run_ahead_costs_fit_the_budgets()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(ElfPath("vu1-path1"))), null, Ps2MemoryCard.CreateFormatted());
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 60; i++)
        {
            console.RunFrame();
        }

        var frame = clock.Elapsed.TotalMilliseconds / 60;
        var writer = new StateWriter();
        console.SaveSnapshot(writer); // the first save grows the writer
        var save = Median(5, () =>
        {
            writer.Reset();
            console.SaveSnapshot(writer);
        });
        var state = writer.ToArray();
        var load = Median(5, () => console.LoadSnapshot(state));

        var inBackground = new RewindBuffer { CaptureInterval = 1, CompressInBackground = true };
        var onThread = Median(5, () => inBackground.OnFrame(console), after: () => _ = inBackground.Count); // waits outside the timing
        var whole = new RewindBuffer();
        var capture = Median(5, () =>
        {
            whole.OnFrame(console);
            whole.OnFrame(console); // the second frame of each interval doesn't capture
        });

        var runAhead = new RunAhead { Frames = 1 };
        var withRunAhead = Median(5, () => runAhead.RunFrame(console, _ => { }));
        output.WriteLine(
            $"state {state.Length:N0} bytes; frame {frame:F1} ms; save {save:F1} ms; load {load:F1} ms; " +
            $"rewind capture {capture:F1} ms ({whole.BytesUsed / Math.Max(1, whole.Count):N0} bytes each); " +
            $"emulation thread with background compression {onThread:F1} ms; frame with run-ahead {withRunAhead:F1} ms");

        state.Length.Should().BeInRange(45_000_000, 55_000_000, "about 49 MB, the memory card included");
        inBackground.Count.Should().Be(5);
        save.Should().BeLessThan(capture, "compressing a capture costs far more than taking it");

        // The wall-clock budget (a capture spread over its interval costing less than a frame) holds in Release, which
        // docs/design/host-budgets.md records; Debug builds on a loaded CI machine are several times slower, so it is
        // only printed here.
    }

    private static double Median(int runs, Action action, Action? after = null)
    {
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var start = Stopwatch.GetTimestamp();
            action();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            after?.Invoke();
        }

        Array.Sort(times);
        return times[runs / 2];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root wasn't found.");
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

            await Task.Delay(10);
        }
    }
}
