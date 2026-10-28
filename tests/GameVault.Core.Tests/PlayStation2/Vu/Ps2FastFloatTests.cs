using System.Runtime.Intrinsics;
using FluentAssertions;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.PlayStation2.R5900;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>
/// The fast float maths (ps2-full-speed.md §2): the host's arithmetic clamped to the PS2's rules. Ordinary results are
/// the exact maths' or one away in the last bit; the PS2's extremes are clamped as the plan says; the flags agree.
/// </summary>
public sealed class Ps2FastFloatTests
{
    private const uint Max = Ps2VuFloat.Max;
    private const uint Sign = Ps2VuFloat.SignBit;

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    /// <summary>A PS2 float with an exponent from 30 to 224 (well inside the host's range either way).</summary>
    private static uint Ordinary(Random random) =>
        (uint)random.Next(2) << 31 | (uint)random.Next(30, 225) << 23 | (uint)random.Next(1 << 23);

    /// <summary>How far apart two results of the same sign are, in last bits.</summary>
    private static long Ulps(uint a, uint b) => Math.Abs((long)(a & ~Sign) - (b & ~Sign));

    [Fact]
    public void Ordinary_sums_and_products_are_the_exact_ones_or_a_last_bit_away()
    {
        var random = new Random(7);
        var same = 0;
        for (var i = 0; i < 20000; i++)
        {
            var a = Ordinary(random);
            var b = Ordinary(random);

            var product = Ps2FastFloat.Mul(a, b, out var fastOu);
            var exactProduct = Ps2VuFloat.Mul(a, b, out var exactOu);
            if ((exactProduct & 0x7F800000) == 0x7F800000 && exactOu == 0)
            {
                // The PS2's top binade, past the host's range: it saturates with O (the plan's one deviation in range).
                (product & ~Sign).Should().Be(Max);
                fastOu.Should().Be(Ps2VuFloat.Overflow);
                continue;
            }

            fastOu.Should().Be(exactOu);
            (product & Sign).Should().Be(exactProduct & Sign);
            Ulps(product, exactProduct).Should().BeLessThanOrEqualTo(1, $"{a:X8} × {b:X8}");

            var sum = Ps2FastFloat.Add(a, b, out fastOu);
            var exactSum = Ps2VuFloat.Add(a, b, out exactOu);
            if ((exactSum & 0x7F800000) == 0x7F800000)
            {
                continue; // as above
            }

            fastOu.Should().Be(exactOu);
            if ((exactSum & ~Sign) != 0)
            {
                (sum & Sign).Should().Be(exactSum & Sign);
                var scale = (int)(Math.Max(a & 0x7F800000, b & 0x7F800000) >> 23);
                var resultScale = (int)((exactSum & 0x7F800000) >> 23);

                // Cancellation brings the operands' last bits up: the sum is one away at the operands' scale.
                Ulps(sum, exactSum).Should().BeLessThanOrEqualTo(1L << Math.Max(0, scale - resultScale), $"{a:X8} + {b:X8}");
            }

            same += product == exactProduct ? 1 : 0;
        }

        same.Should().BeGreaterThan(5000, "most products are exact anyway");
    }

    [Fact]
    public void Exponent_255_reads_as_the_hosts_largest_float_and_exponent_0_as_zero()
    {
        // 0x7F800000 is infinity to the host but 2^128 on the PS2: it must not make an infinity or a NaN.
        Ps2FastFloat.Mul(0x7F800000, F(0.5f), out var ou).Should().Be(F(float.MaxValue * 0.5f));
        ou.Should().Be(0);
        Ps2FastFloat.Add(0x7FC00000, 0xFFC00000, out ou).Should().Be(0, "+max + −max is +0, not a NaN");
        ou.Should().Be(0);

        // A denormal pattern is zero on the PS2 (with its sign).
        Ps2FastFloat.Add(0x00000001, 0x00000001, out ou).Should().Be(0);
        Ps2FastFloat.Mul(0x80000005, F(3f), out _).Should().Be(Sign);
        Ps2FastFloat.Mul(0x00400000, F(float.MaxValue), out _).Should().Be(0);
    }

    [Fact]
    public void Overflow_saturates_with_o_and_a_denormal_result_flushes_with_u()
    {
        Ps2FastFloat.Mul(F(float.MaxValue), F(4f), out var ou).Should().Be(Max);
        ou.Should().Be(Ps2VuFloat.Overflow);
        Ps2FastFloat.Mul(F(-float.MaxValue), F(4f), out ou).Should().Be(Sign | Max);
        ou.Should().Be(Ps2VuFloat.Overflow);
        Ps2FastFloat.Add(Max, Max, out ou).Should().Be(Max, "the PS2's largest number, doubled, saturates");
        ou.Should().Be(Ps2VuFloat.Overflow);

        Ps2FastFloat.Mul(F(1e-20f), F(-1e-20f), out ou).Should().Be(Sign);
        ou.Should().Be(Ps2VuFloat.Underflow);
        Ps2FastFloat.Add(F(1.5e-38f), F(-1.4e-38f), out ou).Should().Be(0);
        ou.Should().Be(Ps2VuFloat.Underflow);

        Ps2FastFloat.Add(F(1f), F(-1f), out ou).Should().Be(0, "an exact zero is no underflow");
        ou.Should().Be(0);
    }

    [Fact]
    public void Four_lanes_agree_with_one()
    {
        var random = new Random(3);
        uint[] specials = [0, Sign, Max, Sign | Max, 0x7F800000, 0x00000001, F(float.MaxValue), F(1e-20f)];
        for (var i = 0; i < 2000; i++)
        {
            uint Pick() => random.Next(4) == 0 ? specials[random.Next(specials.Length)] : Ordinary(random);
            var a = Vector128.Create(Pick(), Pick(), Pick(), Pick());
            var b = Vector128.Create(Pick(), Pick(), Pick(), Pick());

            var sum = Ps2FastFloat.Add4(a, b, out var overflow, out var underflow);
            var product = Ps2FastFloat.Mul4(a, b, out var mulOverflow, out var mulUnderflow);
            for (var lane = 0; lane < 4; lane++)
            {
                sum[lane].Should().Be(Ps2FastFloat.Add(a[lane], b[lane], out var ou));
                (overflow[lane] != 0).Should().Be(ou == Ps2VuFloat.Overflow);
                (underflow[lane] != 0).Should().Be(ou == Ps2VuFloat.Underflow);

                product[lane].Should().Be(Ps2FastFloat.Mul(a[lane], b[lane], out ou));
                (mulOverflow[lane] != 0).Should().Be(ou == Ps2VuFloat.Overflow);
                (mulUnderflow[lane] != 0).Should().Be(ou == Ps2VuFloat.Underflow);
            }
        }
    }

    [Fact]
    public void The_ee_fpu_uses_it_when_the_switch_is_on()
    {
        // ADD.S f3 = f1 + f2 and MUL.S f4 = f1 × f2, with operands whose exact and fast results differ in the last bit.
        var random = new Random(11);
        (uint A, uint B) operands;
        do
        {
            operands = (Ordinary(random), Ordinary(random));
        }
        while (Ps2FastFloat.Mul(operands.A, operands.B, out _) == Ps2Float.Multiply(operands.A, operands.B).Value);

        foreach (var fast in new[] { false, true })
        {
            var machine = new R5900TestMachine();
            machine.Cpu.FastFloat = fast;
            machine.Cpu.SetFpr(1, operands.A);
            machine.Cpu.SetFpr(2, operands.B);
            machine.Cpu.SetFpr(5, F(float.MaxValue));
            machine.Run(Ee.FpuS(0x00, 3, 1, 2), Ee.FpuS(0x02, 4, 1, 2), Ee.FpuS(0x02, 6, 5, 5));

            var expected = fast ? Ps2FastFloat.Mul(operands.A, operands.B, out _) : Ps2Float.Multiply(operands.A, operands.B).Value;
            machine.Cpu.GetFpr(4).Should().Be(expected);
            machine.Cpu.GetFpr(3).Should().Be(fast ? Ps2FastFloat.Add(operands.A, operands.B, out _) : Ps2Float.Add(operands.A, operands.B).Value);
            machine.Cpu.GetFpr(6).Should().Be(Max, "an overflow saturates either way");
            (machine.Cpu.Fcr31 & 0x8000).Should().Be(0x8000u, "and sets O");
        }
    }

    [Fact]
    public void The_console_switch_reaches_the_ee_and_both_vus()
    {
        var console = new Ps2Console(Ps2Boot.FromElf(File.ReadAllBytes(Homebrew.Ps2HomebrewRunner.ElfPath("gs-basic")), "host:gs-basic.elf"));
        console.FastFloat.Should().BeFalse("exact unless asked");
        console.Vu0.FastFloat.Should().BeFalse();

        console.FastFloat = true;
        console.Cpu.FastFloat.Should().BeTrue();
        console.Vu0.FastFloat.Should().BeTrue();
        console.Vu1.FastFloat.Should().BeTrue();
    }
}
