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
}
