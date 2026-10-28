using GameVault.Core.PlayStation2;
using System.Numerics;
using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// COP1, the EE's single-precision FPU (ps2tek: "EE COP1 Registers", "EE COP1 Floating Point Format"). It isn't IEEE
/// 754: an exponent of 255 is an ordinary number (no infinities or NaNs), denormals read as zero, results that
/// overflow saturate to ±0x7FFFFFFF with the O flag and results that underflow flush to ±0 with the U flag. ADD/SUB
/// and MUL truncate (and the multiplier drops one of its Booth partial products' carry-in); DIV and SQRT round to
/// nearest; RSQRT is fs × DIV(1, SQRT(ft)), truncated. All of it is matched to ps2autotests' hardware results (cpu/ee_fpu).
/// </summary>
public sealed partial class R5900Cpu
{
    // FCR31 (ps2tek: "EE COP1 Registers"; ps2autotests cpu/ee_fpu/fcr): C, the flags and the sticky flags are writable;
    // bits 0 and 24 always read as set.
    private const uint Fcr31Reset = 0x0100_0001;
    private const uint Fcr31WriteMask = 0x0083_C078;
    private const uint FlagC = 1 << 23;
    private const uint FlagI = 1 << 17;
    private const uint FlagD = 1 << 16;
    private const uint FlagO = 1 << 15;
    private const uint FlagU = 1 << 14;
    private const uint FlagSI = 1 << 6;
    private const uint FlagSD = 1 << 5;
    private const uint FlagSO = 1 << 4;
    private const uint FlagSU = 1 << 3;

    /// <summary>FCR0: the FPU's implementation (0x2E) and revision (3.0).</summary>
    public const uint FpuImplementation = 0x2E30;

    // FPU latencies in EE cycles (the EE Core User's Manual: DIV/SQRT 8, RSQRT 14), stalling the next FPU operation.
    private const int FpuDivCycles = 7;
    private const int FpuRsqrtCycles = 13;

    private const uint SignBit = 0x8000_0000;
    private const uint FloatMax = 0x7FFF_FFFF;

    private readonly uint[] _fpr = new uint[32];
    private uint _fpuAcc;
    private uint _fcr31;
    private long _fpuReady;

    /// <summary>An FPU register's bits.</summary>
    public uint GetFpr(int index) => _fpr[index];

    public void SetFpr(int index, uint value) => _fpr[index] = value;

    /// <summary>The FPU accumulator (ADDA, MULA, MADD…).</summary>
    public uint FpuAccumulator
    {
        get => _fpuAcc;
        set => _fpuAcc = value;
    }

    /// <summary>FCR31: the condition bit and the flags.</summary>
    public uint Fcr31
    {
        get => _fcr31;
        set => _fcr31 = (value & Fcr31WriteMask) | Fcr31Reset;
    }

    private bool Cop1Usable()
    {
        if ((_status & StatusCu1) != 0)
        {
            return true;
        }

        RaiseException(R5900Exception.CoprocessorUnusable, 1);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FpuWait()
    {
        if (Cycles < _fpuReady)
        {
            Cycles = _fpuReady;
        }
    }

    private void ExecuteCop1(uint op, int rs, int rt)
    {
        if (!Cop1Usable())
        {
            return;
        }

        var fs = (int)(op >> 11) & 31;
        switch (rs)
        {
            case 0x00: // MFC1
                FpuWait();
                Gpr(rt) = SignExtend32(_fpr[fs]);
                break;
            case 0x02: // CFC1
                FpuWait();
                Gpr(rt) = SignExtend32(fs < 16 ? FpuImplementation : _fcr31);
                break;
            case 0x04: // MTC1
                FpuWait();
                _fpr[fs] = (uint)Gpr(rt);
                break;
            case 0x06: // CTC1: only FCR31 is writable
                FpuWait();
                if (fs == 31)
                {
                    Fcr31 = (uint)Gpr(rt);
                }

                break;
            case 0x08: // BC1F, BC1T, BC1FL, BC1TL
            {
                var condition = (_fcr31 & FlagC) != 0;
                if (_inDelaySlot && (rt & 2) == 0)
                {
                    // BC1F/BC1T in another branch's delay slot cancel that branch: execution goes on with the
                    // instruction after it as its own delay slot (ps2autotests cpu/ee_fpu/branchdelay, on hardware;
                    // the likely forms behave as the integer branches do).
                    _pc = _currentPc + 4;
                    _nextPc = _pc + 4;
                }

                switch (rt & 3)
                {
                    case 0:
                        Branch(!condition, BranchTarget(op));
                        break;
                    case 1:
                        Branch(condition, BranchTarget(op));
                        break;
                    case 2:
                        BranchLikely(!condition, BranchTarget(op));
                        break;
                    default:
                        BranchLikely(condition, BranchTarget(op));
                        break;
                }

                break;
            }

            case 0x10:
                FpuWait();
                ExecuteFpuSingle(op, fs, rt);
                break;
            case 0x14: // W: CVT.S.W
                FpuWait();
                if ((op & 0x3F) == 0x20)
                {
                    _fpr[(int)(op >> 6) & 31] = IntToFloat((int)_fpr[fs]);
                }
                else
                {
                    RaiseException(R5900Exception.ReservedInstruction);
                }

                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    private void ExecuteFpuSingle(uint op, int fs, int ft)
    {
        var fd = (int)(op >> 6) & 31;
        var s = _fpr[fs];
        var t = _fpr[ft];
        switch (op & 0x3F)
        {
            case 0x00: // ADD.S
                _fpr[fd] = Arithmetic(Add(s, t));
                break;
            case 0x01: // SUB.S
                _fpr[fd] = Arithmetic(Add(s, t ^ SignBit));
                break;
            case 0x02: // MUL.S
                _fpr[fd] = Arithmetic(Multiply(s, t));
                break;
            case 0x03: // DIV.S
                _fpr[fd] = Divide(s, t);
                _fpuReady = Cycles + FpuDivCycles;
                break;
            case 0x04: // SQRT.S
                _fcr31 &= ~(FlagI | FlagD);
                _fpr[fd] = SquareRoot(t);
                _fpuReady = Cycles + FpuDivCycles;
                break;
            case 0x05: // ABS.S
                _fcr31 &= ~(FlagO | FlagU);
                _fpr[fd] = s & ~SignBit;
                break;
            case 0x06: // MOV.S
                _fpr[fd] = s;
                break;
            case 0x07: // NEG.S
                _fcr31 &= ~(FlagO | FlagU);
                _fpr[fd] = s ^ SignBit;
                break;
            case 0x16: // RSQRT.S
                _fcr31 &= ~(FlagI | FlagD);
                _fpr[fd] = ReciprocalSquareRoot(s, t);
                _fpuReady = Cycles + FpuRsqrtCycles;
                break;
            case 0x18: // ADDA.S
                _fpuAcc = Arithmetic(Add(s, t));
                break;
            case 0x19: // SUBA.S
                _fpuAcc = Arithmetic(Add(s, t ^ SignBit));
                break;
            case 0x1A: // MULA.S
                _fpuAcc = Arithmetic(Multiply(s, t));
                break;
            case 0x1C: // MADD.S
                _fpr[fd] = MultiplyAdd(_fpuAcc, s, t, false);
                break;
            case 0x1D: // MSUB.S
                _fpr[fd] = MultiplyAdd(_fpuAcc, s, t, true);
                break;
            case 0x1E: // MADDA.S
                _fpuAcc = MultiplyAdd(_fpuAcc, s, t, false);
                break;
            case 0x1F: // MSUBA.S
                _fpuAcc = MultiplyAdd(_fpuAcc, s, t, true);
                break;
            case 0x24: // CVT.W.S
                _fpr[fd] = (uint)FloatToInt(s);
                break;
            case 0x28: // MAX.S
                _fcr31 &= ~(FlagO | FlagU);
                _fpr[fd] = OrderKey(s) >= OrderKey(t) ? s : t;
                break;
            case 0x29: // MIN.S
                _fcr31 &= ~(FlagO | FlagU);
                _fpr[fd] = OrderKey(s) <= OrderKey(t) ? s : t;
                break;
            case 0x30: // C.F
                _fcr31 &= ~FlagC;
                break;
            case 0x32: // C.EQ
                SetCondition(CompareKey(s) == CompareKey(t));
                break;
            case 0x34: // C.LT
                SetCondition(CompareKey(s) < CompareKey(t));
                break;
            case 0x36: // C.LE
                SetCondition(CompareKey(s) <= CompareKey(t));
                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    private void SetCondition(bool condition) => _fcr31 = condition ? _fcr31 | FlagC : _fcr31 & ~FlagC;

    /// <summary>
    /// The flags of an ADD/SUB/MUL result: O (and SO) when it overflowed, U (and SU) when it underflowed; each
    /// operation clears O and U first.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Arithmetic(FloatResult result)
    {
        _fcr31 &= ~(FlagO | FlagU);
        if (result.Overflow)
        {
            _fcr31 |= FlagO | FlagSO;
        }
        else if (result.Underflow)
        {
            _fcr31 |= FlagU | FlagSU;
        }

        return result.Value;
    }

    /// <summary>
    /// MADD/MSUB: ACC ± fs×ft. When the product overflows the result is the saturated product, whatever ACC holds
    /// (ps2autotests cpu/ee_fpu/muldiv).
    /// </summary>
    private uint MultiplyAdd(uint accumulator, uint s, uint t, bool subtract)
    {
        var product = Multiply(s, t);
        var sign = subtract ? SignBit : 0;
        if (product.Overflow)
        {
            return Arithmetic(new FloatResult(product.Value ^ sign, true, false));
        }

        var sum = Add(accumulator, product.Value ^ sign);
        return Arithmetic(new FloatResult(sum.Value, sum.Overflow, sum.Underflow || product.Underflow));
    }

    private uint Divide(uint s, uint t)
    {
        var sign = (s ^ t) & SignBit;
        if ((t & 0x7F80_0000) == 0)
        {
            // Divide by zero: ±max, with I for 0/0 and D otherwise.
            _fcr31 |= (s & 0x7F80_0000) == 0 ? FlagI | FlagSI : FlagD | FlagSD;
            return sign | FloatMax;
        }

        return Ps2Float.Divide(s, t).Value;
    }

    private uint SquareRoot(uint t)
    {
        if ((t & SignBit) != 0 && (t & 0x7F80_0000) != 0)
        {
            _fcr31 |= FlagI | FlagSI; // the root of a negative number: of its magnitude, with I
        }

        return Ps2Float.SquareRoot(t & ~SignBit);
    }

    private uint ReciprocalSquareRoot(uint s, uint t)
    {
        if ((t & 0x7F80_0000) == 0)
        {
            _fcr31 |= (s & 0x7F80_0000) == 0 ? FlagI | FlagSI : FlagD | FlagSD;
            return (s & SignBit) | FloatMax;
        }

        if ((t & SignBit) != 0)
        {
            _fcr31 |= FlagI | FlagSI;
        }

        var root = Ps2Float.SquareRoot(t & ~SignBit);
        var reciprocal = Ps2Float.Divide(0x3F80_0000, root).Value;
        return Ps2Float.Multiply(s, reciprocal, booth: false).Value;
    }

    /// <summary>
    /// The FPU's add and multiply in the host's float arithmetic, clamped to the PS2's rules, instead of bit-exactly
    /// (ps2-full-speed.md §2). Off by default; the host turns it on for normal play unless the player asks for exact
    /// emulation. Netplay and challenge checks keep it off.
    /// </summary>
    public bool FastFloat { get; set; }

    private FloatResult Add(uint a, uint b) => FastFloat ? Ps2FastFloat.AddResult(a, b) : Ps2Float.Add(a, b);

    private FloatResult Multiply(uint a, uint b) => FastFloat ? Ps2FastFloat.MultiplyResult(a, b) : Ps2Float.Multiply(a, b);

    /// <summary>
    /// The order MAX/MIN use: sign and magnitude as an integer (no NaNs on the PS2), with −0 below +0
    /// (ps2autotests cpu/ee_fpu/arithmetic).
    /// </summary>
    private static long OrderKey(uint value) => (value & SignBit) != 0 ? -(long)(value & ~SignBit) - 1 : value;

    /// <summary>The order the compares use: sign and magnitude, with denormals and −0 equal to +0.</summary>
    private static long CompareKey(uint value) =>
        (value & 0x7F80_0000) == 0 ? 0 : (value & SignBit) != 0 ? -(long)(value & ~SignBit) : value;

    /// <summary>CVT.S.W: truncated toward zero.</summary>
    private static uint IntToFloat(int value)
    {
        if (value == 0)
        {
            return 0;
        }

        var sign = value < 0 ? SignBit : 0;
        var magnitude = value < 0 ? (uint)-(long)value : (uint)value;
        var leading = BitOperations.LeadingZeroCount(magnitude);
        var exponent = (uint)(31 - leading + 127);
        var mantissa = leading >= 8 ? magnitude << (leading - 8) : magnitude >> (8 - leading);
        return sign | (exponent << 23) | (mantissa & 0x7F_FFFF);
    }

    /// <summary>CVT.W.S: truncated toward zero; magnitudes of 2³¹ and more saturate.</summary>
    private static int FloatToInt(uint value)
    {
        var exponent = (int)((value >> 23) & 0xFF);
        var negative = (value & SignBit) != 0;
        if (exponent < 127)
        {
            return 0;
        }

        if (exponent >= 158)
        {
            return negative ? int.MinValue : int.MaxValue;
        }

        var mantissa = (value & 0x7F_FFFF) | 0x80_0000;
        var shift = exponent - 150;
        var magnitude = shift >= 0 ? (long)mantissa << shift : mantissa >> -shift;
        return (int)(negative ? -magnitude : magnitude);
    }

    private void SaveFpu(StateWriter writer)
    {
        foreach (var register in _fpr)
        {
            writer.WriteUInt32(register);
        }

        writer.WriteUInt32(_fpuAcc);
        writer.WriteUInt32(_fcr31);
        writer.WriteInt64(_fpuReady);
    }

    private void LoadFpu(StateReader reader)
    {
        for (var i = 0; i < _fpr.Length; i++)
        {
            _fpr[i] = reader.ReadUInt32();
        }

        _fpuAcc = reader.ReadUInt32();
        _fcr31 = reader.ReadUInt32();
        _fpuReady = reader.ReadInt64();
    }
}

/// <summary>An FPU result and whether it overflowed (saturated) or underflowed (flushed to zero).</summary>
internal readonly record struct FloatResult(uint Value, bool Overflow, bool Underflow);

/// <summary>
/// The PS2's single-precision arithmetic on raw bits: no infinities, NaNs or denormals, saturation instead of
/// overflow. Addition and multiplication truncate; division and square root round to nearest.
/// </summary>
internal static class Ps2Float
{
    private const uint SignBit = 0x8000_0000;
    private const uint ImplicitBit = 1 << 23;
    private const uint MantissaMask = ImplicitBit - 1;

    /// <summary>
    /// a + b, truncated. Ported from Play! (BSD-2-Clause): Source/ee/FpAddTruncate.cpp (itself from LLVM's addsf3),
    /// with exponent 255 an ordinary number, denormal inputs read as zero, and saturation/flush for out-of-range
    /// results. The smaller operand keeps one guard bit when aligned, as the hardware's adder does.
    /// </summary>
    public static FloatResult Add(uint a, uint b)
    {
        // Denormals are zero.
        if ((a & 0x7F80_0000) == 0)
        {
            a &= SignBit;
        }

        if ((b & 0x7F80_0000) == 0)
        {
            b &= SignBit;
        }

        var aAbs = a & ~SignBit;
        var bAbs = b & ~SignBit;
        if (aAbs == 0)
        {
            return new FloatResult(bAbs == 0 ? a & b : b, false, false);
        }

        if (bAbs == 0)
        {
            return new FloatResult(a, false, false);
        }

        if (bAbs > aAbs)
        {
            (a, b) = (b, a);
        }

        var aExponent = (int)((a >> 23) & 0xFF);
        var bExponent = (int)((b >> 23) & 0xFF);
        var aSignificand = ((a & MantissaMask) | ImplicitBit) << 3;
        var bSignificand = ((b & MantissaMask) | ImplicitBit) << 3;
        var resultSign = a & SignBit;
        var subtraction = ((a ^ b) & SignBit) != 0;

        var align = aExponent - bExponent;
        if (align != 0)
        {
            bSignificand = align < 32 ? (bSignificand >> align) & ~3u : 0;
        }

        if (subtraction)
        {
            aSignificand -= bSignificand;
            if (aSignificand == 0)
            {
                return new FloatResult(0, false, false);
            }

            if (aSignificand < ImplicitBit << 3)
            {
                var shift = BitOperations.LeadingZeroCount(aSignificand) - BitOperations.LeadingZeroCount(ImplicitBit << 3);
                aSignificand <<= shift;
                aExponent -= shift;
            }
        }
        else
        {
            aSignificand += bSignificand;
            if ((aSignificand & (ImplicitBit << 4)) != 0)
            {
                aSignificand >>= 1;
                aExponent++;
            }
        }

        if (aExponent > 255)
        {
            return new FloatResult(resultSign | 0x7FFF_FFFF, true, false);
        }

        if (aExponent <= 0)
        {
            return new FloatResult(resultSign, false, true);
        }

        return new FloatResult(resultSign | ((uint)aExponent << 23) | ((aSignificand >> 3) & MantissaMask), false, false);
    }

    /// <summary>
    /// a × b, truncated (after Play!'s FpMulTruncate.cpp, BSD-2-Clause). The hardware's Booth multiplier loses the
    /// carry-in of its lowest partial product when that digit is negative (bit 1 of b's mantissa set), so the product
    /// is one less in its lowest bit than the exact one; that is what makes 1.0 × 0x3FFFFFFF give 0x3FFFFFFE while
    /// 0x3FFFFFFF × 1.0 gives 0x3FFFFFFF (ps2autotests cpu/ee_fpu/muldiv). Where the dropped carries of the higher
    /// partial products would show isn't covered by the hardware vectors; this keeps them.
    /// </summary>
    /// <param name="booth">False for RSQRT's multiply, which the hardware vectors show exact before truncation.</param>
    public static FloatResult Multiply(uint a, uint b, bool booth = true)
    {
        var sign = (a ^ b) & SignBit;
        var aExponent = (int)((a >> 23) & 0xFF);
        var bExponent = (int)((b >> 23) & 0xFF);
        if (aExponent == 0 || bExponent == 0)
        {
            return new FloatResult(sign, false, false);
        }

        var aSignificand = (ulong)((a & MantissaMask) | ImplicitBit);
        var bSignificand = (b & MantissaMask) | ImplicitBit;
        var product = aSignificand * bSignificand;
        if (booth && (bSignificand & 2) != 0)
        {
            product--;
        }

        var exponent = aExponent + bExponent - 127;
        uint mantissa;
        if (product >= 1UL << 47)
        {
            mantissa = (uint)(product >> 24);
            exponent++;
        }
        else
        {
            mantissa = (uint)(product >> 23);
        }

        if (exponent > 255)
        {
            return new FloatResult(sign | 0x7FFF_FFFF, true, false);
        }

        if (exponent <= 0)
        {
            return new FloatResult(sign, false, true);
        }

        return new FloatResult(sign | ((uint)exponent << 23) | (mantissa & MantissaMask), false, false);
    }

    /// <summary>a ÷ b for a nonzero b, rounded to nearest (ps2autotests cpu/ee_fpu/muldiv).</summary>
    public static FloatResult Divide(uint a, uint b)
    {
        var sign = (a ^ b) & SignBit;
        var aExponent = (int)((a >> 23) & 0xFF);
        var bExponent = (int)((b >> 23) & 0xFF);
        if (aExponent == 0)
        {
            return new FloatResult(sign, false, false);
        }

        var aSignificand = (ulong)((a & MantissaMask) | ImplicitBit);
        var bSignificand = (ulong)((b & MantissaMask) | ImplicitBit);
        var quotient = (aSignificand << 25) / bSignificand;
        var remainder = (aSignificand << 25) % bSignificand;
        var exponent = aExponent - bExponent + 127;
        ulong mantissa;
        bool guard;
        bool sticky;
        if (quotient >= 1UL << 25)
        {
            mantissa = quotient >> 2;
            guard = ((quotient >> 1) & 1) != 0;
            sticky = (quotient & 1) != 0 || remainder != 0;
        }
        else
        {
            mantissa = quotient >> 1;
            guard = (quotient & 1) != 0;
            sticky = remainder != 0;
            exponent--;
        }

        return Round(sign, exponent, mantissa, guard, sticky);
    }

    /// <summary>√a for a non-negative a, rounded to nearest (ps2autotests cpu/ee_fpu/sqrt).</summary>
    public static uint SquareRoot(uint a)
    {
        var aExponent = (int)((a >> 23) & 0xFF);
        if (aExponent == 0)
        {
            return 0;
        }

        // a = M × 2^k with M the 24-bit significand; make k even, then √a = √(M × 2^28) × 2^(k/2 − 14).
        var significand = (ulong)((a & MantissaMask) | ImplicitBit);
        var k = aExponent - 127 - 23;
        if ((k & 1) != 0)
        {
            significand <<= 1;
            k--;
        }

        var radicand = significand << 28;
        var root = IntegerSquareRoot(radicand);
        var exact = root * root == radicand;
        var scale = k / 2 - 14;
        ulong mantissa;
        bool guard;
        bool sticky;
        if (root >= 1UL << 26)
        {
            mantissa = root >> 3;
            guard = ((root >> 2) & 1) != 0;
            sticky = (root & 3) != 0 || !exact;
            scale += 3;
        }
        else
        {
            mantissa = root >> 2;
            guard = ((root >> 1) & 1) != 0;
            sticky = (root & 1) != 0 || !exact;
            scale += 2;
        }

        return Round(0, scale + 23 + 127, mantissa, guard, sticky).Value;
    }

    private static ulong IntegerSquareRoot(ulong value)
    {
        var root = (ulong)Math.Sqrt(value);
        while (root * root > value)
        {
            root--;
        }

        while ((root + 1) * (root + 1) <= value)
        {
            root++;
        }

        return root;
    }

    /// <summary>Rounds a 24-bit significand to nearest (ties to even), then saturates or flushes the exponent.</summary>
    private static FloatResult Round(uint sign, int exponent, ulong mantissa, bool guard, bool sticky)
    {
        if (guard && (sticky || (mantissa & 1) != 0))
        {
            mantissa++;
            if (mantissa == 1UL << 24)
            {
                mantissa >>= 1;
                exponent++;
            }
        }

        if (exponent > 255)
        {
            return new FloatResult(sign | 0x7FFF_FFFF, true, false);
        }

        if (exponent <= 0)
        {
            return new FloatResult(sign, false, true);
        }

        return new FloatResult(sign | ((uint)exponent << 23) | ((uint)mantissa & MantissaMask), false, false);
    }
}
