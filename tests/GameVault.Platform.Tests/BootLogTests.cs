using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.PlayStation;
using GameVault.Platform.Emulation;

namespace GameVault.Platform.Tests;

/// <summary>The boot log of PlayStation and PlayStation 2 games (docs/bugs.md GV-023, GV-024).</summary>
public sealed class BootLogTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gv-bootlog-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void A_playstation_log_has_the_cd_rom_commands_and_a_line_a_second_then_its_probes_come_off()
    {
        var exe = LoopExe();
        var console = new PlayStationConsole(disc: TestDisc.Create(exe), exe: PsExe.Parse(exe));
        console.Power();
        var log = BootLog.Start(console, _root.FullName, "Test Disc")!;

        console.Write8(0x1F801800, 0);
        console.Write8(0x1F801801, 0x01); // Getstat
        console.SetControllerState(0, GameVault.Core.Input.ControllerState.Start);
        for (var frame = 0; frame < 60 * 61; frame++)
        {
            console.RunFrame();
            log.OnFrame();
        }

        var text = File.ReadAllText(Path.Combine(_root.FullName, "boot-ps1.log"));
        text.Should().Contain("Test Disc").And.Contain("cd").And.Contain("command 01").And.Contain("INT3 12");
        text.Should().Contain("of real time for 60 frames) pc 8001000").And.Contain("I_STAT ").And.Contain("pad 1 held: Start  port conversations: 0");
        text.Should().Contain("(end of the boot log)");
        console.TtyOutput.Should().BeNull();
        console.IoReadProbe.Should().BeNull();
        console.CdRom.Trace.Should().BeNull();
    }

    [Fact]
    public void A_playstation_2_log_has_both_processors()
    {
        var elf = File.ReadAllBytes(Path.Combine(FindRepoRoot(), "tests", "Ps2Homebrew", "elf", "gs-basic.elf"));
        var console = new Ps2Console(Ps2Boot.FromElf(elf), null, Ps2MemoryCard.CreateFormatted());
        using (var log = BootLog.Start(console, _root.FullName, "gs-basic")!)
        {
            for (var frame = 0; frame < 60; frame++)
            {
                console.RunFrame();
                log.OnFrame();
            }
        }

        File.ReadAllText(Path.Combine(_root.FullName, "boot-ps2.log")).Should().Contain("for 60 frames) EE pc ").And.Contain("IOP pc").And.Contain("EE reads");
        console.RegisterReadProbe.Should().BeNull();
    }

    [Fact]
    public void When_no_frame_finishes_the_watchdog_writes_the_processors_their_last_writes_and_the_stuck_stack()
    {
        var elf = File.ReadAllBytes(Path.Combine(FindRepoRoot(), "tests", "Ps2Homebrew", "elf", "gs-basic.elf"));
        var console = new Ps2Console(Ps2Boot.FromElf(elf), null, Ps2MemoryCard.CreateFormatted());
        BootLog? log = null;
        using var release = new ManualResetEventSlim();
        var emulation = new Thread(() =>
        {
            log = BootLog.Start(console, _root.FullName, "gs-basic", TimeSpan.FromMilliseconds(100), stallTicks: 2);
            console.Write32(0x1000_F590, 0x1201); // D_ENABLEW, so the EE's last writes aren't empty
            log!.OnFrame();
            StuckInTheEmulator(release);
        });
        emulation.Start();

        var path = Path.Combine(_root.FullName, "boot-ps2.log");
        var text = "";
        for (var i = 0; i < 300 && !text.Contains("(end of stall)"); i++)
        {
            Thread.Sleep(50);
            text = ReadShared(path);
            if (text.Contains("[stall]") && text.Contains("emulation thread:"))
            {
                Thread.Sleep(2000); // the stack is written right after
                text = ReadShared(path) + "(end of stall)";
            }
        }

        release.Set();
        emulation.Join();
        log!.Dispose();

        text.Should().Contain("[stall] no frame has finished").And.Contain("EE pc ").And.Contain("1000F590=00001201");
        text.Should().Contain(nameof(StuckInTheEmulator), "the stack names the method the emulation thread is stuck in");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void StuckInTheEmulator(ManualResetEventSlim release) => release.Wait();

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>A PS-EXE at $80010000 that loops: <c>b . ; nop</c>.</summary>
    private static byte[] LoopExe()
    {
        var exe = new byte[PsExe.HeaderSize + 8];
        "PS-X EXE"u8.CopyTo(exe);
        BitConverter.GetBytes(0x80010000u).CopyTo(exe, 0x10);
        BitConverter.GetBytes(0x80010000u).CopyTo(exe, 0x18);
        BitConverter.GetBytes(8u).CopyTo(exe, 0x1C);
        BitConverter.GetBytes(0x1000FFFFu).CopyTo(exe, PsExe.HeaderSize);
        return exe;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GameVault.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }
}
