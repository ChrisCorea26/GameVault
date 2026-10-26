using System.Text;
using System.Text.Json;
using FluentAssertions;
using GameVault.Core.Cpu.W65816;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.W65816;

/// <summary>Runs only with <c>GAMEVAULT_RUN_65816=1</c> and the SingleStepTests 65816 files fetched.</summary>
public sealed class W65816SingleStepFactAttribute : FactAttribute
{
    public W65816SingleStepFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("GAMEVAULT_RUN_65816") == "1"
            ? null
            : "Set GAMEVAULT_RUN_65816=1 to run the SingleStepTests 65816 suite (about 10 million tests).";
    }
}

/// <summary>
/// The SingleStepTests 65816 suite (MIT; SPEC §7.11, Phase 14): one instruction per test, native (".n") and
/// emulation (".e") mode, checking registers, flags and memory. Cycle counts (bus cycles, including internal ones)
/// are reported.
/// </summary>
public sealed class W65816SingleStepTests(ITestOutputHelper output)
{
    private static string Directory_ =>
        Environment.GetEnvironmentVariable("GAMEVAULT_65816_TESTS")
        ?? Path.Combine(TestRomLocator.CacheDirectory, "65816", "v1");

    [W65816SingleStepFact]
    public void Every_instruction_matches_the_hardware()
    {
        var directory = Directory_;
        Directory.Exists(directory).Should().BeTrue($"the SingleStepTests 65816 files are fetched into {directory}");
        var only = Environment.GetEnvironmentVariable("GAMEVAULT_65816_ONLY");
        var limit = int.TryParse(Environment.GetEnvironmentVariable("GAMEVAULT_65816_LIMIT"), out var l) ? l : int.MaxValue;
        var bus = new TestBus();
        var cpu = new W65816Cpu(bus);
        var report = new StringBuilder();
        var cycleReport = new StringBuilder();
        long total = 0, failed = 0, cycleMismatches = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(name))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var count = 0;
            var failures = 0;
            var examples = new List<string>();
            var cycleExamples = new List<string>();
            var cycleCount = 0;
            foreach (var test in document.RootElement.EnumerateArray())
            {
                if (count >= limit)
                {
                    break;
                }

                count++;
                bus.Clear();
                Load(cpu, bus, test.GetProperty("initial"));
                var start = cpu.Pc;
                cpu.Step();

                // MVN/MVP move a byte per step and repeat until A runs out; a test covers the whole move.
                while (cpu.Pc == start && bus.Peek((uint)(cpu.Pbr << 16 | start)) is 0x44 or 0x54 && bus.Cycles < 1_000_000)
                {
                    cpu.Step();
                }
                var problems = Compare(cpu, bus, test.GetProperty("final"));
                if (problems.Count > 0)
                {
                    failures++;
                    if (examples.Count < 4)
                    {
                        examples.Add($"{test.GetProperty("name").GetString()}: {string.Join(", ", problems.Take(6))}");
                    }
                }

                var expected = test.GetProperty("cycles").GetArrayLength();
                if (bus.Cycles != expected)
                {
                    cycleCount++;
                    if (cycleExamples.Count < 3)
                    {
                        cycleExamples.Add($"{test.GetProperty("name").GetString()} {bus.Cycles} instead of {expected}");
                    }
                }
            }

            total += count;
            failed += failures;
            cycleMismatches += cycleCount;
            if (failures > 0)
            {
                report.AppendLine($"{name}: {failures} of {count} failed");
                foreach (var example in examples)
                {
                    report.AppendLine("    " + example);
                }
            }

            if (cycleCount > 0)
            {
                cycleReport.AppendLine($"{name}: {cycleCount} cycle counts differ, such as {string.Join("; ", cycleExamples)}");
            }
        }

        output.WriteLine($"{total - failed} of {total} passed; {cycleMismatches} cycle counts differ");
        output.WriteLine(report.ToString());
        output.WriteLine(cycleReport.ToString());
        failed.Should().Be(0, report.ToString());
    }

    private static void Load(W65816Cpu cpu, TestBus bus, JsonElement state)
    {
        foreach (var cell in state.GetProperty("ram").EnumerateArray())
        {
            bus.Poke(cell[0].GetUInt32(), (byte)cell[1].GetUInt32());
        }

        cpu.E = state.GetProperty("e").GetInt32() != 0;
        cpu.P = (byte)state.GetProperty("p").GetInt32();
        cpu.A = (ushort)state.GetProperty("a").GetInt32();
        cpu.X = (ushort)state.GetProperty("x").GetInt32();
        cpu.Y = (ushort)state.GetProperty("y").GetInt32();
        var s = (ushort)state.GetProperty("s").GetInt32();
        cpu.S = cpu.E ? (ushort)(0x0100 | (s & 0xFF)) : s; // emulation mode keeps the stack in page 1
        cpu.D = (ushort)state.GetProperty("d").GetInt32();
        cpu.Dbr = (byte)state.GetProperty("dbr").GetInt32();
        cpu.Pbr = (byte)state.GetProperty("pbr").GetInt32();
        cpu.Pc = (ushort)state.GetProperty("pc").GetInt32();
        cpu.Nmi = false;
        cpu.Irq = false;
        cpu.Waiting = false;
        cpu.Stopped = false;
        bus.Cycles = 0;
    }

    private static List<string> Compare(W65816Cpu cpu, TestBus bus, JsonElement state)
    {
        var problems = new List<string>();
        void Check(string name, int actual, int expected)
        {
            if (actual != expected)
            {
                problems.Add($"{name} {actual:X} expected {expected:X}");
            }
        }

        Check("A", cpu.A, state.GetProperty("a").GetInt32());
        Check("X", cpu.X, state.GetProperty("x").GetInt32());
        Check("Y", cpu.Y, state.GetProperty("y").GetInt32());
        Check("S", cpu.S, state.GetProperty("s").GetInt32());
        Check("P", cpu.P, state.GetProperty("p").GetInt32());
        Check("PC", cpu.Pc, state.GetProperty("pc").GetInt32());
        Check("D", cpu.D, state.GetProperty("d").GetInt32());
        Check("DBR", cpu.Dbr, state.GetProperty("dbr").GetInt32());
        Check("PBR", cpu.Pbr, state.GetProperty("pbr").GetInt32());
        Check("E", cpu.E ? 1 : 0, state.GetProperty("e").GetInt32());
        foreach (var cell in state.GetProperty("ram").EnumerateArray())
        {
            var address = cell[0].GetUInt32();
            Check($"[{address:X6}]", bus.Peek(address), cell[1].GetInt32());
        }

        return problems;
    }

    private sealed class TestBus : IW65816Bus
    {
        private readonly byte[] _memory = new byte[1 << 24];
        private readonly List<uint> _touched = [];

        public int Cycles { get; set; }

        public void Clear()
        {
            foreach (var address in _touched)
            {
                _memory[address] = 0;
            }

            _touched.Clear();
        }

        public void Poke(uint address, byte value)
        {
            _memory[address & 0xFFFFFF] = value;
            _touched.Add(address & 0xFFFFFF);
        }

        public byte Peek(uint address) => _memory[address & 0xFFFFFF];

        public byte Read(uint address)
        {
            Cycles++;
            return _memory[address];
        }

        public void Write(uint address, byte value)
        {
            Cycles++;
            Poke(address, value);
        }

        public void Idle() => Cycles++;
    }
}
