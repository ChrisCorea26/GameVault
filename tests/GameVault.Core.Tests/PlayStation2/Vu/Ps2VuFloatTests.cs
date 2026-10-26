using System.Runtime.Intrinsics;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>The VU's non-IEEE arithmetic: truncation, exponent 255 as a number, saturation and flushing.</summary>
public class Ps2VuFloatTests
{
    [Theory]
    [InlineData(0x3F800005u, 0x3FC00000u, 0x3FC00007u)] // 1.5 + 7.5 ulp: truncated (IEEE rounds to 8)
    [InlineData(0x3F800000u, 0x3F800000u, 0x3F800000u)]
    [InlineData(0x7F800000u, 0x3F000000u, 0x7F000000u)] // "Infinity" is 2^128 here
    [InlineData(0xBF800000u, 0x00000000u, 0x80000000u)] // −1 × 0 = −0
    [InlineData(0x00400000u, 0x7F000000u, 0x00000000u)] // A denormal is zero
    public void Mul_Truncates(uint a, uint b, uint expected)
    {
        Assert.Equal(expected, Ps2VuFloat.Mul(a, b, out var ou));
        Assert.Equal(0, ou);
    }

    [Fact]
    public void Mul_SaturatesAndFlushes()
    {
        Assert.Equal(0x7FFFFFFFu, Ps2VuFloat.Mul(0x7F000000, 0x7F000000, out var ou));
        Assert.Equal(Ps2VuFloat.Overflow, ou);
        Assert.Equal(0xFFFFFFFFu, Ps2VuFloat.Mul(0xFF000000, 0x7F000000, out ou));
        Assert.Equal(Ps2VuFloat.Overflow, ou);
        Assert.Equal(0x80000000u, Ps2VuFloat.Mul(0x80800000, 0x00800000, out ou));
        Assert.Equal(Ps2VuFloat.Underflow, ou);
    }

    [Theory]
    [InlineData(0x3F800000u, 0x33C00000u, 0x3F800000u)] // 1 + 0.75 ulp truncates to 1 (IEEE: 1 + 1 ulp)
    [InlineData(0xBF800000u, 0x3F800000u, 0x00000000u)] // x − x = +0
    [InlineData(0x80000000u, 0x80000000u, 0x80000000u)] // −0 + −0 = −0
    [InlineData(0x80000000u, 0x00000000u, 0x00000000u)]
    [InlineData(0x00000001u, 0x3F800000u, 0x3F800000u)] // denormal + 1
    [InlineData(0x7F7FFFFFu, 0x7F7FFFFFu, 0x7FFFFFFFu)] // exponent 255 is still in range
    [InlineData(0xFFFF8000u, 0x00000000u, 0xFFFF8000u)] // x + 0 keeps x's bits
    public void Add_Truncates(uint a, uint b, uint expected)
    {
        Assert.Equal(expected, Ps2VuFloat.Add(a, b, out var ou));
        Assert.Equal(0, ou);
    }

    [Fact]
    public void Add_SaturatesAndFlushes()
    {
        Assert.Equal(0x7FFFFFFFu, Ps2VuFloat.Add(0x7FFFFFFF, 0x7FFFFFFF, out var ou));
        Assert.Equal(Ps2VuFloat.Overflow, ou);
        Assert.Equal(0x00000000u, Ps2VuFloat.Sub(0x00C00000, 0x00800001, out ou));
        Assert.Equal(Ps2VuFloat.Underflow, ou);
    }

    [Fact]
    public void Div_TruncatesAndFlagsDivisionByZero()
    {
        Assert.Equal(0x3EAAAAAAu, Ps2VuFloat.Div(0x3F800000, 0x40400000, out var flags));
        Assert.Equal(0, flags);
        Assert.Equal(0x7FFFFFFFu, Ps2VuFloat.Div(0x3F800000, 0x00000000, out flags));
        Assert.Equal(0x20, flags); // D
        Assert.Equal(0xFFFFFFFFu, Ps2VuFloat.Div(0xBF800000, 0x00000000, out flags));
        Assert.Equal(0x20, flags);
        Ps2VuFloat.Div(0, 0, out flags);
        Assert.Equal(0x10, flags); // I
    }

    [Fact]
    public void Sqrt_OfNegativeUsesTheMagnitudeAndFlagsInvalid()
    {
        Assert.Equal(0x40000000u, Ps2VuFloat.Sqrt(0xC0800000, out var flags));
        Assert.Equal(0x10, flags);
        Assert.Equal(0x3FB504F3u, Ps2VuFloat.Sqrt(0x40000000, out flags)); // √2 = 1.41421354…, truncated
        Assert.Equal(0x3FB504F2u, Ps2VuFloat.Sqrt(0x3FFFFFFF, out flags)); // 0.85 of a unit, truncated
        Assert.Equal(0, flags);
        Assert.Equal(0x3F000000u, Ps2VuFloat.Rsqrt(0x3F800000, 0x40800000, out flags));
        Assert.Equal(0x7FFFFFFFu, Ps2VuFloat.Rsqrt(0x3F800000, 0, out flags));
        Assert.Equal(0x20, flags);
    }

    [Theory]
    [InlineData(0x4F000000u, 0, 0x7FFFFFFFu)] // 2^31 saturates
    [InlineData(0xCF800000u, 0, 0x80000000u)]
    [InlineData(0x7FFFFFFFu, 15, 0x7FFFFFFFu)]
    [InlineData(0x3FC00000u, 4, 24u)] // 1.5 × 16
    [InlineData(0xBFC00000u, 0, 0xFFFFFFFFu)] // −1.5 truncates to −1
    [InlineData(0x3F7FFFFFu, 12, 4095u)]
    public void ToInt_TruncatesAndSaturates(uint value, int fraction, uint expected) =>
        Assert.Equal(expected, Ps2VuFloat.ToInt(value, fraction));

    [Theory]
    [InlineData(1u, 4, 0x3D800000u)]
    [InlineData(0x7FFFFFFFu, 0, 0x4EFFFFFFu)] // truncated, where IEEE rounds up to 2^31
    [InlineData(0xFFFFFFF0u, 4, 0xBF800000u)]
    [InlineData(0x8000u, 15, 0x3F800000u)]
    public void FromInt_Truncates(uint value, int fraction, uint expected) =>
        Assert.Equal(expected, Ps2VuFloat.FromInt(value, fraction));

    [Fact]
    public void MacBits_PlaceEachFlagForItsField()
    {
        Assert.Equal(0x0008, Ps2VuFloat.MacBits(0, 0, 0)); // Zx
        Assert.Equal(0x0011, Ps2VuFloat.MacBits(0x80000000, 0, 3)); // Sw, Zw
        Assert.Equal(0x4040, Ps2VuFloat.MacBits(0xFFFFFFFF, Ps2VuFloat.Overflow, 1)); // Oy, Sy
        Assert.Equal(0x0202, Ps2VuFloat.MacBits(0, Ps2VuFloat.Underflow, 2)); // Uz, Zz
    }

    [Fact]
    public void Add_MatchesThePortedAdderOnRandomOperands()
    {
        var random = new Random(17);
        for (var n = 0; n < 2_000_000; n++)
        {
            var a = (uint)random.NextInt64(0, 1L << 32);
            var b = (uint)random.NextInt64(0, 1L << 32);
            if ((n & 3) == 0)
            {
                // Nearby exponents, where cancellation and the guard bit matter.
                b = (b & 0x80FFFFFF) | ((uint)(((a >> 24) & 0x7F) + random.Next(-1, 2)) << 24 & 0x7F000000);
            }
            else if ((n & 7) == 1)
            {
                b = a ^ 0x80000000 ^ (uint)random.Next(4);
            }

            var expected = Ps2VuFloat.AddReference(a, b, out var expectedOu);
            var actual = Ps2VuFloat.Add(a, b, out var actualOu);
            Assert.True(expected == actual && expectedOu == actualOu, $"{a:x8} + {b:x8}: {actual:x8}/{actualOu}, expected {expected:x8}/{expectedOu}");
        }
    }

    [Fact]
    public void FourLaneForms_MatchTheScalarOnesLaneForLane()
    {
        var random = new Random(23);
        uint Operand(uint other)
        {
            var v = (uint)random.NextInt64(0, 1L << 32);
            return random.Next(8) switch
            {
                0 => v & 0x807FFFFF, // zero or denormal
                1 => other ^ 0x80000000, // cancellation
                2 => (v & 0x80FFFFFF) | (other & 0x7F000000), // nearby exponent
                3 => v | 0x7F800000, // exponent 255
                _ => v,
            };
        }

        Span<uint> a = stackalloc uint[4];
        Span<uint> b = stackalloc uint[4];
        for (var n = 0; n < 500_000; n++)
        {
            for (var lane = 0; lane < 4; lane++)
            {
                a[lane] = (uint)random.NextInt64(0, 1L << 32);
                b[lane] = Operand(a[lane]);
            }

            var va = Vector128.Create(a[0], a[1], a[2], a[3]);
            var vb = Vector128.Create(b[0], b[1], b[2], b[3]);
            var sum = Ps2VuFloat.Add4(va, vb, out var sumOverflow, out var sumUnderflow);
            var product = Ps2VuFloat.Mul4(va, vb, out var productOverflow, out var productUnderflow);
            for (var lane = 0; lane < 4; lane++)
            {
                var expectedSum = Ps2VuFloat.Add(a[lane], b[lane], out var sumOu);
                Assert.True(expectedSum == sum[lane], $"{a[lane]:x8} + {b[lane]:x8}: {sum[lane]:x8}, expected {expectedSum:x8}");
                Assert.Equal(sumOu == Ps2VuFloat.Overflow, sumOverflow[lane] != 0);
                Assert.Equal(sumOu == Ps2VuFloat.Underflow, sumUnderflow[lane] != 0);
                var expectedProduct = Ps2VuFloat.Mul(a[lane], b[lane], out var productOu);
                Assert.True(expectedProduct == product[lane], $"{a[lane]:x8} × {b[lane]:x8}: {product[lane]:x8}, expected {expectedProduct:x8}");
                Assert.Equal(productOu == Ps2VuFloat.Overflow, productOverflow[lane] != 0);
                Assert.Equal(productOu == Ps2VuFloat.Underflow, productUnderflow[lane] != 0);
            }

            var mac = 0;
            for (var lane = 0; lane < 4; lane++)
            {
                Ps2VuFloat.Add(a[lane], b[lane], out var ou);
                mac |= Ps2VuFloat.MacBits(sum[lane], ou, lane);
            }

            Assert.Equal(mac, Ps2VuFloat.MacBits4(sum, sumOverflow, sumUnderflow, 0xF));
        }
    }
}
