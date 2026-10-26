using System.Text;
using System.Text.Json;
using FluentAssertions;
using GameVault.Core.Snes;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.Snes;

/// <summary>Runs only with <c>GAMEVAULT_RUN_SPC700=1</c> and the SingleStepTests spc700 files fetched.</summary>
public sealed class Spc700SingleStepFactAttribute : FactAttribute
{
    public Spc700SingleStepFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("GAMEVAULT_RUN_SPC700") == "1"
            ? null
            : "Set GAMEVAULT_RUN_SPC700=1 to run the SingleStepTests spc700 suite (256,000 tests).";
    }
}

/// <summary>
/// The SingleStepTests spc700 suite (MIT; SPEC §7.11, Phase 14): one instruction per test, checking registers,
/// flags, memory and the cycle count.
/// </summary>
public sealed class Spc700SingleStepTests(ITestOutputHelper output)
{
    private static string Directory_ =>
        Environment.GetEnvironmentVariable("GAMEVAULT_SPC700_TESTS")
        ?? Path.Combine(TestRomLocator.CacheDirectory, "spc700", "v1");

    [Spc700SingleStepFact]
    public void Every_instruction_matches_the_hardware()
    {
        var directory = Directory_;
        Directory.Exists(directory).Should().BeTrue($"the SingleStepTests spc700 files are fetched into {directory}");
        var bus = new TestBus();
        var cpu = new Spc700(bus);
        var report = new StringBuilder();
        long total = 0, failed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var count = 0;
            var failures = 0;
            var examples = new List<string>();
            foreach (var test in document.RootElement.EnumerateArray())
            {
                count++;
                bus.Clear();
                var initial = test.GetProperty("initial");
                foreach (var cell in initial.GetProperty("ram").EnumerateArray())
                {
                    bus.Poke((ushort)cell[0].GetInt32(), (byte)cell[1].GetInt32());
                }

                cpu.A = (byte)initial.GetProperty("a").GetInt32();
                cpu.X = (byte)initial.GetProperty("x").GetInt32();
                cpu.Y = (byte)initial.GetProperty("y").GetInt32();
                cpu.Sp = (byte)initial.GetProperty("sp").GetInt32();
                cpu.Psw = (byte)initial.GetProperty("psw").GetInt32();
                cpu.Pc = (ushort)initial.GetProperty("pc").GetInt32();
                cpu.Halted = false;
                var cycles = cpu.Step();

                var final = test.GetProperty("final");
                var problems = new List<string>();
                void Check(string name, int actual, int expected)
                {
                    if (actual != expected)
                    {
                        problems.Add($"{name} {actual:X} expected {expected:X}");
                    }
                }

                Check("A", cpu.A, final.GetProperty("a").GetInt32());
                Check("X", cpu.X, final.GetProperty("x").GetInt32());
                Check("Y", cpu.Y, final.GetProperty("y").GetInt32());
                Check("SP", cpu.Sp, final.GetProperty("sp").GetInt32());
                Check("PSW", cpu.Psw, final.GetProperty("psw").GetInt32());
                Check("PC", cpu.Pc, final.GetProperty("pc").GetInt32());
                Check("cycles", cycles, test.GetProperty("cycles").GetArrayLength());
                foreach (var cell in final.GetProperty("ram").EnumerateArray())
                {
                    var address = (ushort)cell[0].GetInt32();
                    Check($"[{address:X4}]", bus.Peek(address), cell[1].GetInt32());
                }

                if (problems.Count > 0)
                {
                    failures++;
                    if (examples.Count < 4)
                    {
                        examples.Add($"{test.GetProperty("name").GetString()}: {string.Join(", ", problems.Take(6))}");
                    }
                }
            }

            total += count;
            failed += failures;
            if (failures > 0)
            {
                report.AppendLine($"{Path.GetFileName(file)}: {failures} of {count} failed");
                foreach (var example in examples)
                {
                    report.AppendLine("    " + example);
                }
            }
        }

        output.WriteLine($"{total - failed} of {total} passed");
        output.WriteLine(report.ToString());
        failed.Should().Be(0, report.ToString());
    }

    private sealed class TestBus : ISpc700Bus
    {
        private readonly byte[] _memory = new byte[0x10000];

        public void Clear() => Array.Clear(_memory);

        public void Poke(ushort address, byte value) => _memory[address] = value;

        public byte Peek(ushort address) => _memory[address];

        public byte Read(ushort address) => _memory[address];

        public void Write(ushort address, byte value) => _memory[address] = value;
    }
}
