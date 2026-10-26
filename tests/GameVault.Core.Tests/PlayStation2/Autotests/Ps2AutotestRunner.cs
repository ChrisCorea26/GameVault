using System.Text;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.Autotests;

/// <summary>The result of running one ps2autotests program on the whole console.</summary>
internal sealed record Ps2AutotestResult(
    string Output, string Expected, bool Exited, int Frames, string? Fault, string UnknownSyscalls, string Machine = "")
{
    public override string ToString() =>
        $"exited={Exited} frames={Frames} fault={Fault ?? "none"} unknown syscalls=[{UnknownSyscalls}] {Machine}\n--- output ---\n{Output}\n--- expected ---\n{Expected}";
}

/// <summary>
/// Runs a ps2autotests EE program (jpd002/ps2autotests, ISC) on <see cref="Ps2Console"/> with GameVault's kernel and IOP
/// services until it exits or <c>maxFrames</c> pass, collecting what it prints.
/// </summary>
internal static class Ps2AutotestRunner
{
    public const string CacheFolder = "ps2autotests/";

    public static Ps2AutotestResult Run(string test, int maxFrames = 600, Action<string>? trace = null, string? dump = null)
    {
        var elf = TestRomLocator.ReadCached(CacheFolder + test + ".elf");
        var expected = Normalize(Encoding.ASCII.GetString(TestRomLocator.ReadCached(CacheFolder + test + ".expected")));
        var output = new StringBuilder();
        var console = new Ps2Console(Ps2Boot.FromElf(elf, "host:" + Path.GetFileName(test) + ".elf"))
        {
            DebugOutput = c => output.Append(c),
        };

        if (trace is not null)
        {
            console.Kernel!.SyscallTrace = (n, pc) =>
                trace($"syscall {n:X} at {pc:X8} a0={console.Cpu.GetGpr64(4):X} a1={console.Cpu.GetGpr64(5):X} st={console.Cpu.Status:X}");
        }

        if (trace is not null)
        {
            console.IopServices!.Trace = trace;
        }

        var frames = 0;
        while (!console.Exited && frames < maxFrames)
        {
            console.RunFrame();
            frames++;
        }

        var machine = $"pc={console.Cpu.Pc:X8} st={console.Cpu.Status:X8} d5={console.Dmac.ReadRegister(0x1000C000):X} "
                      + $"d6={console.Dmac.ReadRegister(0x1000C400):X} dstat={console.Dmac.Stat:X} sif0={console.Sif.Sif0.Count} "
                      + $"sif1={console.Sif.Sif1.Count} intc={console.Intc.Stat:X}/{console.Intc.Mask:X}{Dump(console, dump)}";
        return new Ps2AutotestResult(
            Normalize(output.ToString()), expected, console.Exited, frames, console.Kernel!.Fault,
            string.Join(",", console.Kernel.UnknownSyscalls.Select(n => n.ToString("X"))), machine);
    }

    private static string Dump(Ps2Console console, string? spec)
    {
        if (spec is null)
        {
            return "";
        }

        var text = new StringBuilder();
        foreach (var part in spec.Split(','))
        {
            var address = Convert.ToUInt32(part, 16) & 0x01FF_FFFF;
            text.Append($"\n  {address:X8}:");
            for (var i = 0u; i < 16; i++)
            {
                text.Append($" {BitConverter.ToUInt32(console.Rdram, (int)(address + i * 4)):X8}");
            }
        }

        return text.ToString();
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
