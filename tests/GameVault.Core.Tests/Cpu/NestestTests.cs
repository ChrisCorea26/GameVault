using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Cpu;

/// <summary>nestest in automation mode: start at $C000 and compare every line of the trace (SPEC §4.2).</summary>
public class NestestTests
{
    private const string Rom = "nestest/nestest.nes";
    private const string Log = "nestest/nestest.log";

    [TestRomFact(Rom, Log)]
    public void Trace_matches_nestest_log_line_for_line()
    {
        var expected = System.Text.Encoding.ASCII.GetString(TestRomLocator.ReadCached(Log))
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        var console = NesConsole.FromBytes(TestRomLocator.ReadCached(Rom));
        console.Power();
        console.Cpu.PC = 0xC000;

        for (var i = 0; i < expected.Length; i++)
        {
            var actual = console.Trace();
            if (actual != expected[i])
            {
                var context = string.Join(Environment.NewLine, expected.Skip(Math.Max(0, i - 3)).Take(3));
                Assert.Fail($"Trace differs at line {i + 1}.{Environment.NewLine}" +
                    $"Previous lines:{Environment.NewLine}{context}{Environment.NewLine}" +
                    $"Expected: {expected[i]}{Environment.NewLine}Actual:   {actual}");
            }

            console.StepInstruction();
        }

        // nestest stores its error codes at $02/$03; both are zero on success.
        console.Bus.Peek(0x0002).Should().Be(0, "official opcode tests report errors in $02");
        console.Bus.Peek(0x0003).Should().Be(0, "unofficial opcode tests report errors in $03");
    }
}
