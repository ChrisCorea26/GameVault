using System.Globalization;
using System.Text.RegularExpressions;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// Replays ps2autotests' FPU programs (cpu/ee_fpu: arithmetic, muldiv, sqrt, compare, convert) from their
/// <c>.expected</c> files, the way <see cref="IntegerAutotest"/> does the integer ones. Values print as
/// <c>bits/decimal</c>; only the bits are compared (the decimal is the PS2 libc's own float formatting).
/// </summary>
/// <remarks>
/// ACC is set as the tests set it (<c>adda.s v, -0</c>) and read as they read it (<c>madd.s f4, -0, +0</c>), so
/// those instructions' own behaviour is part of every accumulator line.
/// </remarks>
internal sealed partial class FpuAutotest
{
    private const int Fd = 1;
    private const int Fs = 2;
    private const int Ft = 3;
    private const int AccScratch = 4;
    private const int NegativeZero = 5;
    private const int PositiveZero = 6;

    private static readonly Dictionary<string, uint> Constants = new()
    {
        ["CF_ZERO"] = 0x00000000,
        ["CF_NEGZERO"] = 0x80000000,
        ["CF_MAX"] = 0x7FFFFFFF,
        ["CF_MIN"] = 0xFFFFFFFF,
        ["CF_MAX_MANTISSA"] = 0x3FFFFFFF,
        ["CF_MAX_EXP"] = 0x7F800001,
        ["CF_MIN_EXP"] = 0x00000001,
        ["CF_NEGONE"] = 0xBF800000,
        ["CF_ONE"] = 0x3F800000,
        ["CF_GARBAGE1"] = 0x00001337,
        ["CF_GARBAGE2"] = 0xDEADBEEF,
    };

    // The S-format function codes, and which operands each instruction reads.
    private static readonly Dictionary<string, (uint Funct, string Kind)> Ops = new()
    {
        ["add"] = (0x00, "fff"),
        ["sub"] = (0x01, "fff"),
        ["mul"] = (0x02, "fff"),
        ["div"] = (0x03, "fff"),
        ["max"] = (0x28, "fff"),
        ["min"] = (0x29, "fff"),
        ["rsqrt"] = (0x16, "fff"),
        ["sqrt"] = (0x04, "ff-t"),
        ["abs"] = (0x05, "ff"),
        ["mov"] = (0x06, "ff"),
        ["neg"] = (0x07, "ff"),
        ["cvt.w.s"] = (0x24, "ff"),
        ["cvt.s.w"] = (0x20, "ff-w"),
        ["adda"] = (0x18, "aff"),
        ["suba"] = (0x19, "aff"),
        ["mula"] = (0x1A, "aff"),
        ["madd"] = (0x1C, "faff"),
        ["msub"] = (0x1D, "faff"),
        ["madda"] = (0x1E, "aaff"),
        ["msuba"] = (0x1F, "aaff"),
        ["eq"] = (0x32, "cmp"),
        ["f"] = (0x30, "cmp"),
        ["lt"] = (0x34, "cmp"),
        ["le"] = (0x36, "cmp"),
    };

    private readonly R5900TestMachine _machine = new();

    public FpuAutotest()
    {
        _machine.Cpu.SetFpr(NegativeZero, 0x80000000);
        _machine.Cpu.SetFpr(PositiveZero, 0);
    }

    public List<string> Mismatches { get; } = [];

    public List<string> Skipped { get; } = [];

    public int Checked { get; private set; }

    public int Lines { get; private set; }

    [GeneratedRegex(@"^  (\S+) (?:\((\S+?)(?:/\S+)?\) )?(.*?): (.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"([0-9a-f]{8})/")]
    private static partial Regex BitsRegex();

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
            if (!match.Success || !Ops.TryGetValue(match.Groups[1].Value, out var op))
            {
                Skipped.Add(line);
                continue;
            }

            var accumulator = match.Groups[2].Success ? Value(match.Groups[2].Value) : 0;
            var inputs = match.Groups[3].Value.Split(", ").Select(Value).ToArray();
            var expected = match.Groups[4].Value;
            var actual = Replay(op.Funct, op.Kind, accumulator, inputs);
            var expectedValues = op.Kind == "cmp" ? expected : string.Join(" ", BitsRegex().Matches(expected).Select(m => m.Groups[1].Value));
            Checked++;
            if (actual != expectedValues)
            {
                Mismatches.Add($"{line.Trim()}  -> got {actual}");
            }
        }
    }

    private static uint Value(string text)
    {
        if (Constants.TryGetValue(text, out var constant))
        {
            return constant;
        }

        var slash = text.IndexOf('/');
        return uint.Parse(slash >= 0 ? text[..slash] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private string Replay(uint funct, string kind, uint accumulator, uint[] inputs)
    {
        var cpu = _machine.Cpu;
        cpu.SetFpr(Fd, 0x1337);
        cpu.SetFpr(Fs, inputs[0]);
        cpu.SetFpr(Ft, inputs.Length > 1 ? inputs[1] : 0);
        var setAccumulator = Ee.FpuS(0x18, 0, AccScratch, NegativeZero); // adda.s f4, -0
        var readAccumulator = Ee.FpuS(0x1C, AccScratch, NegativeZero, PositiveZero);
        switch (kind)
        {
            case "fff":
                _machine.Run(Ee.FpuS(funct, Fd, Fs, Ft));
                return $"{cpu.GetFpr(Fd):x8}";
            case "ff":
                _machine.Run(Ee.FpuS(funct, Fd, Fs, 0));
                return $"{cpu.GetFpr(Fd):x8}";
            case "ff-t": // SQRT.S fd, ft
                cpu.SetFpr(Ft, inputs[0]);
                _machine.Run(Ee.FpuS(funct, Fd, 0, Ft));
                return $"{cpu.GetFpr(Fd):x8}";
            case "ff-w":
                _machine.Run(Ee.CvtSW(Fd, Fs));
                return $"{cpu.GetFpr(Fd):x8}";
            case "cmp":
                _machine.Run(Ee.FpuS(funct, 0, Fs, Ft));
                return (cpu.Fcr31 & (1 << 23)) != 0 ? "true" : "false";
            case "aff":
                _machine.Run(LoadAccumulator(0x1337), setAccumulator, Ee.FpuS(funct, 0, Fs, Ft), readAccumulator);
                return $"{cpu.GetFpr(AccScratch):x8}";
            case "faff":
                _machine.Run(LoadAccumulator(accumulator), setAccumulator, Ee.FpuS(funct, Fd, Fs, Ft), readAccumulator);
                return $"{cpu.GetFpr(Fd):x8} {cpu.GetFpr(AccScratch):x8}";
            default: // aaff
                _machine.Run(LoadAccumulator(accumulator), setAccumulator, Ee.FpuS(funct, 0, Fs, Ft), readAccumulator);
                return $"{cpu.GetFpr(AccScratch):x8}";
        }
    }

    /// <summary>Puts <paramref name="value"/> in f4 (an MTC1 from a GPR set beforehand), for <c>adda.s f4, -0</c>.</summary>
    private uint LoadAccumulator(uint value)
    {
        _machine.Cpu.SetGpr64(14, value);
        return Ee.Mtc1(14, AccScratch);
    }
}
