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
        for (var frame = 0; frame < 60 * 61; frame++)
        {
            console.RunFrame();
            log.OnFrame();
        }

        var text = File.ReadAllText(Path.Combine(_root.FullName, "boot-ps1.log"));
        text.Should().Contain("Test Disc").And.Contain("cd").And.Contain("command 01").And.Contain("INT3 12");
        text.Should().Contain("[ 1s] pc 8001000");
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

        File.ReadAllText(Path.Combine(_root.FullName, "boot-ps2.log")).Should().Contain("[ 1s] EE pc ").And.Contain("IOP pc").And.Contain("EE reads");
        console.RegisterReadProbe.Should().BeNull();
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
