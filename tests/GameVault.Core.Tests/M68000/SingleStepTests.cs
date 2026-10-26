using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GameVault.Core.Cpu.M68000;
using GameVault.Core.Tests.Harness;
using Xunit.Abstractions;

namespace GameVault.Core.Tests.M68000;

/// <summary>Runs only with <c>GAMEVAULT_RUN_68K=1</c> and the SingleStepTests 680x0 files fetched.</summary>
public sealed class SingleStepFactAttribute : FactAttribute
{
    public SingleStepFactAttribute()
    {
        Skip = Environment.GetEnvironmentVariable("GAMEVAULT_RUN_68K") == "1"
            ? null
            : "Set GAMEVAULT_RUN_68K=1 to run the SingleStepTests 68000 suite (about 1 million tests).";
    }
}

/// <summary>
/// The SingleStepTests 680x0 suite (SPEC §7.11, Phase 13): each test runs one instruction from a stated processor
/// and memory state and checks the registers, SR and memory afterwards. The prefetch queue's two words are placed
/// in memory at PC and PC+2. Cycle counts are reported, not required.
/// </summary>
public sealed class SingleStepTests(ITestOutputHelper output)
{
    /// <summary>
    /// Tests whose expected state disagrees with the rest of the suite: byte shifts that change all 32 bits of the
    /// register, and one divide by zero.
    /// </summary>
    private static readonly HashSet<string> KnownBad =
    [
        "e502 [ASL.b Q, D2] 1583",
        "e502 [ASL.b Q, D2] 1761",

        // Every other divide by zero pushes the next instruction's address; this one alone pushes its own.
        "80ef [DIVU (d16, A7), D0] 5745",
    ];

    /// <summary>The test files: <c>GAMEVAULT_68K_TESTS</c>, or the test-ROM cache.</summary>
    private static string Directory_ =>
        Environment.GetEnvironmentVariable("GAMEVAULT_68K_TESTS")
        ?? Path.Combine(TestRomLocator.CacheDirectory, "680x0", "68000", "v1");

    [SingleStepFact]
    public void Every_instruction_matches_the_hardware()
    {
        var directory = Directory_;
        Directory.Exists(directory).Should().BeTrue($"the SingleStepTests files are fetched into {directory}");
        var only = Environment.GetEnvironmentVariable("GAMEVAULT_68K_ONLY");
        var bus = new TestBus();
        var cpu = new M68000Cpu(bus);
        var report = new StringBuilder();
        var cycleReport = new StringBuilder();
        long total = 0, failed = 0, cycleMismatches = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json.gz").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file)[..^".json.gz".Length];
            if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(name))
            {
                continue;
            }

            var (count, failures, cycles, examples) = RunFile(file, cpu, bus);
            total += count;
            failed += failures;
            cycleMismatches += cycles.Count;
            if (cycles.Count > 0)
            {
                cycleReport.AppendLine($"{name}: {cycles.Count} cycle counts differ, such as {string.Join("; ", cycles.Take(3))}");
            }

            if (failures > 0)
            {
                report.AppendLine($"{name}: {failures} of {count} failed");
                foreach (var example in examples)
                {
                    report.AppendLine("    " + example);
                }
            }
        }

        output.WriteLine($"{total - failed} of {total} passed; {cycleMismatches} cycle counts differ");
        output.WriteLine(report.ToString());
        output.WriteLine(cycleReport.ToString());
        failed.Should().Be(0, report.ToString());
    }

    private static (int Count, int Failures, List<string> CycleMismatches, List<string> Examples) RunFile(string file, M68000Cpu cpu, TestBus bus)
    {
        using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(stream);
        var count = 0;
        var failures = 0;
        var cycleMismatches = new List<string>();
        var examples = new List<string>();
        foreach (var test in document.RootElement.EnumerateArray())
        {
            if (KnownBad.Contains(test.GetProperty("name").GetString()!))
            {
                continue;
            }

            count++;
            var initial = test.GetProperty("initial");
            var final = test.GetProperty("final");
            bus.Clear();
            Load(cpu, bus, initial);
            var cycles = cpu.Step();
            var problems = Compare(cpu, bus, final);
            var expectedCycles = test.GetProperty("length").GetInt32();
            if (cycles != expectedCycles)
            {
                cycleMismatches.Add($"{test.GetProperty("name").GetString()} {cycles} instead of {expectedCycles}");
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

        return (count, failures, cycleMismatches, examples);
    }

    private static void Load(M68000Cpu cpu, TestBus bus, JsonElement state)
    {
        var pc = state.GetProperty("pc").GetUInt32();
        var prefetch = state.GetProperty("prefetch");
        bus.Poke(pc, (byte)(prefetch[0].GetUInt32() >> 8));
        bus.Poke(pc + 1, (byte)prefetch[0].GetUInt32());
        bus.Poke(pc + 2, (byte)(prefetch[1].GetUInt32() >> 8));
        bus.Poke(pc + 3, (byte)prefetch[1].GetUInt32());
        foreach (var cell in state.GetProperty("ram").EnumerateArray())
        {
            bus.Poke(cell[0].GetUInt32(), (byte)cell[1].GetUInt32());
        }

        // SR first: it picks which stack pointer A7 is.
        cpu.Sr = (ushort)state.GetProperty("sr").GetUInt32();
        cpu.Usp = state.GetProperty("usp").GetUInt32();
        cpu.Ssp = state.GetProperty("ssp").GetUInt32();
        for (var i = 0; i < 8; i++)
        {
            cpu.SetD(i, state.GetProperty($"d{i}").GetUInt32());
        }

        for (var i = 0; i < 7; i++)
        {
            cpu.SetA(i, state.GetProperty($"a{i}").GetUInt32());
        }

        cpu.Pc = pc;
        cpu.InterruptLevel = 0;
    }

    private static List<string> Compare(M68000Cpu cpu, TestBus bus, JsonElement state)
    {
        var problems = new List<string>();
        void Check(string name, uint actual, uint expected)
        {
            if (actual != expected)
            {
                problems.Add($"{name} {actual:X8} expected {expected:X8}");
            }
        }

        for (var i = 0; i < 8; i++)
        {
            Check($"D{i}", cpu.GetD(i), state.GetProperty($"d{i}").GetUInt32());
        }

        for (var i = 0; i < 7; i++)
        {
            Check($"A{i}", cpu.GetA(i), state.GetProperty($"a{i}").GetUInt32());
        }

        Check("USP", cpu.Usp, state.GetProperty("usp").GetUInt32());
        Check("SSP", cpu.Ssp, state.GetProperty("ssp").GetUInt32());
        Check("SR", cpu.Sr, state.GetProperty("sr").GetUInt32());
        Check("PC", cpu.Pc, state.GetProperty("pc").GetUInt32());
        foreach (var cell in state.GetProperty("ram").EnumerateArray())
        {
            var address = cell[0].GetUInt32();
            Check($"[{address:X6}]", bus.Peek(address), cell[1].GetUInt32());
        }

        return problems;
    }

    /// <summary>16 MB of RAM that remembers which bytes were touched, so each test starts clean quickly.</summary>
    private sealed class TestBus : IM68000Bus
    {
        private readonly byte[] _memory = new byte[1 << 24];
        private readonly List<uint> _touched = [];

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
            address &= 0xFFFFFF;
            _memory[address] = value;
            _touched.Add(address);
        }

        public byte Peek(uint address) => _memory[address & 0xFFFFFF];

        public byte ReadByte(uint address) => _memory[address];

        public ushort ReadWord(uint address) => (ushort)((_memory[address] << 8) | _memory[(address + 1) & 0xFFFFFF]);

        public void WriteByte(uint address, byte value) => Poke(address, value);

        public void WriteWord(uint address, ushort value)
        {
            Poke(address, (byte)(value >> 8));
            Poke(address + 1, (byte)value);
        }

        public int AcknowledgeInterrupt(int level) => -1;
    }
}
