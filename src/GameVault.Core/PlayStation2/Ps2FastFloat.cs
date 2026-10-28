using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using GameVault.Core.Cpu.R5900;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The PS2's single-precision add and multiply done in the host's own float arithmetic, clamped to the PS2's rules
/// (ps2-full-speed.md §2): the fast alternative to <see cref="Ps2VuFloat"/> and the EE's <c>Ps2Float</c>, used when a
/// console's <c>FastFloat</c> is on. On a given PC it is as repeatable as the exact maths.
/// </summary>
/// <remarks>
/// <para>Inputs: exponent 255 (an ordinary number on the PS2, infinity or NaN to the host) is clamped to the host's
/// largest float, and exponent 0 (zero on the PS2 whatever the mantissa) to a signed zero. Results: one that overflows
/// to infinity saturates to ±<see cref="Ps2VuFloat.Max"/> with O, as the PS2's does; a denormal result flushes to a
/// signed zero with U.</para>
/// <para>What differs from the hardware: results round to nearest where the PS2 truncates (a difference in the last
/// bit), and numbers with exponent 255 are read as the host's largest float. This is how PCSX2's default clamping
/// treats the PS2's floats.</para>
/// </remarks>
public static class Ps2FastFloat
{
    private const uint SignBit = 0x80000000;
    private const uint ExponentMask = 0x7F800000;
    private const uint HostMax = 0x7F7FFFFF;

    // ---- Four lanes (the VUs) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> ClampIn4(Vector128<uint> v)
    {
        var exponent = v & Vector128.Create(ExponentMask);
        var sign = v & Vector128.Create(SignBit);
        var top = Vector128.Equals(exponent, Vector128.Create(ExponentMask));
        var zero = Vector128.Equals(exponent, Vector128<uint>.Zero);
        v = Vector128.ConditionalSelect(top, sign | Vector128.Create(HostMax), v);
        return Vector128.ConditionalSelect(zero, sign, v).AsSingle();
    }

    /// <summary>
    /// Saturates an overflow and flushes an underflow. <paramref name="tiny"/> marks the lanes whose result is below the
    /// smallest normal number for a reason other than an exact zero (see <see cref="Add4"/> and <see cref="Mul4"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ClampOut4(Vector128<uint> r, Vector128<uint> tiny, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var exponent = r & Vector128.Create(ExponentMask);
        var sign = r & Vector128.Create(SignBit);
        overflow = Vector128.Equals(exponent, Vector128.Create(ExponentMask));
        underflow = Vector128.Equals(exponent, Vector128<uint>.Zero) & tiny;
        r = Vector128.ConditionalSelect(overflow, sign | Vector128.Create(Ps2VuFloat.Max), r);
        return Vector128.ConditionalSelect(Vector128.Equals(exponent, Vector128<uint>.Zero), sign, r);
    }

    /// <summary>
    /// a + b on four lanes, with <paramref name="overflow"/>/<paramref name="underflow"/> as lane masks. A sum below the
    /// smallest normal is exact on the host (a denormal); a sum of exactly zero is cancellation, not an underflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Add4(Vector128<uint> a, Vector128<uint> b, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var r = (ClampIn4(a) + ClampIn4(b)).AsUInt32();
        var denormal = ~Vector128.Equals(r & Vector128.Create(0x007FFFFFu), Vector128<uint>.Zero);
        return ClampOut4(r, denormal, out overflow, out underflow);
    }

    /// <summary>a × b on four lanes. A product of two non-zero numbers that is zero or a denormal underflowed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Mul4(Vector128<uint> a, Vector128<uint> b, out Vector128<uint> overflow, out Vector128<uint> underflow)
    {
        var x = ClampIn4(a);
        var y = ClampIn4(b);
        var magnitude = Vector128.Create(~SignBit);
        var nonZero = ~(Vector128.Equals(x.AsUInt32() & magnitude, Vector128<uint>.Zero) | Vector128.Equals(y.AsUInt32() & magnitude, Vector128<uint>.Zero));
        return ClampOut4((x * y).AsUInt32(), nonZero, out overflow, out underflow);
    }

    // ---- One lane ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ClampIn(uint v)
    {
        var exponent = v & ExponentMask;
        if (exponent == ExponentMask)
        {
            v = (v & SignBit) | HostMax;
        }
        else if (exponent == 0)
        {
            v &= SignBit;
        }

        return BitConverter.UInt32BitsToSingle(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ClampOut(uint r, bool tiny, out int ou)
    {
        var exponent = r & ExponentMask;
        ou = 0;
        if (exponent == ExponentMask)
        {
            ou = Ps2VuFloat.Overflow;
            return (r & SignBit) | Ps2VuFloat.Max;
        }

        if (exponent == 0)
        {
            ou = tiny ? Ps2VuFloat.Underflow : 0;
            return r & SignBit;
        }

        return r;
    }

    /// <summary>a + b, with <paramref name="ou"/> as <see cref="Ps2VuFloat.Add"/> reports it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Add(uint a, uint b, out int ou)
    {
        var r = BitConverter.SingleToUInt32Bits(ClampIn(a) + ClampIn(b));
        return ClampOut(r, (r & 0x007FFFFF) != 0, out ou);
    }

    public static uint Sub(uint a, uint b, out int ou) => Add(a, b ^ SignBit, out ou);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mul(uint a, uint b, out int ou)
    {
        var x = ClampIn(a);
        var y = ClampIn(b);
        return ClampOut(BitConverter.SingleToUInt32Bits(x * y), x != 0 && y != 0, out ou);
    }

    /// <summary>The EE FPU's form of <see cref="Add"/>.</summary>
    internal static FloatResult AddResult(uint a, uint b)
    {
        var value = Add(a, b, out var ou);
        return new FloatResult(value, ou == Ps2VuFloat.Overflow, ou == Ps2VuFloat.Underflow);
    }

    /// <summary>The EE FPU's form of <see cref="Mul"/>.</summary>
    internal static FloatResult MultiplyResult(uint a, uint b)
    {
        var value = Mul(a, b, out var ou);
        return new FloatResult(value, ou == Ps2VuFloat.Overflow, ou == Ps2VuFloat.Underflow);
    }
}
