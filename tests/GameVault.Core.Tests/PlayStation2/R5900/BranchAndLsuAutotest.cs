using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// Replays ps2autotests' branch programs (cpu/ee/branch, cpu/ee_fpu/branch): each line names a branch and its
/// inputs, and the test's block (which records whether the branch was followed, whether it set ra and whether its
/// delay slot ran) is assembled and run here.
/// </summary>
internal sealed partial class BranchAutotest
{
    private const int Result = 8;
    private const int Rs = 9;
    private const int Rt = 10;
    private const int T8 = 24;
    private const int T9 = 25;
    private const int Ra = 31;

    private readonly R5900TestMachine _machine = new();

    public List<string> Mismatches { get; } = [];

    public List<string> Skipped { get; } = [];

    public int Checked { get; private set; }

    public int Lines { get; private set; }

    [GeneratedRegex(@"^  (\S+)(?: (.*?))?: (.*)$")]
    private static partial Regex LineRegex();

    public void Run(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                continue;
            }

            Lines++;
            var match = LineRegex().Match(line);
            var result = match.Success ? Replay(match.Groups[1].Value, match.Groups[2].Value) : null;
            if (result is null)
            {
                Skipped.Add(line);
                continue;
            }

            Checked++;
            if (result != match.Groups[3].Value)
            {
                Mismatches.Add($"{line.Trim()}  -> got {result}");
            }
        }
    }

    private static uint Or(int rd, int rs, int rt) => Ee.R(0x25, rd, rs, rt);

    private string? Replay(string op, string operands)
    {
        var cpu = _machine.Cpu;
        var parts = operands.Length == 0 ? [] : operands.Split(", ");
        cpu.SetGpr64(Ra, 0x0BAD_0000);

        // The branch under test, reaching block index 6 from index 2 (offset 3).
        uint? branch = op switch
        {
            "beq" => Ee.I(0x04, Rt, Rs, 3),
            "bne" => Ee.I(0x05, Rt, Rs, 3),
            "beql" => Ee.I(0x14, Rt, Rs, 3),
            "bnel" => Ee.I(0x15, Rt, Rs, 3),
            "blez" => Ee.I(0x06, 0, Rs, 3),
            "bgtz" => Ee.I(0x07, 0, Rs, 3),
            "blezl" => Ee.I(0x16, 0, Rs, 3),
            "bgtzl" => Ee.I(0x17, 0, Rs, 3),
            "bltz" => Ee.I(0x01, 0x00, Rs, 3),
            "bgez" => Ee.I(0x01, 0x01, Rs, 3),
            "bltzl" => Ee.I(0x01, 0x02, Rs, 3),
            "bgezl" => Ee.I(0x01, 0x03, Rs, 3),
            "bltzal" => Ee.I(0x01, 0x10, Rs, 3),
            "bgezal" => Ee.I(0x01, 0x11, Rs, 3),
            "bltzall" => Ee.I(0x01, 0x12, Rs, 3),
            "bgezall" => Ee.I(0x01, 0x13, Rs, 3),
            "j" => Ee.J(R5900TestMachine.Origin + 6 * 4),
            "jal" => 0x0C00_0000 | (((R5900TestMachine.Origin + 6 * 4) >> 2) & 0x03FF_FFFF),
            "bc1f" => Ee.Bc1(0, 3),
            "bc1t" => Ee.Bc1(1, 3),
            "bc1fl" => Ee.Bc1(2, 3),
            "bc1tl" => Ee.Bc1(3, 3),
            _ => null,
        };

        if (op is "jalr" or "jr")
        {
            var target = R5900TestMachine.Origin + 8 * 4;
            var jump = op == "jalr" ? Ee.R(0x09, T9, T8) : Ee.Jr(T8);
            _machine.Run(
                Ee.Lui(Result, 0),
                Ee.Lui(T8, (int)(target >> 16)),
                Ee.Ori(T8, T8, (int)(target & 0xFFFF)),
                op == "jalr" ? Or(T9, 0, 0) : Or(T9, 0, Ra),
                jump,
                Ee.Ori(Result, Result, 4),
                Ee.Beq(0, 0, 2),
                Ee.Nop,
                Ee.Ori(Result, Result, 1), // index 8: the branch target
                op == "jalr" ? Ee.Beq(T9, 0, 2) : Ee.Beq(T9, Ra, 3),
                Ee.Nop,
                op == "jalr" ? Ee.Ori(Result, Result, 2) : Or(Ra, 0, T9),
                op == "jalr" ? Ee.Nop : Ee.Ori(Result, Result, 2));
            return Format((uint)cpu.GetGpr64(Result));
        }

        if (branch is null)
        {
            return null;
        }

        if (op.StartsWith("bc1", StringComparison.Ordinal))
        {
            // "1"/"0": FCR31's C set with CTC1; "c.eq"/"c.f": set by a compare.
            switch (parts[0])
            {
                case "1": cpu.Fcr31 |= 1 << 23; break;
                case "0": cpu.Fcr31 &= ~(1u << 23); break;
                case "c.eq": _machine.Run(Ee.FpuS(0x32, 0, 0, 0)); break;
                default: _machine.Run(Ee.FpuS(0x30, 0, 0, 0)); break;
            }
        }
        else
        {
            if (parts.Length > 0)
            {
                SetInput(Rs, parts[0]);
            }

            if (parts.Length > 1)
            {
                SetInput(Rt, parts[1]);
            }
        }

        _machine.Run(
            Ee.Lui(Result, 0),
            Or(T9, 0, Ra),
            branch.Value,
            Ee.Ori(Result, Result, 4),
            Ee.Beq(0, 0, 2),
            Ee.Nop,
            Ee.Ori(Result, Result, 1), // index 6: the branch target
            Ee.Beq(T9, Ra, 3),
            Ee.Nop,
            Or(Ra, 0, T9),
            Ee.Ori(Result, Result, 2));
        return Format((uint)cpu.GetGpr64(Result));
    }

    private void SetInput(int register, string text)
    {
        if (IntegerAutotest.Constants.TryGetValue(text, out var constant))
        {
            _machine.Cpu.SetGpr(register, constant);
        }
        else
        {
            _machine.Cpu.SetGpr64(register, (ulong)(long)int.Parse(text, CultureInfo.InvariantCulture));
        }
    }

    private static string Format(uint result) =>
        ((result & 1) != 0 ? "followed" : "skipped") + ", "
        + ((result & 2) != 0 ? "set ra" : "ignored ra") + ", "
        + ((result & 4) != 0 ? "ran delay slot" : "skipped delay slot");
}

/// <summary>
/// Replays ps2autotests' cpu/ee/lsu: loads from and stores into the test's 48-byte pattern, with the register
/// starting from <c>lui/ori 0xABCD4321</c> (its upper 64 bits left from before) or from C_GARBAGE1.
/// </summary>
internal sealed partial class LsuAutotest
{
    private const uint PatternAddress = 0x8002_0000;
    private const int Rt = 8;
    private const int Base = 9;

    private static readonly uint[] Pattern =
    [
        0x45678123, 0x9ABCDEF0, 0xDEADBEEF, 0xC0DE1337,
        0x23456789, 0xABCDEF01, 0xBEEFDEAD, 0xC0DEC0DE,
        0x8899AABB, 0xCCDDEEFF, 0x00112233, 0x44556677,
    ];

    private static readonly Dictionary<string, uint> Opcodes = new()
    {
        ["lb"] = 0x20, ["lh"] = 0x21, ["lwl"] = 0x22, ["lw"] = 0x23, ["lbu"] = 0x24, ["lhu"] = 0x25, ["lwr"] = 0x26,
        ["lwu"] = 0x27, ["ldl"] = 0x1A, ["ldr"] = 0x1B, ["ld"] = 0x37, ["lq"] = 0x1E, ["sb"] = 0x28, ["sh"] = 0x29,
        ["swl"] = 0x2A, ["sw"] = 0x2B, ["sdl"] = 0x2C, ["sdr"] = 0x2D, ["swr"] = 0x2E, ["sd"] = 0x3F, ["sq"] = 0x1F,
    };

    private readonly R5900TestMachine _machine = new();

    public LsuAutotest()
    {
        _machine.Cpu.SetGpr(Rt, IntegerAutotest.Constants["C_GARBAGE1"]);
        _machine.Cpu.SetGpr64(Base, PatternAddress + 16);
    }

    public List<string> Mismatches { get; } = [];

    public List<string> Skipped { get; } = [];

    public int Checked { get; private set; }

    public int Lines { get; private set; }

    [GeneratedRegex(@"^  (\w+) ([+-]?[+-]?\d+)(?:/(\w+) ([+-]\d+))?: (.*)$")]
    private static partial Regex AccessRegex();

    [GeneratedRegex(@"^  (\w+) -> \$0: (.*)$")]
    private static partial Regex ZeroRegex();

    public void Run(IEnumerable<string> lines)
    {
        var index = 0;
        var immediateLines = 3;
        foreach (var line in lines)
        {
            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                // A section header: "lb:", "lq:", "ldl / ldr:"; the first 3 (4 for LQ/SQ, 5 for the pairs) use lui/ori.
                index = 0;
                immediateLines = line.Contains('/') ? 5 : line is "lq:" or "sq:" ? 4 : 3;
                continue;
            }

            Lines++;
            string? actual = null;
            string expected;
            var zero = ZeroRegex().Match(line);
            var access = AccessRegex().Match(line);
            if (zero.Success)
            {
                expected = zero.Groups[2].Value;
                _machine.Cpu.SetGpr(Rt, IntegerAutotest.Constants["C_GARBAGE1"]);
                ResetPattern();
                _machine.Run(Ee.I(Opcodes[zero.Groups[1].Value], 0, Base, 0), Ee.R(0x0F), Ee.Ori(Rt, 0, 0));
                actual = FormatQuad(_machine.Cpu.GetGpr(Rt));
            }
            else if (access.Success && Opcodes.ContainsKey(access.Groups[1].Value))
            {
                expected = access.Groups[5].Value;
                if (index++ < immediateLines)
                {
                    _machine.Cpu.SetGpr64(Rt, 0xFFFF_FFFF_ABCD_4321);
                }
                else
                {
                    _machine.Cpu.SetGpr(Rt, IntegerAutotest.Constants["C_GARBAGE1"]);
                }

                ResetPattern();
                var first = access.Groups[1].Value;
                var offset = int.Parse(access.Groups[2].Value.Replace("+-", "-"), CultureInfo.InvariantCulture);
                var program = new List<uint> { Ee.I(Opcodes[first], Rt, Base, offset), Ee.R(0x0F) };
                if (access.Groups[3].Success)
                {
                    program.Add(Ee.I(Opcodes[access.Groups[3].Value], Rt, Base, int.Parse(access.Groups[4].Value, CultureInfo.InvariantCulture)));
                    program.Add(Ee.R(0x0F));
                }

                _machine.Run([.. program]);
                actual = first[0] == 's'
                    ? FormatQuad(ReadQuad(PatternAddress + 16 + (uint)(offset / 16 * 16)))
                    : FormatQuad(_machine.Cpu.GetGpr(Rt));
            }
            else
            {
                Skipped.Add(line);
                continue;
            }

            Checked++;
            if (actual != expected)
            {
                Mismatches.Add($"{line.Trim()}  -> got {actual}");
            }
        }
    }

    private void ResetPattern()
    {
        for (var i = 0; i < Pattern.Length; i++)
        {
            _machine.Bus.WriteWord((PatternAddress & 0x1FFF_FFFF) + (uint)(i * 4), Pattern[i]);
        }
    }

    private UInt128 ReadQuad(uint address)
    {
        var span = _machine.Bus.Rdram.AsSpan((int)(address & 0x1FFF_FFFF), 16);
        return new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), BinaryPrimitives.ReadUInt64LittleEndian(span));
    }

    private static string FormatQuad(UInt128 value)
    {
        var high = (ulong)(value >> 64);
        var low = (ulong)value;
        return $"{high >> 32:x8} {(uint)high:x8} {low >> 32:x8} {(uint)low:x8}";
    }
}
