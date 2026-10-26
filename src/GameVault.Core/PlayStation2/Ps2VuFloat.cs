using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The vector units' single-precision arithmetic, which isn't IEEE 754 (ps2tek: "VU Registers", MAC flags; the VU
/// User's Manual): every exponent from 1 to 255 is an ordinary number (there are no infinities or NaNs), exponent 0 is
/// zero whatever the mantissa (no denormals), results are truncated toward zero, a result too large saturates to ±max
/// (<c>0x7FFFFFFF</c>) and raises O, and one too small flushes to ±0 and raises U (and Z).
/// </summary>
/// <remarks>
/// Values are raw IEEE bit patterns held in <see cref="uint"/>s. A product of two 24-bit significands fits a double
/// exactly, as does the quotient or square root to within less than its distance to the nearest 24-bit boundary, so
/// those go through <see cref="double"/> and are truncated afterwards; the adder is a port of Play!'s truncating adder.
/// </remarks>
public static class Ps2VuFloat
{
    /// <summary>The largest magnitude: exponent 255, mantissa all ones.</summary>
    public const uint Max = 0x7FFFFFFF;

    public const uint SignBit = 0x80000000;

    /// <summary>An <c>ou</c> result: the operation overflowed and saturated.</summary>
    public const int Overflow = 1;

    /// <summary>An <c>ou</c> result: the operation underflowed and flushed to zero.</summary>
    public const int Underflow = 2;

    /// <summary>The exact value of a VU float (exponent 255 included; exponent 0 reads as a signed zero).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble(uint v)
    {
        var e = (v >> 23) & 0xFF;
        if (e == 0)
        {
            return (v & SignBit) != 0 ? -0.0 : 0.0;
        }

        var bits = ((ulong)(v & SignBit) << 32) | ((ulong)(e + 896) << 52) | ((ulong)(v & 0x7FFFFF) << 29);
        return BitConverter.UInt64BitsToDouble(bits);
    }

    /// <summary>A double truncated toward zero to a VU float, saturating or flushing out-of-range results.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromDouble(double d, out int ou)
    {
        var bits = BitConverter.DoubleToUInt64Bits(d);
        var sign = (uint)(bits >> 32) & SignBit;
        var e = (int)((bits >> 52) & 0x7FF);
        ou = 0;
        if (e == 0)
        {
            return sign;
        }

        e -= 896;
        if (e > 255)
        {
            ou = Overflow;
            return sign | Max;
        }

        if (e <= 0)
        {
            ou = Underflow;
            return sign;
        }

        return sign | ((uint)e << 23) | ((uint)(bits >> 29) & 0x7FFFFF);
    }

    /// <summary>Multiplication, truncated (the exact product's top 24 bits, as Play!'s <c>FpMulTruncate</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mul(uint a, uint b, out int ou)
    {
        if ((a & 0x7F800000) == 0 || (b & 0x7F800000) == 0)
        {
            ou = 0;
            return (a ^ b) & SignBit;
        }

        return FromDouble(ToDouble(a) * ToDouble(b), out ou);
    }

    /// <summary>
    /// Addition as the VU's adder does it: the smaller operand is aligned to the larger one keeping a single guard bit,
    /// and the exact sum of that is truncated (see <see cref="AddReference"/>). Dropping the alignment's lost bits from
    /// the smaller operand's mantissa first makes the sum exact in a double.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Add(uint a, uint b, out int ou)
    {
        var aAbs = a & Max;
        var bAbs = b & Max;
        if (bAbs > aAbs)
        {
            (a, b) = (b, a);
            (aAbs, bAbs) = (bAbs, aAbs);
        }

        if (bAbs < 0x00800000)
        {
            // b is zero (or a denormal, which is zero): a alone, or a signed zero.
            ou = 0;
            return aAbs < 0x00800000 ? a & b & SignBit : a;
        }

        var align = (int)(aAbs >> 23) - (int)(bAbs >> 23);
        if (align > 1)
        {
            if (align > 24)
            {
                ou = 0;
                return a;
            }

            b &= ~((1u << (align - 1)) - 1);
        }

        return FromDouble(ToDouble(a) + ToDouble(b), out ou);
    }

    /// <summary>
    /// Addition. Ported from Play! (BSD-2-Clause, Jean-Philip Desjardins): Source/ee/FpAddTruncate.cpp, itself from
    /// LLVM's addsf3: the smaller operand is aligned keeping a single guard bit, and the sum is truncated. Changed for
    /// the VU: exponent 255 is a number, exponent 0 is zero, and the result saturates or flushes. <see cref="Add"/>
    /// gives the same results faster; this is the reference the tests hold it to.
    /// </summary>
    public static uint AddReference(uint a, uint b, out int ou)
    {
        ou = 0;
        if ((a & 0x7F800000) == 0)
        {
            a &= SignBit;
        }

        if ((b & 0x7F800000) == 0)
        {
            b &= SignBit;
        }

        var aAbs = a & Max;
        var bAbs = b & Max;
        if (aAbs == 0)
        {
            return bAbs == 0 ? a & b : b;
        }

        if (bAbs == 0)
        {
            return a;
        }

        if (bAbs > aAbs)
        {
            (a, b) = (b, a);
        }

        var aExponent = (int)((a >> 23) & 0xFF);
        var bExponent = (int)((b >> 23) & 0xFF);
        var aSignificand = ((a & 0x7FFFFF) | 0x800000) << 3;
        var bSignificand = ((b & 0x7FFFFF) | 0x800000) << 3;
        var resultSign = a & SignBit;
        var subtraction = ((a ^ b) & SignBit) != 0;

        var align = aExponent - bExponent;
        if (align != 0)
        {
            // Only one guard bit survives the alignment.
            bSignificand = align < 32 ? (bSignificand >> align) & ~3u : 0;
        }

        if (subtraction)
        {
            aSignificand -= bSignificand;
            if (aSignificand == 0)
            {
                return 0;
            }

            if (aSignificand < 0x800000u << 3)
            {
                var shift = BitOperations.LeadingZeroCount(aSignificand) - BitOperations.LeadingZeroCount(0x800000u << 3);
                aSignificand <<= shift;
                aExponent -= shift;
            }
        }
        else
        {
            aSignificand += bSignificand;
            if ((aSignificand & (0x800000u << 4)) != 0)
            {
                aSignificand = (aSignificand >> 1) | (aSignificand & 1);
                aExponent++;
            }
        }

        if (aExponent > 255)
        {
            ou = Overflow;
            return resultSign | Max;
        }

        if (aExponent <= 0)
        {
            ou = Underflow;
            return resultSign;
        }

        return resultSign | ((uint)aExponent << 23) | ((aSignificand >> 3) & 0x7FFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sub(uint a, uint b, out int ou) => Add(a, b ^ SignBit, out ou);

    /// <summary>
    /// DIV: <paramref name="a"/> / <paramref name="b"/>, truncated. <paramref name="flags"/> gets the status bits I
    /// (0x10: 0/0) and D (0x20: division of a non-zero by zero); either gives ±max (VU User's Manual, DIV).
    /// </summary>
    public static uint Div(uint a, uint b, out int flags)
    {
        var sign = (a ^ b) & SignBit;
        var aZero = (a & 0x7F800000) == 0;
        if ((b & 0x7F800000) == 0)
        {
            flags = aZero ? 0x10 : 0x20;
            return sign | Max;
        }

        flags = 0;
        if (aZero)
        {
            return sign;
        }

        return FromDouble(ToDouble(a) / ToDouble(b), out _);
    }

    /// <summary>SQRT: √|b|, truncated; a negative operand raises I (0x10). D is always cleared.</summary>
    public static uint Sqrt(uint b, out int flags)
    {
        if ((b & 0x7F800000) == 0)
        {
            flags = 0;
            return 0;
        }

        flags = (b & SignBit) != 0 ? 0x10 : 0;
        return FromDouble(Math.Sqrt(ToDouble(b & Max)), out _);
    }

    /// <summary>RSQRT: <paramref name="a"/> / √|b|, truncated; b = 0 raises D (I if a is 0 too) and b &lt; 0 raises I.</summary>
    public static uint Rsqrt(uint a, uint b, out int flags)
    {
        var aZero = (a & 0x7F800000) == 0;
        if ((b & 0x7F800000) == 0)
        {
            flags = aZero ? 0x10 : 0x20;
            return (a & SignBit) | Max;
        }

        flags = (b & SignBit) != 0 ? 0x10 : 0;
        if (aZero)
        {
            return a & SignBit;
        }

        return FromDouble(ToDouble(a) / Math.Sqrt(ToDouble(b & Max)), out _);
    }

    /// <summary>FTOIn: the value × 2^<paramref name="fraction"/> truncated to a signed 32-bit integer, saturating.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToInt(uint v, int fraction)
    {
        var d = ToDouble(v) * (1 << fraction);
        if (d >= 2147483648.0)
        {
            return 0x7FFFFFFF;
        }

        if (d <= -2147483648.0)
        {
            return 0x80000000;
        }

        return (uint)(int)d;
    }

    /// <summary>ITOFn: a signed 32-bit integer ÷ 2^<paramref name="fraction"/>, truncated to 24 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromInt(uint v, int fraction) => FromDouble((int)v / (double)(1 << fraction), out _);

    // Four lanes at once: the same arithmetic as Mul and Add, lane for lane, on Vector128 (two doubles per half).

    private const ulong DoubleBias = 896UL << 52;

    /// <summary>The doubles of four VU magnitudes (exponent 0 gives garbage; callers mask those lanes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<double> MagnitudesToDouble(Vector128<ulong> magnitude, Vector128<ulong> sign) =>
        ((sign << 32) | ((magnitude << 29) + Vector128.Create(DoubleBias))).AsDouble();

    /// <summary>Four doubles truncated to VU floats; <paramref name="overflow"/>/<paramref name="underflow"/> are lane masks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> FromDouble4(
        Vector128<double> low, Vector128<double> high, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var lowBits = low.AsUInt64();
        var highBits = high.AsUInt64();
        var sign = Vector128.Narrow(lowBits >> 32, highBits >> 32) & Vector128.Create(SignBit);
        var exponent = (Vector128.Narrow(lowBits >> 52, highBits >> 52) & Vector128.Create(0x7FFu)).AsInt32();
        var mantissa = Vector128.Narrow(lowBits >> 29, highBits >> 29) & Vector128.Create(0x7FFFFFu);
        var zero = Vector128.Equals(exponent, Vector128<int>.Zero).AsUInt32();
        exponent -= Vector128.Create(896);
        overflow = Vector128.GreaterThan(exponent, Vector128.Create(255)).AsUInt32() & ~zero;
        underflow = Vector128.LessThanOrEqual(exponent, Vector128<int>.Zero).AsUInt32() & ~zero;
        var normal = (exponent.AsUInt32() << 23) | mantissa;
        var magnitude = Vector128.ConditionalSelect(overflow, Vector128.Create(Max), normal) & ~(underflow | zero);
        return sign | magnitude;
    }

    /// <summary><see cref="Mul"/> on four lanes.</summary>
    public static Vector128<uint> Mul4(Vector128<uint> a, Vector128<uint> b, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var exponentMask = Vector128.Create(0x7F800000u);
        var sign = (a ^ b) & Vector128.Create(SignBit);
        var zero = Vector128.Equals(a & exponentMask, Vector128<uint>.Zero) | Vector128.Equals(b & exponentMask, Vector128<uint>.Zero);
        var am = a & Vector128.Create(Max);
        var bm = b & Vector128.Create(Max);
        var none = Vector128<ulong>.Zero;
        var low = MagnitudesToDouble(Vector128.WidenLower(am), none) * MagnitudesToDouble(Vector128.WidenLower(bm), none);
        var high = MagnitudesToDouble(Vector128.WidenUpper(am), none) * MagnitudesToDouble(Vector128.WidenUpper(bm), none);
        var magnitude = FromDouble4(low, high, out overflow, out underflow);
        overflow &= ~zero;
        underflow &= ~zero;
        return sign | (magnitude & ~zero);
    }

    /// <summary><see cref="Add"/> on four lanes.</summary>
    public static Vector128<uint> Add4(Vector128<uint> a, Vector128<uint> b, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var max = Vector128.Create(Max);
        var swap = Vector128.GreaterThan(b & max, a & max);
        var big = Vector128.ConditionalSelect(swap, b, a);
        var small = Vector128.ConditionalSelect(swap, a, b);
        var bigAbs = big & max;
        var smallAbs = small & max;
        var smallest = Vector128.Create(0x00800000u);
        var smallZero = Vector128.LessThan(smallAbs, smallest);
        var bigZero = Vector128.LessThan(bigAbs, smallest);
        var align = (bigAbs >> 23).AsInt32() - (smallAbs >> 23).AsInt32();
        var far = Vector128.GreaterThan(align, Vector128.Create(24)).AsUInt32();

        // Drop the bits the alignment loses below the guard bit: clear the low (align − 1) mantissa bits, with
        // 2^(align − 1) made by the float conversion (there's no per-lane shift on every platform).
        var lost = Vector128.Min(Vector128.Max(align - Vector128<int>.One, Vector128<int>.Zero), Vector128.Create(23));
        var power = Vector128.ConvertToInt32(((lost + Vector128.Create(127)) << 23).AsSingle()).AsUInt32();
        small &= ~(power - Vector128<uint>.One);
        smallAbs = small & max;

        var bigSign = big & Vector128.Create(SignBit);
        var smallSign = small & Vector128.Create(SignBit);
        var low = MagnitudesToDouble(Vector128.WidenLower(bigAbs), Vector128.WidenLower(bigSign))
                  + MagnitudesToDouble(Vector128.WidenLower(smallAbs), Vector128.WidenLower(smallSign));
        var high = MagnitudesToDouble(Vector128.WidenUpper(bigAbs), Vector128.WidenUpper(bigSign))
                   + MagnitudesToDouble(Vector128.WidenUpper(smallAbs), Vector128.WidenUpper(smallSign));
        var sum = FromDouble4(low, high, out overflow, out underflow);

        // b = 0 (or denormal): a alone, or a signed zero when both are; b too small to reach a's guard bit: a.
        var passThrough = smallZero | far;
        var zeroResult = Vector128.ConditionalSelect(bigZero, a & b & Vector128.Create(SignBit), big);
        overflow &= ~passThrough;
        underflow &= ~passThrough;
        return Vector128.ConditionalSelect(passThrough, Vector128.ConditionalSelect(smallZero, zeroResult, big), sum);
    }

    /// <summary>The MAC flags of four results (only the lanes of <paramref name="dest"/>, x = 8 … w = 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MacBits4(Vector128<uint> result, Vector128<uint> overflow, Vector128<uint> underflow, int dest)
    {
        var zero = Vector128.Equals(result & Vector128.Create(Max), Vector128<uint>.Zero);
        var z = (int)zero.ExtractMostSignificantBits();
        var s = (int)result.ExtractMostSignificantBits();
        var u = (int)underflow.ExtractMostSignificantBits();
        var o = (int)overflow.ExtractMostSignificantBits();
        var reversed = s_reverse4[z] | (s_reverse4[s] << 4) | (s_reverse4[u] << 8) | (s_reverse4[o] << 12);
        return reversed & (dest * 0x1111);
    }

    // ExtractMostSignificantBits puts lane x in bit 0; the MAC register has x in the field's top bit.
    private static readonly int[] s_reverse4 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];

    /// <summary>
    /// An ordering key for MAX, MINI and CLIP: sign-magnitude turned into two's complement, so −0 sorts just below +0
    /// and exponent 255 above everything else.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int OrderKey(uint v) => (int)v >= 0 ? (int)v : (int)(v ^ Max);

    /// <summary>A lane's four MAC bits (Z, S, U, O) placed for <paramref name="lane"/> (0 = x … 3 = w).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MacBits(uint result, int ou, int lane)
    {
        var shift = 3 - lane;
        var bits = (result & Max) == 0 ? 1 : 0;
        bits |= (int)(result >> 31) << 4;
        bits |= (ou & Underflow) << 7;
        bits |= (ou & Overflow) << 12;
        return bits << shift;
    }
}
