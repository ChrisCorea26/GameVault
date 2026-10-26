using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// Tests against the owner's own files (skipped unless <c>GAMEVAULT_LOCAL_ROMS</c> is set; GameVault never ships them):
/// <list type="bullet">
/// <item><c>psx/bios.bin</c>: the owner's 512 KB BIOS.</item>
/// <item><c>ps1-tests/**/&lt;name&gt;.exe</c> with the <c>psx.log</c> from the ps1-tests repository beside each: the
/// MIT-licensed ps1-tests programs from their GitHub release. They print through the BIOS, so they can't run in CI.</item>
/// </list>
/// </summary>
public sealed class PlayStationLocalTests
{
    private static string? Bios =>
        TestRomLocator.LocalRomsDirectory is { } root && Path.Combine(root, "psx", "bios.bin") is var path && File.Exists(path) ? path : null;

    [LocalRomFact]
    public void The_bios_boots_to_its_shell()
    {
        if (Bios is not { } bios)
        {
            return; // no BIOS in the folder
        }

        var console = new PlayStationConsole(File.ReadAllBytes(bios));
        console.Power();
        var sawShell = false;
        for (var frame = 0; frame < 600 && !sawShell; frame++)
        {
            console.RunFrame();
            sawShell = console.Cpu.Pc >= 0x80030000 && console.Cpu.Pc < 0x80200000;
        }

        sawShell.Should().BeTrue("the BIOS should reach its shell in RAM within ten seconds");
        console.FrameBuffer.ToArray().Should().Contain(pixel => pixel != 0, "the shell draws something");
    }

    /// <summary>Runs every ps1-tests program found and checks that the lines its hardware log reports appear in order.</summary>
    [LocalRomFact]
    public void Ps1_tests_print_what_the_hardware_printed()
    {
        if (Bios is not { } bios || TestRomLocator.LocalRomsDirectory is not { } root || !Directory.Exists(Path.Combine(root, "ps1-tests")))
        {
            return;
        }

        var failures = new List<string>();
        foreach (var exe in Directory.EnumerateFiles(Path.Combine(root, "ps1-tests"), "*.exe", SearchOption.AllDirectories))
        {
            var log = Path.Combine(Path.GetDirectoryName(exe)!, "psx.log");
            if (!File.Exists(log))
            {
                continue;
            }

            var output = new StringBuilder();
            var console = new PlayStationConsole(File.ReadAllBytes(bios), exe: PsExe.Parse(File.ReadAllBytes(exe)));
            console.TtyOutput = c => output.Append(c);
            console.Power();
            for (var frame = 0; frame < 60 * 30; frame++)
            {
                console.RunFrame();
            }

            var printed = Lines(output.ToString());
            var position = 0;
            foreach (var expected in Lines(File.ReadAllText(log)).Where(l => l.Contains("pass", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase)))
            {
                var found = printed.FindIndex(position, l => l == expected);
                if (found < 0)
                {
                    failures.Add($"{Path.GetFileName(exe)}: expected \"{expected}\"");
                    break;
                }

                position = found + 1;
            }
        }

        failures.Should().BeEmpty();
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
