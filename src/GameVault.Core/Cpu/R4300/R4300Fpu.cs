using System.Numerics;
using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R4300;

/// <summary>
/// The VR4300's floating-point unit, coprocessor 1 (SPEC §7.11, Phase 16): 32 registers of 64 bits (or 16 pairs of
/// 32-bit halves with Status.FR clear), FCR0 and FCR31, and the S, D, W and L instruction formats. The CPU decodes
/// MFC1/MTC1/CFC1/CTC1, the BC1 branches and the COP1 loads and stores, and hands the computational instructions to
/// <see cref="Execute"/>. Deterministic: every result is computed in managed IEEE arithmetic and rounded exactly in
/// FCR31's rounding mode.
/// </summary>
/// <remarks>
/// References: n64brew "COP1"; the VR4300 User's Manual, chapter 7 ("Floating-Point Exceptions"). VR4300 specifics:
/// <list type="bullet">
/// <item>Denormal operands, and NaNs with the MIPS signalling bit (the fraction's top bit) set, raise the
/// unimplemented-operation exception; quiet NaN operands raise invalid operation.</item>
/// <item>A result that would be denormal (or underflows to zero) raises unimplemented operation, unless FS is set
/// and the underflow and inexact enables are clear: then it is flushed by the rounding mode, with underflow and
/// inexact.</item>
/// <item>Conversions to integers raise unimplemented operation for NaN, infinite and denormal operands, for results
/// outside 32 bits (.W) and for operands of 2^53 and beyond (.L); CVT.S/D.L for operands of 2^55 and beyond.</item>
/// <item>Invalid operations produce the MIPS default NaN: 0x7FBFFFFF (single) and 0x7FF7FFFFFFFFFFFF (double).</item>
/// </list>
/// .NET always rounds to nearest-even. Every operation therefore computes the nearest double result and the sign of
/// its exact error (TwoSum for addition, fused multiply-add residuals for multiplication, division and square
/// root), and the directed modes step the result by one unit when the error points the other way. Single-precision
/// operations go through double precision first, which is exact for rounding to nearest (53 ≥ 2·24 + 2), and the
/// error is carried to the second rounding.
/// </remarks>
public sealed class R4300Fpu : IStateful
{
    /// <summary>FCR0: implementation 0x0A, revision 0 (n64brew "COP1").</summary>
    public const uint ImplementationRevision = 0x00000A00;

    public const uint SingleDefaultNaN = 0x7FBFFFFF;
    public const ulong DoubleDefaultNaN = 0x7FF7FFFFFFFFFFFF;

    // FCR31 fields. Flags, enables and causes use the order inexact, underflow, overflow, division by zero, invalid.
    public const int CauseInexact = 0;
    public const int CauseUnderflow = 1;
    public const int CauseOverflow = 2;
    public const int CauseDivisionByZero = 3;
    public const int CauseInvalid = 4;
    public const int CauseUnimplemented = 5;
    public const uint FlagShift = 2;
    public const uint EnableShift = 7;
    public const uint CauseShift = 12;
    public const uint CauseMask = 0x3Fu << 12;
    public const uint ConditionBit = 1u << 23;
    public const uint FlushBit = 1u << 24;
    public const uint WritableMask = 0x0183FFFF;

    private const int RoundNearest = 0;
    private const int RoundZero = 1;
    private const int RoundPlus = 2;
    private const int RoundMinus = 3;

    private const int FormatSingle = 16;
    private const int FormatDouble = 17;
    private const int FormatWord = 20;
    private const int FormatLong = 21;

    private const double MinNormalSingle = 1.1754943508222875E-38; // 2^-126
    private const double MinNormalDouble = 2.2250738585072014E-308; // 2^-1022

    private static readonly StateTag Tag = StateTag.From("FPU ");

    private readonly ulong[] _registers = new ulong[32];
    private uint _fcr31;

    // Set during an instruction when an enabled cause (or unimplemented operation) asks for the exception.
    private bool _trap;

    /// <summary>Status.FR: 32 registers of 64 bits when set, 16 even/odd pairs when clear. The CPU keeps it current.</summary>
    public bool Fr { get; set; }

    /// <summary>FCR31, the control and status register.</summary>
    public uint Fcr31
    {
        get => _fcr31;
        set => _fcr31 = value & WritableMask;
    }

    /// <summary>The compare condition (FCR31 bit 23) that BC1T and BC1F test.</summary>
    public bool Condition => (_fcr31 & ConditionBit) != 0;

    private int RoundingMode => (int)(_fcr31 & 3);

    /// <summary>A 32-bit register (MFC1, SWC1): with FR clear, an odd register is the high half of the pair.</summary>
    public uint ReadWord(int reg)
    {
        if (!Fr && (reg & 1) != 0)
        {
            return (uint)(_registers[reg & ~1] >> 32);
        }

        return (uint)_registers[reg];
    }

    /// <summary>Writes a 32-bit register (MTC1, LWC1), keeping the other half of the 64-bit register.</summary>
    public void WriteWord(int reg, uint value)
    {
        if (!Fr && (reg & 1) != 0)
        {
            ref var pair = ref _registers[reg & ~1];
            pair = (pair & 0x00000000FFFFFFFF) | ((ulong)value << 32);
            return;
        }

        ref var r = ref _registers[reg];
        r = (r & 0xFFFFFFFF00000000) | value;
    }

    /// <summary>A 64-bit register (DMFC1, SDC1): with FR clear, an odd number means its even pair.</summary>
    public ulong ReadDoubleword(int reg) => _registers[Fr ? reg : reg & ~1];

    /// <summary>Writes a 64-bit register (DMTC1, LDC1).</summary>
    public void WriteDoubleword(int reg, ulong value) => _registers[Fr ? reg : reg & ~1] = value;

    /// <summary>CFC1: FCR0 or FCR31; the other control registers read as zero.</summary>
    public uint ReadControl(int reg) => reg switch
    {
        0 => ImplementationRevision,
        31 => _fcr31,
        _ => 0,
    };

    /// <summary>
    /// CTC1. Returns false when the written FCR31 has a cause bit whose exception is enabled (or the unimplemented
    /// cause): the CPU then raises the floating-point exception. The value is written either way.
    /// </summary>
    public bool WriteControl(int reg, uint value)
    {
        if (reg != 31)
        {
            return true;
        }

        _fcr31 = value & WritableMask;
        var causes = (_fcr31 >> (int)CauseShift) & 0x3F;
        var enables = ((_fcr31 >> (int)EnableShift) & 0x1F) | (1u << CauseUnimplemented);
        return (causes & enables) == 0;
    }

    public void Reset()
    {
        Array.Clear(_registers);
        _fcr31 = 0;
        Fr = false;
    }

    /// <summary>
    /// A computational COP1 instruction (rs = 16, 17, 20 or 21 for S, D, W and L). Returns false when the CPU must
    /// take the floating-point exception (code 15): FCR31's cause bits say why, and no result was written.
    /// </summary>
    public bool Execute(uint instruction)
    {
        _fcr31 &= ~CauseMask;
        _trap = false;
        var format = (int)(instruction >> 21) & 31;
        var ft = (int)(instruction >> 16) & 31;
        var fs = (int)(instruction >> 11) & 31;
        var fd = (int)(instruction >> 6) & 31;
        var function = (int)instruction & 63;
        return format switch
        {
            FormatSingle => ExecuteSingle(function, ft, fs, fd),
            FormatDouble => ExecuteDouble(function, ft, fs, fd),
            FormatWord => ExecuteInteger(function, fs, fd, (int)ReadWord(fs), isLong: false),
            FormatLong => ExecuteInteger(function, fs, fd, (long)ReadDoubleword(fs), isLong: true),
            _ => RaiseUnimplemented(),
        };
    }

    private bool ExecuteSingle(int function, int ft, int fs, int fd)
    {
        var a = ReadWord(fs);
        switch (function)
        {
            case <= 3:
            {
                var b = ReadWord(ft);
                if (!CheckSingle(a) || !CheckSingle(b))
                {
                    return false;
                }

                var x = (double)BitConverter.UInt32BitsToSingle(a);
                var y = (double)BitConverter.UInt32BitsToSingle(b);
                var q = Arithmetic(function, x, y, out var error, out var exact);
                return FinishSingle(q, error, exact, fd);
            }

            case 4: // SQRT.S
            {
                if (!CheckSingle(a))
                {
                    return false;
                }

                var q = SquareRoot(BitConverter.UInt32BitsToSingle(a), out var error);
                return FinishSingle(q, error, exact: double.IsInfinity(q), fd);
            }

            case 5: // ABS.S
            case 7: // NEG.S
            {
                if (!CheckSingle(a))
                {
                    return false;
                }

                var result = IsNaN(a) ? SingleDefaultNaN : function == 5 ? a & 0x7FFFFFFF : a ^ 0x80000000;
                WriteWord(fd, result);
                return true;
            }

            case 6: // MOV.S
                WriteWord(fd, a);
                return true;

            case >= 8 and <= 15: // ROUND/TRUNC/CEIL/FLOOR .L and .W
                return ToInteger(BitConverter.UInt32BitsToSingle(a), IsDenormal(a), function & 3, toLong: function < 12, fd);

            case 0x21: // CVT.D.S
            {
                if (!CheckSingle(a))
                {
                    return false;
                }

                var value = (double)BitConverter.UInt32BitsToSingle(a);
                WriteDoubleword(fd, double.IsNaN(value) ? DoubleDefaultNaN : BitConverter.DoubleToUInt64Bits(value));
                return true;
            }

            case 0x24: // CVT.W.S
            case 0x25: // CVT.L.S
                return ToInteger(BitConverter.UInt32BitsToSingle(a), IsDenormal(a), RoundingMode, toLong: function == 0x25, fd);

            case >= 0x30:
                return Compare(function, BitConverter.UInt32BitsToSingle(a), BitConverter.UInt32BitsToSingle(ReadWord(ft)),
                    IsSignallingNaN(a) || IsSignallingNaN(ReadWord(ft)));

            default: // CVT.S.S and the reserved functions
                return RaiseUnimplemented();
        }
    }

    private bool ExecuteDouble(int function, int ft, int fs, int fd)
    {
        var a = ReadDoubleword(fs);
        switch (function)
        {
            case <= 3:
            {
                var b = ReadDoubleword(ft);
                if (!CheckDouble(a) || !CheckDouble(b))
                {
                    return false;
                }

                var q = Arithmetic(function, BitConverter.UInt64BitsToDouble(a), BitConverter.UInt64BitsToDouble(b),
                    out var error, out var exact);
                return FinishDouble(q, error, exact, fd);
            }

            case 4: // SQRT.D
            {
                if (!CheckDouble(a))
                {
                    return false;
                }

                var q = SquareRoot(BitConverter.UInt64BitsToDouble(a), out var error);
                return FinishDouble(q, error, exact: double.IsInfinity(q), fd);
            }

            case 5: // ABS.D
            case 7: // NEG.D
            {
                if (!CheckDouble(a))
                {
                    return false;
                }

                var result = IsNaN(a) ? DoubleDefaultNaN : function == 5 ? a & 0x7FFFFFFFFFFFFFFF : a ^ 0x8000000000000000;
                WriteDoubleword(fd, result);
                return true;
            }

            case 6: // MOV.D
                WriteDoubleword(fd, a);
                return true;

            case >= 8 and <= 15:
                return ToInteger(BitConverter.UInt64BitsToDouble(a), IsDenormal(a), function & 3, toLong: function < 12, fd);

            case 0x20: // CVT.S.D
                if (!CheckDouble(a))
                {
                    return false;
                }

                return FinishSingle(BitConverter.UInt64BitsToDouble(a), error: 0, exact: double.IsInfinity(BitConverter.UInt64BitsToDouble(a)), fd);

            case 0x24:
            case 0x25:
                return ToInteger(BitConverter.UInt64BitsToDouble(a), IsDenormal(a), RoundingMode, toLong: function == 0x25, fd);

            case >= 0x30:
                return Compare(function, BitConverter.UInt64BitsToDouble(a), BitConverter.UInt64BitsToDouble(ReadDoubleword(ft)),
                    IsSignallingNaN(a) || IsSignallingNaN(ReadDoubleword(ft)));

            default: // CVT.D.D and the reserved functions
                return RaiseUnimplemented();
        }
    }

    /// <summary>CVT.S.W, CVT.D.W, CVT.S.L and CVT.D.L; everything else in the W and L formats is unimplemented.</summary>
    private bool ExecuteInteger(int function, int fs, int fd, long value, bool isLong)
    {
        if (function is not (0x20 or 0x21))
        {
            return RaiseUnimplemented();
        }

        // The VR4300 converts longs only up to 55 bits (n64brew "COP1").
        if (isLong && (value >= 1L << 55 || value < -(1L << 55)))
        {
            return RaiseUnimplemented();
        }

        if (function == 0x21)
        {
            var d = (double)value;
            var error = Math.Sign(value - (long)d);
            return FinishDouble(d, error, exact: false, fd);
        }

        // To single: round to nearest double first (exact below 2^53), then to single with the carried error.
        var nearest = (double)value;
        var doubleError = Math.Sign(value - (long)nearest);
        return FinishSingle(nearest, doubleError, exact: false, fd);
    }

    /// <summary>ADD, SUB, MUL and DIV in double precision: the nearest result and the sign of the exact error.</summary>
    private double Arithmetic(int function, double x, double y, out int error, out bool exact)
    {
        exact = double.IsInfinity(x) || double.IsInfinity(y);
        error = 0;
        double q;
        switch (function)
        {
            case 0:
            case 1:
            {
                if (function == 1)
                {
                    y = -y;
                }

                q = x + y;
                if (double.IsFinite(q))
                {
                    // TwoSum (Knuth): x + y = q + e exactly.
                    var bv = q - x;
                    var e = (x - (q - bv)) + (y - bv);
                    error = Math.Sign(e);
                    // An exact zero from operands of opposite signs is +0, or -0 when rounding down (IEEE 754 §6.3).
                    if (q == 0 && RoundingMode == RoundMinus && (x != 0 || double.IsNegative(x) != double.IsNegative(y)))
                    {
                        q = -0.0;
                    }
                }

                break;
            }

            case 2:
                q = x * y;
                if (double.IsFinite(q))
                {
                    error = Math.Sign(Math.FusedMultiplyAdd(x, y, -q));
                    if (q == 0 && x != 0 && y != 0)
                    {
                        error = Math.Sign(x) * Math.Sign(y); // underflowed past the smallest denormal
                    }
                }

                break;

            default:
                if (y == 0 && x != 0 && !double.IsNaN(x))
                {
                    if (double.IsFinite(x))
                    {
                        Signal(CauseDivisionByZero);
                    }

                    exact = true;
                    return (double.IsNegative(x) ^ double.IsNegative(y)) ? double.NegativeInfinity : double.PositiveInfinity;
                }

                q = x / y;
                if (double.IsFinite(q) && double.IsFinite(y))
                {
                    var residual = Math.FusedMultiplyAdd(-q, y, x);
                    error = Math.Sign(residual) * Math.Sign(y);
                }

                break;
        }

        if (double.IsNaN(q) && !double.IsNaN(x) && !double.IsNaN(y))
        {
            Signal(CauseInvalid); // ∞ − ∞, 0 × ∞, 0 / 0, ∞ / ∞
        }

        return q;
    }

    private double SquareRoot(double x, out int error)
    {
        error = 0;
        if (x < 0)
        {
            Signal(CauseInvalid);
            return double.NaN;
        }

        var q = Math.Sqrt(x);
        if (double.IsFinite(q) && q != 0)
        {
            error = Math.Sign(Math.FusedMultiplyAdd(-q, q, x));
        }

        return q;
    }

    /// <summary>
    /// Rounds the nearest double <paramref name="q"/> (exact result = q + something with the sign of
    /// <paramref name="error"/>) to single precision in the rounding mode, applies the output rules and writes
    /// <paramref name="fd"/>. <paramref name="exact"/> marks infinities that are exact (from infinite operands or
    /// division by zero), which aren't overflows.
    /// </summary>
    private bool FinishSingle(double q, int error, bool exact, int fd)
    {
        if (double.IsNaN(q))
        {
            if (!_trap)
            {
                WriteWord(fd, SingleDefaultNaN);
            }

            return !_trap;
        }

        var nearest = (float)q;
        if (double.IsInfinity(q) && !exact)
        {
            error = q > 0 ? -1 : 1;
        }
        else if (q != nearest && !double.IsInfinity(nearest))
        {
            // Rounding twice can go wrong only when q sits exactly between two singles and the first rounding lost
            // something on the far side (long → single); then the far neighbour is the nearest.
            var other = q > nearest ? MathF.BitIncrement(nearest) : MathF.BitDecrement(nearest);
            if (error != 0 && Math.Sign(error) == Math.Sign(q - nearest) && ((double)nearest + other) / 2 == q)
            {
                nearest = other;
            }

            error = q > nearest ? 1 : -1;
        }
        else if (float.IsInfinity(nearest) && !double.IsInfinity(q))
        {
            error = q > 0 ? -1 : 1; // finite beyond the single range
        }

        var result = Directed(nearest, error);
        if (!exact && (float.IsInfinity(nearest) || float.IsInfinity(result)))
        {
            Signal(CauseOverflow);
            Signal(CauseInexact);
        }
        else if (IsTiny(BitConverter.SingleToUInt32Bits(result) & 0x7FFFFFFF, error, 23))
        {
            var flushed = (double)result;
            if (!Flush(error, ref flushed, MinNormalSingle))
            {
                return false;
            }

            result = (float)flushed;
        }
        else if (error != 0)
        {
            Signal(CauseInexact);
        }

        if (_trap)
        {
            return false;
        }

        WriteWord(fd, BitConverter.SingleToUInt32Bits(result));
        return true;
    }

    /// <summary>The double-precision counterpart of <see cref="FinishSingle"/>.</summary>
    private bool FinishDouble(double q, int error, bool exact, int fd)
    {
        if (double.IsNaN(q))
        {
            if (!_trap)
            {
                WriteDoubleword(fd, DoubleDefaultNaN);
            }

            return !_trap;
        }

        if (double.IsInfinity(q) && !exact)
        {
            error = q > 0 ? -1 : 1;
        }

        var result = Directed(q, error);
        if (!exact && (double.IsInfinity(q) || double.IsInfinity(result)))
        {
            Signal(CauseOverflow);
            Signal(CauseInexact);
        }
        else if (IsTiny(BitConverter.DoubleToUInt64Bits(result) & 0x7FFFFFFFFFFFFFFF, error, 52))
        {
            if (!Flush(error, ref result, MinNormalDouble))
            {
                return false;
            }
        }
        else if (error != 0)
        {
            Signal(CauseInexact);
        }

        if (_trap)
        {
            return false;
        }

        WriteDoubleword(fd, BitConverter.DoubleToUInt64Bits(result));
        return true;
    }

    /// <summary>A denormal result, or a zero that came from a non-zero exact result (an underflow).</summary>
    private static bool IsTiny(ulong magnitude, int error, int fractionBits) =>
        magnitude != 0 ? magnitude >> fractionBits == 0 : error != 0;

    /// <summary>
    /// A tiny result: unimplemented operation, unless FS is set with the underflow and inexact exceptions disabled,
    /// when it flushes to zero or the smallest normal number by the rounding mode (n64brew "COP1").
    /// </summary>
    private bool Flush(int error, ref double result, double minNormal)
    {
        var enables = _fcr31 >> (int)EnableShift;
        if ((_fcr31 & FlushBit) == 0 || (enables & ((1u << CauseUnderflow) | (1u << CauseInexact))) != 0)
        {
            RaiseUnimplemented();
            return false;
        }

        Signal(CauseUnderflow);
        Signal(CauseInexact);
        var negative = result == 0 ? error < 0 || (error == 0 && double.IsNegative(result)) : result < 0;
        result = RoundingMode switch
        {
            RoundPlus => negative ? -0.0 : minNormal,
            RoundMinus => negative ? -minNormal : 0.0,
            _ => negative ? -0.0 : 0.0,
        };
        return true;
    }

    /// <summary>Steps the nearest result one unit when the rounding mode and the exact error disagree with it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T Directed<T>(T nearest, int error)
        where T : struct, IFloatingPointIeee754<T>
    {
        if (error == 0)
        {
            return nearest;
        }

        return RoundingMode switch
        {
            RoundZero when T.IsPositive(nearest) && !T.IsZero(nearest) && error < 0 => T.BitDecrement(nearest),
            RoundZero when T.IsNegative(nearest) && !T.IsZero(nearest) && error > 0 => T.BitIncrement(nearest),
            RoundPlus when error > 0 => T.BitIncrement(nearest),
            RoundMinus when error < 0 => T.BitDecrement(nearest),
            _ => nearest,
        };
    }

    /// <summary>
    /// ROUND/TRUNC/CEIL/FLOOR and CVT.W/L: <paramref name="mode"/> is a rounding mode (0 nearest, 1 toward zero,
    /// 2 up, 3 down).
    /// </summary>
    private bool ToInteger(double value, bool denormal, int mode, bool toLong, int fd)
    {
        if (!double.IsFinite(value) || denormal || (toLong && Math.Abs(value) >= 9007199254740992.0))
        {
            return RaiseUnimplemented();
        }

        var rounded = mode switch
        {
            RoundNearest => Math.Round(value, MidpointRounding.ToEven),
            RoundZero => Math.Truncate(value),
            RoundPlus => Math.Ceiling(value),
            _ => Math.Floor(value),
        };

        if (!toLong && (rounded > int.MaxValue || rounded < int.MinValue))
        {
            return RaiseUnimplemented();
        }

        if (rounded != value)
        {
            Signal(CauseInexact);
        }

        if (_trap)
        {
            return false;
        }

        if (toLong)
        {
            WriteDoubleword(fd, (ulong)(long)rounded);
        }
        else
        {
            WriteWord(fd, (uint)(int)rounded);
        }

        return true;
    }

    private static bool IsDenormal(uint bits) => (bits & 0x7F800000) == 0 && (bits & 0x007FFFFF) != 0;

    private static bool IsDenormal(ulong bits) => (bits & 0x7FF0000000000000) == 0 && (bits & 0x000FFFFFFFFFFFFF) != 0;

    /// <summary>C.cond.fmt: bit 0 unordered, bit 1 equal, bit 2 less than, bit 3 signals invalid on any NaN.</summary>
    private bool Compare(int function, double a, double b, bool signallingNaN)
    {
        var unordered = double.IsNaN(a) || double.IsNaN(b);
        if (unordered && ((function & 8) != 0 || signallingNaN))
        {
            Signal(CauseInvalid);
            if (_trap)
            {
                return false;
            }
        }

        var condition = unordered
            ? (function & 1) != 0
            : ((function & 2) != 0 && a == b) || ((function & 4) != 0 && a < b);
        _fcr31 = condition ? _fcr31 | ConditionBit : _fcr31 & ~ConditionBit;
        return true;
    }

    /// <summary>A single operand's checks: denormals and signalling NaNs are unimplemented, quiet NaNs invalid.</summary>
    private bool CheckSingle(uint bits)
    {
        var exponent = (bits >> 23) & 0xFF;
        var fraction = bits & 0x7FFFFF;
        if (exponent == 0 && fraction != 0)
        {
            return RaiseUnimplemented();
        }

        if (exponent == 0xFF && fraction != 0)
        {
            if ((fraction & 0x400000) != 0)
            {
                return RaiseUnimplemented();
            }

            Signal(CauseInvalid);
            return !_trap;
        }

        return true;
    }

    private bool CheckDouble(ulong bits)
    {
        var exponent = (bits >> 52) & 0x7FF;
        var fraction = bits & 0xFFFFFFFFFFFFF;
        if (exponent == 0 && fraction != 0)
        {
            return RaiseUnimplemented();
        }

        if (exponent == 0x7FF && fraction != 0)
        {
            if ((fraction & 0x8000000000000) != 0)
            {
                return RaiseUnimplemented();
            }

            Signal(CauseInvalid);
            return !_trap;
        }

        return true;
    }

    private static bool IsNaN(uint bits) => (bits & 0x7FFFFFFF) > 0x7F800000;

    private static bool IsNaN(ulong bits) => (bits & 0x7FFFFFFFFFFFFFFF) > 0x7FF0000000000000;

    /// <summary>MIPS (pre-2008) NaNs signal when the fraction's top bit is set.</summary>
    private static bool IsSignallingNaN(uint bits) => IsNaN(bits) && (bits & 0x00400000) != 0;

    private static bool IsSignallingNaN(ulong bits) => IsNaN(bits) && (bits & 0x0008000000000000) != 0;

    /// <summary>Sets a cause bit; its flag too when the exception is disabled, or asks for the trap when enabled.</summary>
    private void Signal(int cause)
    {
        _fcr31 |= 1u << (int)(CauseShift + cause);
        if ((_fcr31 & (1u << (int)(EnableShift + cause))) != 0)
        {
            _trap = true;
        }
        else
        {
            _fcr31 |= 1u << (int)(FlagShift + cause);
        }
    }

    /// <summary>
    /// Sets the unimplemented-operation cause (clearing the others), for the reserved COP1 formats the CPU decodes
    /// (DCFC1, DCTC1…) before it raises the floating-point exception.
    /// </summary>
    public void Unimplemented() => _fcr31 = (_fcr31 & ~CauseMask) | (1u << (int)(CauseShift + CauseUnimplemented));

    /// <summary>The unimplemented-operation exception, which can't be disabled. Its cause replaces any other.</summary>
    private bool RaiseUnimplemented()
    {
        Unimplemented();
        _trap = true;
        return false;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var r in _registers)
        {
            writer.WriteUInt64(r);
        }

        writer.WriteUInt32(_fcr31);
        writer.WriteBool(Fr);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _registers.Length; i++)
        {
            _registers[i] = reader.ReadUInt64();
        }

        _fcr31 = reader.ReadUInt32() & WritableMask;
        Fr = reader.ReadBool();
        reader.EndChunk();
    }
}
