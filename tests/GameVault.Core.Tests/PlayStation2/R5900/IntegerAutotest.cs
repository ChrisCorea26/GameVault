using System.Globalization;
using System.Text.RegularExpressions;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// Replays a ps2autotests integer program (cpu/ee/alu, cpu/ee/muldiv, cpu/ee_simd/*) from its <c>.expected</c> file:
/// each result line names the instruction and its inputs (a number set with <c>lui/ori</c>, or a named constant from
/// the test's <c>shared.h</c>), so the line is re-run on the CPU the way the test's macro runs it and the result is
/// printed in the test's format and compared with the hardware's.
/// </summary>
/// <remarks>
/// The registers keep their values from line to line, as the compiled tests' registers do: in cpu/ee the
/// <c>lui/ori</c> inputs leave a register's upper 64 bits as the previous test left them, and the hardware's output
/// shows it.
/// </remarks>
internal sealed partial class IntegerAutotest
{
    private const int RdVar = 8;
    private const int RsVar = 9;
    private const int RtVar = 10;

    private static readonly Dictionary<string, UInt128> Constants = new()
    {
        ["C_ZERO"] = Q(0, 0, 0, 0),
        ["C_S16_MAX"] = Q(0x7FFF, 0, 0, 0),
        ["C_S16_MIN"] = Q(0xFFFF8000, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF),
        ["C_S32_MAX"] = Q(0x7FFFFFFF, 0, 0, 0),
        ["C_S32_MIN"] = Q(0x80000000, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF),
        ["C_S64_MAX"] = Q(0xFFFFFFFF, 0x7FFFFFFF, 0, 0),
        ["C_S64_MIN"] = Q(0, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFF),
        ["C_NEGONE"] = Q(0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF),
        ["C_ONE"] = Q(1, 0, 0, 0),
        ["C_GARBAGE1"] = Q(0x1337, 0x1338, 0x1339, 0x133A),
        ["C_GARBAGE2"] = Q(0xDEADBEEF, 0xDEADBEEE, 0xDEADBEED, 0xDEADBEEC),
        ["C_P_S8_A"] = Q(0x80808080, 0xFFFFFFFF, 0x12345678, 0x7F7F7F7F),
        ["C_P_S8_B"] = Q(0x80FF127F, 0xFF807F34, 0x567F80FF, 0x7F78FF80),
        ["C_P_S16_A"] = Q(0x80008000, 0xFFFFFFFF, 0x12345678, 0x7FFF7FFF),
        ["C_P_S16_B"] = Q(0x8000FFFF, 0x12347FFF, 0xFFFF8000, 0x7FFF5678),
        ["C_P_S32_A"] = Q(0x80000000, 0xFFFFFFFF, 0x12345678, 0x7FFFFFFF),
        ["C_P_S32_B"] = Q(0xFFFFFFFF, 0x80000000, 0x7FFFFFFF, 0x12345678),
        ["C_P_S32_C"] = Q(0x80000000, 0xFFFFFFFF, 0x12345678, 0x7FFFFFFF),
        ["C_P_S32_D"] = Q(0x7FFFFFFF, 0x12345678, 0xFFFFFFFF, 0x80000000),
        ["C_P_S32_E"] = Q(0x00000000, 0x7FFFFFFF, 0xFFFFFFFF, 0x80000000),
        ["C_P_S32_F"] = Q(0xFFFFFFFF, 0x80000000, 0x00000000, 0x7FFFFFFF),
        ["C_ONEONEONEONE"] = Q(1, 1, 1, 1),
        ["C_TWOTWOTWOTWO"] = Q(2, 2, 2, 2),
    };

    private static readonly Dictionary<string, (ulong Hi, ulong Lo, ulong Hi1, ulong Lo1)> HiLoConstants = new()
    {
        ["C_HILO"] = (0x0123456789ABCDEF, 0x123456789ABCDEF0, 0x23456789ABCDEF01, 0x456789ABCDEF0123),
        ["C_HILO_ZERO"] = (0, 0, 0, 0),
        ["C_HILO_PATTERN"] = (0, 0x0000000010001234, 0, 0x00000000F000ABCD),
    };

    private readonly R5900TestMachine _machine = new();
    private readonly bool _simd;

    /// <param name="simd">cpu/ee_simd: <c>SET_U32</c> copies the value to both halves and the $0 tests clear with POR.</param>
    public IntegerAutotest(bool simd)
    {
        _simd = simd;
    }

    public List<string> Mismatches { get; } = [];

    public int Checked { get; private set; }

    /// <summary>The result lines in the file.</summary>
    public int Lines { get; private set; }

    /// <summary>Lines whose instruction this replayer doesn't know.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>
    /// Lines that matched except in the destination's upper 64 bits, which in cpu/ee come from whatever the compiled
    /// test last left in that register (a <c>lui/ori</c> input doesn't touch them): the register is resynchronised.
    /// </summary>
    public int Resynced { get; private set; }

    private bool ResyncHistory(string expected, string actual)
    {
        if (_simd || expected.Length != actual.Length)
        {
            return false;
        }

        var colon = expected.IndexOf(": ", StringComparison.Ordinal) + 2;
        if (expected[..colon] != actual[..colon] || expected[(colon + 17)..] != actual[(colon + 17)..])
        {
            return false;
        }

        var high = ulong.Parse(expected.Substring(colon, 17).Replace(" ", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var value = _machine.Cpu.GetGpr(RdVar);
        _machine.Cpu.SetGpr(RdVar, new UInt128(high, (ulong)value));
        Resynced++;
        return true;
    }

    private static UInt128 Q(uint w0, uint w1, uint w2, uint w3) =>
        new((ulong)w3 << 32 | w2, (ulong)w1 << 32 | w0);

    [GeneratedRegex(@"^  (\S+) (.*)$")]
    private static partial Regex LineRegex();

    public static string[] ReadExpected(string path) =>
        System.Text.Encoding.ASCII.GetString(TestRomLocator.ReadCached(path)).Replace("\r", "").Split('\n');

    /// <summary>Replays every result line of <paramref name="lines"/> whose instruction is in <see cref="Ee.Ops"/>.</summary>
    public void Run(IEnumerable<string> lines)
    {
        var indexInSection = 0;
        foreach (var line in lines)
        {
            if (!line.StartsWith("  ", StringComparison.Ordinal))
            {
                indexInSection = 0;
                continue;
            }

            Lines++;
            var match = LineRegex().Match(line);
            if (!match.Success || !Ee.Ops.ContainsKey(match.Groups[1].Value))
            {
                Skipped.Add(line);
                continue;
            }

            var actual = Replay(match.Groups[1].Value, match.Groups[2].Value, indexInSection++);
            Checked++;
            if (actual != line && !ResyncHistory(line, actual))
            {
                Mismatches.Add($"expected: {line.Trim()}\n     got: {actual.Trim()}");
            }
        }
    }

    private string Replay(string op, string rest, int index)
    {
        var cpu = _machine.Cpu;
        var (syntax, _) = Ee.Ops[op];
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        var operandText = rest[..colon];
        var printsHiLo = rest.IndexOf("H: ", colon, StringComparison.Ordinal) >= 0;

        if (operandText == "->" + " $0")
        {
            SetM(RdVar, Constants["C_GARBAGE1"]);
            SetM(RsVar, Constants["C_GARBAGE1"]);
            SetM(RtVar, Constants["C_GARBAGE1"]);
            var fields = syntax.Split(',');
            var operands = new int[fields.Length];
            operands[0] = 0;
            for (var k = 1; k < fields.Length; k++)
            {
                operands[k] = fields[k] is "i" or "a" ? ParseImmediateForZeroTest(op) : k == 1 ? RsVar : RtVar;
            }

            _machine.Run(Ee.Assemble(op, operands), _simd ? Ee.Por(RdVar, 0, 0) : Ee.Ori(RdVar, 0, 0));
            return $"  {op} -> $0: {FormatQuad(cpu.GetGpr(RdVar))}";
        }

        // Inputs: "a, b" or "a" or "a (C_HILO)".
        var hiLoName = "C_HILO";
        var parenthesis = operandText.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis >= 0)
        {
            hiLoName = operandText[(parenthesis + 2)..^1];
            operandText = operandText[..parenthesis];
        }

        var parts = operandText.Split(", ");
        var named = parts[0].StartsWith("C_", StringComparison.Ordinal);
        uint instruction;
        switch (syntax)
        {
            case "s,t": // RRHL
                SetInput(RsVar, parts[0]);
                SetInput(RtVar, parts[1]);
                instruction = Ee.Assemble(op, RsVar, RtVar);
                break;
            case "d" or "s": // RHL: the one register is both what's printed and the operand
                SetInput(RdVar, parts[0]);
                instruction = Ee.Assemble(op, RdVar);
                break;
            case "t,i": // RI (LUI): the first six lines start from lui/ori 0x1337, the rest from C_GARBAGE1
                InitDestination(index >= 6);
                instruction = Ee.Assemble(op, RdVar, ParseImmediate(parts[0]));
                break;
            case "t,s,i" or "d,t,a": // RRI, RRS
                InitDestination(named);
                SetInput(RsVar, parts[0]);
                instruction = Ee.Assemble(op, RdVar, RsVar, ParseImmediate(parts[1]));
                break;
            case "d,t" or "d,s": // RR
                InitDestination(named);
                SetInput(RsVar, parts[0]);
                instruction = Ee.Assemble(op, RdVar, RsVar);
                break;
            default: // RRR, RRRHL
                InitDestination(named);
                SetInput(RsVar, parts[0]);
                SetInput(RtVar, parts[1]);
                instruction = Ee.Assemble(op, RdVar, RsVar, RtVar);
                break;
        }

        if (printsHiLo)
        {
            var (hi, lo, hi1, lo1) = HiLoConstants[hiLoName];
            cpu.Hi = hi;
            cpu.Lo = lo;
            cpu.Hi1 = hi1;
            cpu.Lo1 = lo1;
        }

        _machine.Run(instruction);
        var head = rest[..(colon + 2)];
        var hiLo = $"H: {cpu.Hi:x16} {cpu.Hi1:x16} L: {cpu.Lo:x16} {cpu.Lo1:x16}";
        if (!printsHiLo)
        {
            return $"  {op} {head}{FormatQuad(cpu.GetGpr(RdVar))}";
        }

        return syntax == "s,t" ? $"  {op} {head}{hiLo}" : $"  {op} {head}{FormatQuad(cpu.GetGpr(RdVar))} {hiLo}";
    }

    private static int ParseImmediateForZeroTest(string op) => op switch
    {
        "sll" or "srl" or "sra" or "dsll" or "dsrl" or "dsra" or "dsll32" or "dsrl32" or "dsra32" => 5,
        "slti" or "sltiu" => 5,
        "psllh" or "psrlh" or "psrah" or "psllw" or "psrlw" or "psraw" => 5,
        _ => 0xDEAD,
    };

    private static int ParseImmediate(string text) => (int)uint.Parse(text, CultureInfo.InvariantCulture);

    private void InitDestination(bool named)
    {
        if (named)
        {
            SetM(RdVar, Constants["C_GARBAGE1"]);
        }
        else
        {
            SetU32(RdVar, 0x1337);
        }
    }

    private void SetInput(int register, string text)
    {
        if (text.StartsWith("C_", StringComparison.Ordinal))
        {
            SetM(register, Constants[text]);
        }
        else
        {
            SetU32(register, (uint)int.Parse(text, CultureInfo.InvariantCulture));
        }
    }

    /// <summary><c>lui/ori</c> (and in cpu/ee_simd a <c>pcpyld</c>): the value sign-extended into the low 64 bits.</summary>
    private void SetU32(int register, uint value)
    {
        var low = (ulong)(long)(int)value;
        if (_simd)
        {
            _machine.Cpu.SetGpr(register, new UInt128(low, low));
        }
        else
        {
            _machine.Cpu.SetGpr64(register, low);
        }
    }

    private void SetM(int register, UInt128 value) => _machine.Cpu.SetGpr(register, value);

    private static string FormatQuad(UInt128 value)
    {
        var high = (ulong)(value >> 64);
        var low = (ulong)value;
        return $"{high >> 32:x8} {(uint)high:x8} {low >> 32:x8} {(uint)low:x8}";
    }
}
