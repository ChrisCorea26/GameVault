using FluentAssertions;
using GameVault.Core.Cpu.R4300;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cpu;

/// <summary>The VR4300's COP1 (Phase 16): operations, rounding modes, register pairing, compares and exceptions.</summary>
public sealed partial class R4300FpuTests
{
    private const int S = 16, D = 17, W = 20, L = 21;

    // FCR31 bits.
    private const uint RoundZero = 1, RoundPlus = 2, RoundMinus = 3;
    private const uint FlagInexact = 1 << 2, FlagUnderflow = 1 << 3, FlagOverflow = 1 << 4, FlagDivide = 1 << 5, FlagInvalid = 1 << 6;
    private const uint EnableInexact = 1 << 7, EnableDivide = 1 << 10, EnableInvalid = 1 << 11;
    private const uint CauseInexact = 1 << 12, CauseUnderflow = 1 << 13, CauseOverflow = 1 << 14, CauseDivide = 1 << 15,
        CauseInvalid = 1 << 16, CauseUnimplemented = 1 << 17;
    private const uint Flush = 1 << 24;
    private const uint Causes = 0x3F << 12;

    private static readonly Dictionary<string, int> Functions = new()
    {
        ["add"] = 0, ["sub"] = 1, ["mul"] = 2, ["div"] = 3, ["sqrt"] = 4, ["abs"] = 5, ["mov"] = 6, ["neg"] = 7,
        ["round.l"] = 8, ["trunc.l"] = 9, ["ceil.l"] = 10, ["floor.l"] = 11,
        ["round.w"] = 12, ["trunc.w"] = 13, ["ceil.w"] = 14, ["floor.w"] = 15,
        ["cvt.s"] = 32, ["cvt.d"] = 33, ["cvt.w"] = 36, ["cvt.l"] = 37,
        ["c.f"] = 48, ["c.un"] = 49, ["c.eq"] = 50, ["c.ueq"] = 51, ["c.olt"] = 52, ["c.ult"] = 53, ["c.ole"] = 54,
        ["c.ule"] = 55, ["c.sf"] = 56, ["c.ngle"] = 57, ["c.seq"] = 58, ["c.ngl"] = 59, ["c.lt"] = 60, ["c.nge"] = 61,
        ["c.le"] = 62, ["c.ngt"] = 63,
    };

    private static uint Op(int format, int function, int fd = 0, int fs = 0, int ft = 1) =>
        (0x11u << 26) | ((uint)format << 21) | ((uint)ft << 16) | ((uint)fs << 11) | ((uint)fd << 6) | (uint)function;

    private static R4300Fpu NewFpu(uint fcr31 = 0) => new() { Fr = true, Fcr31 = fcr31 };

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static ulong Dbl(double value) => BitConverter.DoubleToUInt64Bits(value);

    private static int FormatOf(char c) => c switch { 's' => S, 'd' => D, 'w' => W, _ => L };

    private static bool Wide(int format) => format is D or L;

    /// <summary>
    /// Every check in PeterLemon's CPUTest/CP1 programs (ABS, ADD, C.cond, CEIL, CVT, DIV, FLOOR, MUL, NEG, ROUND,
    /// SQRT, SUB, TRUNC), extracted from their .asm sources: f0 = a, f1 = b, then the instruction (fd = fs = f0), and
    /// the stored result or, for compares, the condition the program's BC1T/BC1F expects. FCR31 is 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(PeterLemonVectors))]
    public void PeterLemon_cp1_checks(string mnemonic, ulong a, ulong b, ulong expected)
    {
        var parts = mnemonic.Split('.');
        var source = FormatOf(parts[^1][0]);
        var function = Functions[string.Join('.', parts[..^1])];
        var destination = parts.Length == 3 && !mnemonic.StartsWith("c.", StringComparison.Ordinal) ? FormatOf(parts[1][0]) : source;
        var fpu = NewFpu();
        if (Wide(source))
        {
            fpu.WriteDoubleword(0, a);
            fpu.WriteDoubleword(1, b);
        }
        else
        {
            fpu.WriteWord(0, (uint)a);
            fpu.WriteWord(1, (uint)b);
        }

        fpu.Execute(Op(source, function)).Should().BeTrue($"{mnemonic} shouldn't trap (FCR31 {fpu.Fcr31:X8})");
        if (function >= 48)
        {
            fpu.Condition.Should().Be(expected != 0);
        }
        else if (Wide(destination))
        {
            fpu.ReadDoubleword(0).Should().Be(expected, mnemonic);
        }
        else
        {
            fpu.ReadWord(0).Should().Be((uint)expected, mnemonic);
        }
    }

    [Theory]
    [InlineData(0u, 0x3EAAAAABu, 0xBEAAAAABu)]
    [InlineData(RoundZero, 0x3EAAAAAAu, 0xBEAAAAAAu)]
    [InlineData(RoundPlus, 0x3EAAAAABu, 0xBEAAAAAAu)]
    [InlineData(RoundMinus, 0x3EAAAAAAu, 0xBEAAAAABu)]
    public void Single_division_rounds_in_every_mode(uint mode, uint third, uint minusThird)
    {
        var fpu = NewFpu(mode);
        fpu.WriteWord(0, F(1));
        fpu.WriteWord(1, F(3));
        fpu.Execute(Op(S, 3, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(third);
        fpu.Fcr31.Should().Be(mode | CauseInexact | FlagInexact);

        fpu.WriteWord(0, F(-1));
        fpu.Execute(Op(S, 3, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(minusThird);
    }

    [Theory]
    [InlineData(0u, 0x3FD5555555555555ul)]
    [InlineData(RoundZero, 0x3FD5555555555555ul)]
    [InlineData(RoundPlus, 0x3FD5555555555556ul)]
    [InlineData(RoundMinus, 0x3FD5555555555555ul)]
    public void Double_division_rounds_in_every_mode(uint mode, ulong third)
    {
        var fpu = NewFpu(mode);
        fpu.WriteDoubleword(0, Dbl(1));
        fpu.WriteDoubleword(1, Dbl(3));
        fpu.Execute(Op(D, 3, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(third);
    }

    [Theory]
    [InlineData(0u, 0x3FF0000000000000ul)]
    [InlineData(RoundZero, 0x3FF0000000000000ul)]
    [InlineData(RoundPlus, 0x3FF0000000000001ul)]
    [InlineData(RoundMinus, 0x3FF0000000000000ul)]
    public void Double_addition_uses_the_exact_error(uint mode, ulong expected)
    {
        var fpu = NewFpu(mode);
        fpu.WriteDoubleword(0, Dbl(1));
        fpu.WriteDoubleword(1, Dbl(Math.Pow(2, -60)));
        fpu.Execute(Op(D, 0, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(expected);
        (fpu.Fcr31 & CauseInexact).Should().NotBe(0);
    }

    [Theory]
    [InlineData(0u, 0x3FB504F3u)]
    [InlineData(RoundZero, 0x3FB504F3u)]
    [InlineData(RoundPlus, 0x3FB504F4u)]
    [InlineData(RoundMinus, 0x3FB504F3u)]
    public void Square_root_rounds_in_every_mode(uint mode, uint expected)
    {
        var fpu = NewFpu(mode);
        fpu.WriteWord(0, F(2));
        fpu.Execute(Op(S, 4, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(expected);
    }

    [Theory]
    [InlineData(0u, 0x7F800000u)]
    [InlineData(RoundZero, 0x7F7FFFFFu)]
    [InlineData(RoundPlus, 0x7F800000u)]
    [InlineData(RoundMinus, 0x7F7FFFFFu)]
    public void Overflow_depends_on_the_rounding_mode(uint mode, uint expected)
    {
        var fpu = NewFpu(mode);
        fpu.WriteWord(0, F(float.MaxValue));
        fpu.WriteWord(1, F(2));
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(expected);
        fpu.Fcr31.Should().Be(mode | CauseOverflow | CauseInexact | FlagOverflow | FlagInexact);
    }

    [Fact]
    public void Exact_zero_sums_are_negative_only_when_rounding_down()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, F(1));
        fpu.WriteWord(1, F(-1));
        fpu.Execute(Op(S, 0, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0u);

        fpu.Fcr31 = RoundMinus;
        fpu.Execute(Op(S, 0, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0x80000000u);
        (fpu.Fcr31 & Causes).Should().Be(0);
    }

    [Fact]
    public void Division_by_zero_gives_infinity_or_traps_when_enabled()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, F(-1));
        fpu.WriteWord(1, F(0));
        fpu.Execute(Op(S, 3, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0xFF800000u);
        fpu.Fcr31.Should().Be(CauseDivide | FlagDivide);

        fpu = NewFpu(EnableDivide);
        fpu.WriteWord(0, F(1));
        fpu.WriteWord(1, F(0));
        fpu.WriteWord(2, 0x12345678);
        fpu.Execute(Op(S, 3, fd: 2)).Should().BeFalse();
        fpu.ReadWord(2).Should().Be(0x12345678u, "a trapped result isn't written");
        fpu.Fcr31.Should().Be(EnableDivide | CauseDivide, "the flag isn't set when the exception is taken");
    }

    [Fact]
    public void Invalid_operations_give_the_default_nan()
    {
        var fpu = NewFpu();
        fpu.WriteDoubleword(0, Dbl(0));
        fpu.WriteDoubleword(1, Dbl(0));
        fpu.Execute(Op(D, 3, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(R4300Fpu.DoubleDefaultNaN);
        fpu.Fcr31.Should().Be(CauseInvalid | FlagInvalid);

        fpu = NewFpu();
        fpu.WriteWord(0, F(-4));
        fpu.Execute(Op(S, 4, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(R4300Fpu.SingleDefaultNaN);

        fpu = NewFpu(EnableInvalid);
        fpu.WriteWord(0, F(float.PositiveInfinity));
        fpu.WriteWord(1, F(float.NegativeInfinity));
        fpu.Execute(Op(S, 0, fd: 2)).Should().BeFalse();
        fpu.Fcr31.Should().Be(EnableInvalid | CauseInvalid);
    }

    [Fact]
    public void Quiet_nan_operands_are_invalid_and_signalling_ones_unimplemented()
    {
        // MIPS NaNs are quiet with the fraction's top bit clear.
        var fpu = NewFpu();
        fpu.WriteWord(0, 0x7F800001);
        fpu.WriteWord(1, F(1));
        fpu.Execute(Op(S, 0, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(R4300Fpu.SingleDefaultNaN);
        fpu.Fcr31.Should().Be(CauseInvalid | FlagInvalid);

        fpu.WriteWord(0, 0x7FC00000);
        fpu.Execute(Op(S, 0, fd: 2)).Should().BeFalse();
        (fpu.Fcr31 & Causes).Should().Be(CauseUnimplemented);

        fpu = NewFpu();
        fpu.WriteWord(0, 0x7FC00000);
        fpu.Execute(Op(S, 5, fd: 2)).Should().BeFalse("ABS checks its operand too");
    }

    [Fact]
    public void Denormal_operands_are_unimplemented()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, 0x00000001);
        fpu.WriteWord(1, F(1));
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeFalse();
        fpu.Fcr31.Should().Be(CauseUnimplemented);

        fpu.WriteDoubleword(4, 0x0000000000000001);
        fpu.Execute(Op(D, 13, fd: 2, fs: 4)).Should().BeFalse("TRUNC.W.D of a denormal");
    }

    [Fact]
    public void Denormal_results_are_unimplemented_unless_flushed()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, F(MathF.Pow(2, -100)));
        fpu.WriteWord(1, F(MathF.Pow(2, -30)));
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeFalse();
        fpu.Fcr31.Should().Be(CauseUnimplemented);

        fpu.Fcr31 = Flush;
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0u);
        fpu.Fcr31.Should().Be(Flush | CauseUnderflow | CauseInexact | FlagUnderflow | FlagInexact);

        fpu.Fcr31 = Flush | RoundPlus;
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0x00800000u, "rounding up flushes to the smallest normal number");

        fpu.Fcr31 = Flush | RoundMinus;
        fpu.WriteWord(1, F(-MathF.Pow(2, -30)));
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0x80800000u);

        fpu.Fcr31 = Flush | EnableInexact;
        fpu.Execute(Op(S, 2, fd: 2)).Should().BeFalse("FS doesn't flush with the inexact exception enabled");
    }

    [Fact]
    public void Doubles_that_underflow_completely_are_tiny()
    {
        var fpu = NewFpu(Flush);
        fpu.WriteDoubleword(0, Dbl(Math.Pow(2, -600)));
        fpu.WriteDoubleword(1, Dbl(Math.Pow(2, -600)));
        fpu.Execute(Op(D, 2, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(0ul);
        (fpu.Fcr31 & CauseUnderflow).Should().NotBe(0);
    }

    [Theory]
    [InlineData(12, 2.5f, 2)] // ROUND.W: to even
    [InlineData(12, 3.5f, 4)]
    [InlineData(13, -2.7f, -2)] // TRUNC.W
    [InlineData(14, -2.7f, -2)] // CEIL.W
    [InlineData(15, -2.2f, -3)] // FLOOR.W
    public void Integer_conversions_round_their_own_way(int function, float value, int expected)
    {
        var fpu = NewFpu(RoundPlus);
        fpu.WriteWord(0, F(value));
        fpu.Execute(Op(S, function, fd: 2)).Should().BeTrue();
        ((int)fpu.ReadWord(2)).Should().Be(expected);
        fpu.Fcr31.Should().Be(RoundPlus | CauseInexact | FlagInexact);
    }

    [Theory]
    [InlineData(0u, -2)]
    [InlineData(RoundZero, -1)]
    [InlineData(RoundPlus, -1)]
    [InlineData(RoundMinus, -2)]
    public void Cvt_w_uses_the_rounding_mode(uint mode, int expected)
    {
        var fpu = NewFpu(mode);
        fpu.WriteDoubleword(0, Dbl(-1.5));
        fpu.Execute(Op(D, 36, fd: 2)).Should().BeTrue();
        ((int)fpu.ReadWord(2)).Should().Be(expected);
    }

    [Fact]
    public void Out_of_range_conversions_are_unimplemented()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, F(3e9f));
        fpu.Execute(Op(S, 36, fd: 2)).Should().BeFalse("3e9 doesn't fit a word");
        fpu.Fcr31.Should().Be(CauseUnimplemented);

        fpu.WriteDoubleword(0, Dbl(Math.Pow(2, 53)));
        fpu.Execute(Op(D, 37, fd: 2)).Should().BeFalse("the VR4300 converts to longs below 2^53 only");

        fpu.WriteDoubleword(0, Dbl(Math.Pow(2, 53) - 1));
        fpu.Execute(Op(D, 37, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be((1UL << 53) - 1);

        fpu.WriteDoubleword(0, Dbl(double.NaN));
        fpu.Execute(Op(D, 9, fd: 2)).Should().BeFalse("TRUNC.L of a NaN");

        fpu.WriteDoubleword(0, Dbl(double.PositiveInfinity));
        fpu.Execute(Op(D, 13, fd: 2)).Should().BeFalse("TRUNC.W of infinity");

        fpu.WriteDoubleword(0, 1UL << 55);
        fpu.Execute(Op(L, 32, fd: 2)).Should().BeFalse("CVT.S.L takes 55 bits at most");
        fpu.WriteDoubleword(0, unchecked((ulong)-(1L << 55)));
        fpu.Execute(Op(L, 33, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(Dbl(-Math.Pow(2, 55)));
    }

    [Theory]
    [InlineData(0u, 0x4B800000u)]
    [InlineData(RoundZero, 0x4B800000u)]
    [InlineData(RoundPlus, 0x4B800001u)]
    [InlineData(RoundMinus, 0x4B800000u)]
    public void Integer_to_single_conversions_round(uint mode, uint expected)
    {
        var fpu = NewFpu(mode);
        fpu.WriteWord(0, 16777217);
        fpu.Execute(Op(W, 32, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(expected);
        (fpu.Fcr31 & CauseInexact).Should().NotBe(0);
    }

    [Fact]
    public void Long_to_single_rounds_once()
    {
        // 2^54 + 2^30 + 1: rounding to a double first would land on the midpoint 2^54 + 2^30 and then go to even.
        var value = (1L << 54) + (1L << 30) + 1;
        var fpu = NewFpu();
        fpu.WriteDoubleword(0, (ulong)value);
        fpu.Execute(Op(L, 32, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(F((float)value));
        BitConverter.UInt32BitsToSingle(fpu.ReadWord(2)).Should().Be(MathF.BitIncrement((float)(1L << 54)));
    }

    [Fact]
    public void Single_to_double_and_back()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, F(1.1f));
        fpu.Execute(Op(S, 33, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(Dbl(1.1f));
        (fpu.Fcr31 & Causes).Should().Be(0);

        fpu.WriteDoubleword(0, Dbl(1.1));
        fpu.Fcr31 = RoundPlus;
        fpu.Execute(Op(D, 32, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(F(1.1f), "1.1f is just above 1.1");
        (fpu.Fcr31 & CauseInexact).Should().NotBe(0);

        fpu.Fcr31 = RoundMinus;
        fpu.Execute(Op(D, 32, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(F(MathF.BitDecrement(1.1f)));
    }

    [Fact]
    public void Quiet_compares_ignore_quiet_nans_and_signalling_ones_do_not()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, 0x7F800001); // quiet NaN on MIPS
        fpu.WriteWord(1, F(1));
        fpu.Execute(Op(S, Functions["c.ueq"])).Should().BeTrue();
        fpu.Condition.Should().BeTrue();
        fpu.Execute(Op(S, Functions["c.eq"])).Should().BeTrue();
        fpu.Condition.Should().BeFalse();
        (fpu.Fcr31 & FlagInvalid).Should().Be(0);

        fpu.Execute(Op(S, Functions["c.seq"])).Should().BeTrue();
        fpu.Fcr31.Should().Be(CauseInvalid | FlagInvalid);

        fpu.WriteWord(0, 0x7FC00000); // signalling NaN on MIPS
        fpu.Fcr31 = 0;
        fpu.Execute(Op(S, Functions["c.un"])).Should().BeTrue();
        fpu.Fcr31.Should().Be(CauseInvalid | FlagInvalid | (1u << 23));

        fpu.Fcr31 = EnableInvalid;
        fpu.Execute(Op(S, Functions["c.f"])).Should().BeFalse();
        fpu.Condition.Should().BeFalse();
    }

    [Fact]
    public void Compares_order_signed_zeros_and_infinities()
    {
        var fpu = NewFpu();
        fpu.WriteDoubleword(0, Dbl(-0.0));
        fpu.WriteDoubleword(1, Dbl(0.0));
        fpu.Execute(Op(D, Functions["c.eq"])).Should().BeTrue();
        fpu.Condition.Should().BeTrue();
        fpu.WriteDoubleword(0, Dbl(double.NegativeInfinity));
        fpu.Execute(Op(D, Functions["c.lt"])).Should().BeTrue();
        fpu.Condition.Should().BeTrue();
    }

    [Fact]
    public void With_fr_clear_odd_registers_are_the_high_halves_of_pairs()
    {
        var fpu = new R4300Fpu { Fr = false };
        fpu.WriteWord(0, 0x11111111);
        fpu.WriteWord(1, 0x22222222);
        fpu.ReadDoubleword(0).Should().Be(0x2222222211111111);
        fpu.ReadDoubleword(1).Should().Be(0x2222222211111111, "a 64-bit access to an odd register uses its pair");
        fpu.ReadWord(1).Should().Be(0x22222222u);

        fpu.WriteDoubleword(3, Dbl(2.5));
        fpu.ReadDoubleword(2).Should().Be(Dbl(2.5));

        // ADD.S f1, f1, f3 works on the high halves.
        fpu.WriteWord(1, F(1.5f));
        fpu.WriteWord(3, F(2));
        fpu.Execute(Op(S, 0, fd: 1, fs: 1, ft: 3)).Should().BeTrue();
        fpu.ReadWord(1).Should().Be(F(3.5f));
        fpu.ReadWord(0).Should().Be(0x11111111u);

        fpu.Fr = true;
        fpu.WriteWord(1, 0x33333333);
        fpu.ReadDoubleword(0).Should().Be(((ulong)F(3.5f) << 32) | 0x11111111, "with FR set the registers are separate");
        fpu.ReadWord(1).Should().Be(0x33333333u);
    }

    [Fact]
    public void Control_registers_and_ctc1_exceptions()
    {
        var fpu = NewFpu();
        fpu.ReadControl(0).Should().Be(0x00000A00u);
        fpu.WriteControl(31, 0xFFFFFFFF & ~CauseUnimplemented & ~EnableInvalid).Should().BeFalse("inexact is enabled and caused");
        fpu.ReadControl(31).Should().Be(0x0181F7FFu, "only FCR31's defined bits are kept");
        fpu.WriteControl(31, CauseInvalid).Should().BeTrue("invalid isn't enabled");
        fpu.WriteControl(31, CauseUnimplemented).Should().BeFalse("unimplemented operation always traps");
        fpu.WriteControl(31, 1u << 23).Should().BeTrue();
        fpu.Condition.Should().BeTrue();
        fpu.WriteControl(25, 0xFFFFFFFF).Should().BeTrue();
        fpu.ReadControl(25).Should().Be(0u);
    }

    [Fact]
    public void Reserved_formats_and_functions_are_unimplemented()
    {
        var fpu = NewFpu();
        fpu.Execute(Op(W, 0)).Should().BeFalse("ADD.W");
        fpu.Fcr31.Should().Be(CauseUnimplemented);
        fpu.Execute(Op(S, 32)).Should().BeFalse("CVT.S.S");
        fpu.Execute(Op(D, 33)).Should().BeFalse("CVT.D.D");
        fpu.Execute(Op(L, 36)).Should().BeFalse("CVT.W.L");
        fpu.Execute(Op(S, 0x1F)).Should().BeFalse();
        fpu.Execute(Op(S, Functions["c.eq"])).Should().BeTrue("the cause is cleared by the next instruction");
        (fpu.Fcr31 & Causes).Should().Be(0);

        fpu.Fcr31 = CauseInexact | FlagInexact;
        fpu.Unimplemented();
        fpu.Fcr31.Should().Be(CauseUnimplemented | FlagInexact);
    }

    [Fact]
    public void Narrowing_a_huge_double_overflows()
    {
        var fpu = NewFpu(RoundZero);
        fpu.WriteDoubleword(0, Dbl(1e300));
        fpu.Execute(Op(D, 32, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(F(float.MaxValue));
        fpu.Fcr31.Should().Be(RoundZero | CauseOverflow | CauseInexact | FlagOverflow | FlagInexact);
    }

    [Fact]
    public void Mov_copies_bits_without_checks()
    {
        var fpu = NewFpu();
        fpu.WriteWord(0, 0x7FC00000);
        fpu.Execute(Op(S, 6, fd: 2)).Should().BeTrue();
        fpu.ReadWord(2).Should().Be(0x7FC00000u);
        fpu.WriteDoubleword(0, 1);
        fpu.Execute(Op(D, 6, fd: 2)).Should().BeTrue();
        fpu.ReadDoubleword(2).Should().Be(1ul);
    }

    [Fact]
    public void State_round_trips()
    {
        var fpu = NewFpu(RoundMinus | Flush | (1u << 23));
        for (var i = 0; i < 32; i++)
        {
            fpu.WriteDoubleword(i, (ulong)i * 0x0101010101010101);
        }

        var writer = new StateWriter();
        fpu.SaveState(writer);
        var copy = new R4300Fpu();
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.Fr.Should().BeTrue();
        copy.Fcr31.Should().Be(fpu.Fcr31);
        for (var i = 0; i < 32; i++)
        {
            copy.ReadDoubleword(i).Should().Be(fpu.ReadDoubleword(i));
        }
    }
}
