namespace GameVault.Core.PlayStation2;

/// <summary>
/// VU1's elementary function unit (ps2tek: "EFU Pipeline"), in the VU's truncating arithmetic. The series and their
/// constants are Play!'s (BSD-2-Clause, Jean-Philip Desjardins): Source/ee/MA_VU_Lower.cpp (ESIN, EATAN, EEXP). The
/// hardware's own algorithms aren't documented bit for bit, so the series' last bits can differ from it.
/// </summary>
public static class Ps2VuEfu
{
    private const uint One = 0x3F800000;
    private const uint PiOver4 = 0x3F490FDA; // ps2autotests efu: EATAN(1) = $3F490FDA

    private static readonly uint[] s_sinSeries = [0x3F800000, 0xBE2AAAA4, 0x3C08873E, 0xB94FB21F, 0x362E9C14];

    private static readonly uint[] s_atanSeries =
        [0x3F7FFFF5, 0xBEAAA61C, 0x3E4C40A6, 0xBE0E6C63, 0x3DC577DF, 0xBD6501C4, 0x3CB31652, 0xBB84D7E7];

    private static readonly uint[] s_expSeries = [0x3E7FFFA8, 0x3D0007F4, 0x3B29D3FF, 0x3933E553, 0x36B63510, 0x353961AC];

    /// <summary>ESADD: x² + y² + z².</summary>
    public static uint SquareSum(uint x, uint y, uint z)
    {
        var sum = Ps2VuFloat.Add(Ps2VuFloat.Mul(x, x, out _), Ps2VuFloat.Mul(y, y, out _), out _);
        return Ps2VuFloat.Add(sum, Ps2VuFloat.Mul(z, z, out _), out _);
    }

    /// <summary>ESUM: x + y + z + w.</summary>
    public static uint Sum(uint x, uint y, uint z, uint w) =>
        Ps2VuFloat.Add(Ps2VuFloat.Add(Ps2VuFloat.Add(x, y, out _), z, out _), w, out _);

    /// <summary>ERCPR: 1 / v, rounded to nearest; 1 / 0 gives ±max.</summary>
    public static uint Reciprocal(uint v)
    {
        if ((v & 0x7F800000) == 0)
        {
            return (v & Ps2VuFloat.SignBit) | Ps2VuFloat.Max;
        }

        return FromDoubleNearest(1.0 / Ps2VuFloat.ToDouble(v));
    }

    /// <summary>ESQRT: √|v|, rounded to nearest.</summary>
    public static uint Sqrt(uint v) => FromDoubleNearest(Math.Sqrt(Ps2VuFloat.ToDouble(v & Ps2VuFloat.Max)));

    /// <summary>ERSQRT: 1 / √|v|, as the rounded reciprocal of the rounded root (ps2autotests efu: 1/√$7F800001 = $1F800000).</summary>
    public static uint ReciprocalSqrt(uint v) => Reciprocal(Sqrt(v));

    /// <summary>
    /// The EFU's square roots and reciprocals round to nearest, unlike the FMAC (ps2autotests efu: ERCPR(3) = $3EAAAAAB).
    /// </summary>
    private static uint FromDoubleNearest(double d)
    {
        var bits = BitConverter.DoubleToUInt64Bits(d);
        var sign = (uint)(bits >> 32) & Ps2VuFloat.SignBit;
        var e = (int)((bits >> 52) & 0x7FF);
        if (e == 0)
        {
            return sign;
        }

        var mantissa = (bits & 0xFFFFFFFFFFFFFul) | (1ul << 52);
        var kept = mantissa >> 29;
        var rest = mantissa & 0x1FFFFFFF;
        if (rest > 0x10000000 || (rest == 0x10000000 && (kept & 1) != 0))
        {
            kept++;
            if (kept == 1ul << 24)
            {
                kept >>= 1;
                e++;
            }
        }

        e -= 896;
        if (e > 255)
        {
            return sign | Ps2VuFloat.Max;
        }

        return e <= 0 ? sign : sign | ((uint)e << 23) | ((uint)kept & 0x7FFFFF);
    }

    /// <summary>ESIN: sin(x) by its odd series to x⁹.</summary>
    public static uint Sin(uint x) => OddSeries(x, s_sinSeries);

    /// <summary>EATAN (b = 1), EATANxy and EATANxz: atan(a / b) as π/4 + the series in t = (a − b) / (a + b).</summary>
    public static uint ArcTanRatio(uint a, uint b)
    {
        var t = Ps2VuFloat.Div(Ps2VuFloat.Sub(a, b, out _), Ps2VuFloat.Add(a, b, out _), out _);
        return Ps2VuFloat.Add(OddSeries(t, s_atanSeries), PiOver4, out _);
    }

    /// <summary>EEXP: e^−x as 1 / (1 + c₁x + … + c₆x⁶)⁴.</summary>
    public static uint Exp(uint x)
    {
        var sum = 0u;
        var power = One;
        for (var n = 0; n < s_expSeries.Length; n++)
        {
            power = Ps2VuFloat.Mul(power, x, out _);
            sum = Ps2VuFloat.Add(sum, Ps2VuFloat.Mul(power, s_expSeries[n], out _), out _);
        }

        sum = Ps2VuFloat.Add(sum, One, out _);
        var square = Ps2VuFloat.Mul(sum, sum, out _);
        return Ps2VuFloat.Div(One, Ps2VuFloat.Mul(square, square, out _), out _);
    }

    private static uint OddSeries(uint x, uint[] constants)
    {
        var square = Ps2VuFloat.Mul(x, x, out _);
        var power = x;
        var sum = 0u;
        for (var n = 0; n < constants.Length; n++)
        {
            sum = Ps2VuFloat.Add(sum, Ps2VuFloat.Mul(power, constants[n], out _), out _);
            power = Ps2VuFloat.Mul(power, square, out _);
        }

        return sum;
    }
}
