using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The RSP's vector unit (COP2): 32 registers of eight 16-bit lanes, a 48-bit accumulator per lane, the VCO, VCC and
/// VCE flags, and the divide unit behind VRCP/VRSQ.
/// </summary>
/// <remarks>
/// References: n64brew "Reality Signal Processor/CPU Core" (element selectors, the instructions, the reciprocal ROMs) and
/// the hardware results recorded in PeterLemon's RSPTest programs (the reserved opcodes, VSAR, VRCP/VRSQ).
/// Lane <c>n</c> of a register is bytes <c>2n</c> and <c>2n+1</c> of its big-endian image; lane <c>n</c> of a flag
/// register is bit <c>n</c> (VCO's and VCC's upper halves are bits 8–15).
/// <para>
/// The registers are <see cref="Vector128{T}"/> values with lane <c>n</c> as element <c>n</c>, so the common
/// instructions work on all eight lanes at once (integer SIMD, deterministic on every CPU). The accumulator is kept as
/// its low 16 bits (<c>_accL</c>) and its upper 32 bits, high:middle, as two vectors of four signed ints. Rare
/// instructions (the clips, the MPEG ones, the divides) work lane by lane on copies.
/// </para>
/// </remarks>
public sealed partial class N64Rsp
{
    /// <summary>pshufb-style byte indices for each element selector <c>e</c>: whole, quarters, halves, then a single lane.</summary>
    private static readonly Vector128<byte>[] ElementShuffles = BuildElementShuffles();

    /// <summary>For each 8-bit flag value, a mask with lane <c>n</c> all ones when bit <c>n</c> is set.</summary>
    private static readonly Vector128<short>[] FlagMasks = BuildFlagMasks();

    private readonly Vector128<short>[] _vr = new Vector128<short>[32];

    // The accumulator: bits 0–15 per lane, and bits 16–47 (high:middle, signed) for lanes 0–3 and 4–7.
    private Vector128<short> _accL;
    private Vector128<int> _accHmLo;
    private Vector128<int> _accHmHi;

    // The previous vector instruction's vt operand (with its element selector), which some reserved opcodes add.
    private Vector128<short> _lastVte;

    private ushort _vco; // bits 0–7: carry (VCOL), 8–15: not-equal (VCOH)
    private ushort _vcc; // bits 0–7: compare (VCCL), 8–15: clip (VCCH)
    private byte _vce;

    private short _divIn;
    private short _divOut;
    private bool _divInLoaded;

    /// <summary>A lane of a vector register, for tests and debugging.</summary>
    public ushort Vector(int register, int lane) => (ushort)_vr[register].GetElement(lane);

    /// <summary>A lane's 48-bit accumulator, sign-extended.</summary>
    public long Accumulator(int lane)
    {
        var highMiddle = lane < 4 ? _accHmLo.GetElement(lane) : _accHmHi.GetElement(lane - 4);
        return (long)highMiddle << 16 | (ushort)_accL.GetElement(lane);
    }

    public ushort Vco => _vco;

    public ushort Vcc => _vcc;

    public byte Vce => _vce;

    private static Vector128<byte>[] BuildElementShuffles()
    {
        var shuffles = new Vector128<byte>[16];
        Span<byte> indices = stackalloc byte[16];
        for (var e = 0; e < 16; e++)
        {
            for (var n = 0; n < 8; n++)
            {
                var lane = e switch
                {
                    < 2 => n,
                    < 4 => (n & 6) | (e & 1),
                    < 8 => (n & 4) | (e & 3),
                    _ => e & 7,
                };

                indices[n * 2] = (byte)(lane * 2);
                indices[n * 2 + 1] = (byte)(lane * 2 + 1);
            }

            shuffles[e] = Vector128.Create((ReadOnlySpan<byte>)indices);
        }

        return shuffles;
    }

    private static Vector128<short>[] BuildFlagMasks()
    {
        var masks = new Vector128<short>[256];
        Span<short> lanes = stackalloc short[8];
        for (var bits = 0; bits < 256; bits++)
        {
            for (var n = 0; n < 8; n++)
            {
                lanes[n] = (short)((bits >> n & 1) != 0 ? -1 : 0);
            }

            masks[bits] = Vector128.Create((ReadOnlySpan<short>)lanes);
        }

        return masks;
    }

    private void ResetVector()
    {
        Array.Clear(_vr);
        _accL = default;
        _accHmLo = _accHmHi = default;
        _lastVte = default;
        _vco = _vcc = 0;
        _vce = 0;
        _divIn = _divOut = 0;
        _divInLoaded = false;
    }

    // ---- Register access ------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Vector128<short> Register(int index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_vr), index);

    /// <summary>Byte <paramref name="index"/> (0–15) of a register's big-endian image.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte RegisterByte(int register, int index) =>
        ref Unsafe.Add(ref Unsafe.As<Vector128<short>, byte>(ref Register(register)), (index ^ 1) & 15);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetByte(int register, int index) => RegisterByte(register, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetByte(int register, int index, byte value) => RegisterByte(register, index) = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref short Lane(int register, int lane) =>
        ref Unsafe.Add(ref Unsafe.As<Vector128<short>, short>(ref Register(register)), lane);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Shuffle(Vector128<short> value, Vector128<byte> indices)
    {
        if (Ssse3.IsSupported)
        {
            return Ssse3.Shuffle(value.AsByte(), indices).AsInt16();
        }

        if (AdvSimd.Arm64.IsSupported)
        {
            return AdvSimd.Arm64.VectorTableLookup(value.AsByte(), indices).AsInt16();
        }

        return Vector128.Shuffle(value.AsByte(), indices).AsInt16();
    }

    /// <summary>A big-endian 16-byte block of DMEM as a register (the block must not cross the end of DMEM).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<short> ReadQuad(int address)
    {
        var bytes = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(Dmem), (nuint)address);
        return Vector128.Shuffle(bytes, Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14)).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteQuad(int address, Vector128<short> value)
    {
        var bytes = Vector128.Shuffle(value.AsByte(), Vector128.Create((byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14));
        bytes.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(Dmem), (nuint)address);
    }

    // ---- Accumulator helpers --------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenSignedLower(Vector128<short> value) => Vector128.WidenLower(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenSignedUpper(Vector128<short> value) => Vector128.WidenUpper(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenUnsignedLower(Vector128<short> value) => Vector128.WidenLower(value.AsUInt16()).AsInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenUnsignedUpper(Vector128<short> value) => Vector128.WidenUpper(value.AsUInt16()).AsInt32();

    /// <summary>The low 16 bits of each int lane, packed back into eight shorts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Truncate(Vector128<int> lower, Vector128<int> upper) => Vector128.Narrow(lower, upper);

    /// <summary>Signed saturation to 16 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ClampSigned(Vector128<int> lower, Vector128<int> upper)
    {
        var min = Vector128.Create((int)short.MinValue);
        var max = Vector128.Create((int)short.MaxValue);
        return Vector128.Narrow(Vector128.Min(Vector128.Max(lower, min), max), Vector128.Min(Vector128.Max(upper, min), max));
    }

    /// <summary>Sets the accumulator to <c>highMiddle:low</c> (low holds 0–65535 per lane).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAccumulator(Vector128<int> hmLower, Vector128<int> hmUpper, Vector128<short> low)
    {
        _accHmLo = hmLower;
        _accHmHi = hmUpper;
        _accL = low;
    }

    /// <summary>Adds <c>highMiddle:low</c> (low holds 0–65535 per lane) to the accumulator, carrying from the low lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddToAccumulator(Vector128<int> hmLower, Vector128<int> hmUpper, Vector128<int> lowLower, Vector128<int> lowUpper)
    {
        var sumLower = WidenUnsignedLower(_accL) + lowLower;
        var sumUpper = WidenUnsignedUpper(_accL) + lowUpper;
        _accHmLo += hmLower + Vector128.ShiftRightLogical(sumLower, 16);
        _accHmHi += hmUpper + Vector128.ShiftRightLogical(sumUpper, 16);
        _accL = Truncate(sumLower, sumUpper);
    }

    /// <summary>The middle lane, or the signed clamp when the upper 32 bits don't fit in 16.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<short> SaturateMiddle() => ClampSigned(_accHmLo, _accHmHi);

    /// <summary>The low lane, or 0 / $FFFF when the upper 32 bits are below / above the signed 16-bit range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<short> SaturateLow()
    {
        var below = Vector128.Narrow(
            Vector128.LessThan(_accHmLo, Vector128.Create((int)short.MinValue)),
            Vector128.LessThan(_accHmHi, Vector128.Create((int)short.MinValue)));
        var above = Vector128.Narrow(
            Vector128.GreaterThan(_accHmLo, Vector128.Create((int)short.MaxValue)),
            Vector128.GreaterThan(_accHmHi, Vector128.Create((int)short.MaxValue)));
        return Vector128.AndNot(_accL, below | above) | above;
    }

    /// <summary>The unsigned clamp of VMULU and VMACU: 0 when negative, $FFFF past 15 bits, otherwise the middle lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector128<short> SaturateUnsigned()
    {
        var max = Vector128.Create((int)short.MaxValue);
        var above = Vector128.Narrow(Vector128.GreaterThan(_accHmLo, max), Vector128.GreaterThan(_accHmHi, max));
        var middle = Truncate(Vector128.Max(_accHmLo, Vector128<int>.Zero), Vector128.Max(_accHmHi, Vector128<int>.Zero));
        return middle | above;
    }

    // ---- Lane-by-lane access for the rare instructions -------------------------------------------------------------

    /// <summary>Copies the accumulator out, one sign-extended 48-bit value per lane.</summary>
    private void ReadAccumulators(Span<long> accumulators)
    {
        for (var n = 0; n < 8; n++)
        {
            accumulators[n] = Accumulator(n);
        }
    }

    private void WriteAccumulators(ReadOnlySpan<long> accumulators)
    {
        Span<int> highMiddle = stackalloc int[8];
        Span<short> low = stackalloc short[8];
        for (var n = 0; n < 8; n++)
        {
            highMiddle[n] = (int)(accumulators[n] >> 16);
            low[n] = (short)accumulators[n];
        }

        _accHmLo = Vector128.Create((ReadOnlySpan<int>)highMiddle[..4]);
        _accHmHi = Vector128.Create((ReadOnlySpan<int>)highMiddle[4..]);
        _accL = Vector128.Create((ReadOnlySpan<short>)low);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Wrap48(long value) => (value << 16) >> 16;

    /// <summary>Clamps a lane's accumulator (bits 16–47) to signed 16 bits, else returns its middle (or low) lane.</summary>
    private static ushort SaturateLane(long accumulator, bool low, ushort negative, ushort positive)
    {
        var upper = accumulator >> 16;
        if (upper < short.MinValue)
        {
            return negative;
        }

        if (upper > short.MaxValue)
        {
            return positive;
        }

        return low ? (ushort)accumulator : (ushort)(accumulator >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampSigned(int value) => (ushort)Math.Clamp(value, short.MinValue, short.MaxValue);

    // ---- COP2 -----------------------------------------------------------------------------------------------------

    private void ExecuteCop2(uint instruction, int rs, int rt)
    {
        if ((instruction & (1u << 25)) != 0)
        {
            ExecuteVector(instruction);
            return;
        }

        var rd = (int)((instruction >> 11) & 31);
        var element = (int)((instruction >> 7) & 15);
        switch (rs)
        {
            case 0x00: // MFC2
                G(rt) = (uint)(short)(GetByte(rd, element) << 8 | GetByte(rd, (element + 1) & 15));
                break;
            case 0x02: // CFC2
                G(rt) = (rd & 3) switch
                {
                    0 => (uint)(short)_vco,
                    1 => (uint)(short)_vcc,
                    _ => _vce,
                };
                break;
            case 0x04: // MTC2
                SetByte(rd, element, (byte)(G(rt) >> 8));
                if (element != 15)
                {
                    SetByte(rd, element + 1, (byte)G(rt));
                }

                break;
            case 0x06: // CTC2
                switch (rd & 3)
                {
                    case 0: _vco = (ushort)G(rt); break;
                    case 1: _vcc = (ushort)G(rt); break;
                    default: _vce = (byte)G(rt); break;
                }

                break;
        }
    }

    private void ExecuteVector(uint instruction)
    {
        var e = (int)((instruction >> 21) & 15);
        var vt = (int)((instruction >> 16) & 31);
        var vs = (int)((instruction >> 11) & 31);
        var vd = (int)((instruction >> 6) & 31);

        // vt with the element selector applied.
        var vte = Register(vt);
        if (e >= 2)
        {
            vte = Shuffle(vte, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(ElementShuffles), e));
        }

        var a = Register(vs);
        ref var d = ref Register(vd);
        var function = instruction & 0x3F;
        switch (function)
        {
            case 0x00: // VMULF
            case 0x01: // VMULU
                {
                    var productLower = WidenSignedLower(a) * WidenSignedLower(vte);
                    var productUpper = WidenSignedUpper(a) * WidenSignedUpper(vte);

                    // The product doubled and rounded: 2p + $8000, computed without overflowing 32 bits.
                    var lowMask = Vector128.Create(0xFFFF);
                    var roundedLower = (Vector128.ShiftLeft(productLower, 1) & lowMask) + Vector128.Create(0x8000);
                    var roundedUpper = (Vector128.ShiftLeft(productUpper, 1) & lowMask) + Vector128.Create(0x8000);
                    SetAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 15) + Vector128.ShiftRightLogical(roundedLower, 16),
                        Vector128.ShiftRightArithmetic(productUpper, 15) + Vector128.ShiftRightLogical(roundedUpper, 16),
                        Truncate(roundedLower, roundedUpper));
                    d = function == 0x00 ? SaturateMiddle() : SaturateUnsigned();
                    break;
                }

            case 0x02: Round(instruction, vte, positive: true); break; // VRNDP
            case 0x03: MultiplyQuantized(vs, vd, vte); break; // VMULQ
            case 0x04: // VMUDL
                {
                    var productLower = Vector128.ShiftRightLogical(WidenUnsignedLower(a).AsUInt32() * WidenUnsignedLower(vte).AsUInt32(), 16).AsInt32();
                    var productUpper = Vector128.ShiftRightLogical(WidenUnsignedUpper(a).AsUInt32() * WidenUnsignedUpper(vte).AsUInt32(), 16).AsInt32();
                    SetAccumulator(Vector128<int>.Zero, Vector128<int>.Zero, Truncate(productLower, productUpper));
                    d = _accL;
                    break;
                }

            case 0x05: // VMUDM
                {
                    var productLower = WidenSignedLower(a) * WidenUnsignedLower(vte);
                    var productUpper = WidenSignedUpper(a) * WidenUnsignedUpper(vte);
                    SetAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 16),
                        Vector128.ShiftRightArithmetic(productUpper, 16),
                        Truncate(productLower, productUpper));
                    d = Truncate(_accHmLo, _accHmHi);
                    break;
                }

            case 0x06: // VMUDN
                {
                    var productLower = WidenUnsignedLower(a) * WidenSignedLower(vte);
                    var productUpper = WidenUnsignedUpper(a) * WidenSignedUpper(vte);
                    SetAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 16),
                        Vector128.ShiftRightArithmetic(productUpper, 16),
                        Truncate(productLower, productUpper));
                    d = _accL;
                    break;
                }

            case 0x07: // VMUDH
                SetAccumulator(WidenSignedLower(a) * WidenSignedLower(vte), WidenSignedUpper(a) * WidenSignedUpper(vte), Vector128<short>.Zero);
                d = SaturateMiddle();
                break;
            case 0x08: // VMACF
            case 0x09: // VMACU
                {
                    var productLower = WidenSignedLower(a) * WidenSignedLower(vte);
                    var productUpper = WidenSignedUpper(a) * WidenSignedUpper(vte);
                    var lowMask = Vector128.Create(0xFFFF);
                    AddToAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 15),
                        Vector128.ShiftRightArithmetic(productUpper, 15),
                        Vector128.ShiftLeft(productLower, 1) & lowMask,
                        Vector128.ShiftLeft(productUpper, 1) & lowMask);
                    d = function == 0x08 ? SaturateMiddle() : SaturateUnsigned();
                    break;
                }

            case 0x0A: Round(instruction, vte, positive: false); break; // VRNDN
            case 0x0B: MultiplyAccumulateQuantized(vd); break; // VMACQ
            case 0x0C: // VMADL
                {
                    var productLower = Vector128.ShiftRightLogical(WidenUnsignedLower(a).AsUInt32() * WidenUnsignedLower(vte).AsUInt32(), 16).AsInt32();
                    var productUpper = Vector128.ShiftRightLogical(WidenUnsignedUpper(a).AsUInt32() * WidenUnsignedUpper(vte).AsUInt32(), 16).AsInt32();
                    AddToAccumulator(Vector128<int>.Zero, Vector128<int>.Zero, productLower, productUpper);
                    d = SaturateLow();
                    break;
                }

            case 0x0D: // VMADM
                {
                    var productLower = WidenSignedLower(a) * WidenUnsignedLower(vte);
                    var productUpper = WidenSignedUpper(a) * WidenUnsignedUpper(vte);
                    var lowMask = Vector128.Create(0xFFFF);
                    AddToAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 16),
                        Vector128.ShiftRightArithmetic(productUpper, 16),
                        productLower & lowMask,
                        productUpper & lowMask);
                    d = SaturateMiddle();
                    break;
                }

            case 0x0E: // VMADN
                {
                    var productLower = WidenUnsignedLower(a) * WidenSignedLower(vte);
                    var productUpper = WidenUnsignedUpper(a) * WidenSignedUpper(vte);
                    var lowMask = Vector128.Create(0xFFFF);
                    AddToAccumulator(
                        Vector128.ShiftRightArithmetic(productLower, 16),
                        Vector128.ShiftRightArithmetic(productUpper, 16),
                        productLower & lowMask,
                        productUpper & lowMask);
                    d = SaturateLow();
                    break;
                }

            case 0x0F: // VMADH
                _accHmLo += WidenSignedLower(a) * WidenSignedLower(vte);
                _accHmHi += WidenSignedUpper(a) * WidenSignedUpper(vte);
                d = SaturateMiddle();
                break;
            case 0x10: // VADD
            case 0x11: // VSUB
                {
                    var carry = FlagMasks[_vco & 0xFF];
                    var carryLower = -WidenSignedLower(carry);
                    var carryUpper = -WidenSignedUpper(carry);
                    Vector128<int> resultLower, resultUpper;
                    if (function == 0x10)
                    {
                        resultLower = WidenSignedLower(a) + WidenSignedLower(vte) + carryLower;
                        resultUpper = WidenSignedUpper(a) + WidenSignedUpper(vte) + carryUpper;
                    }
                    else
                    {
                        resultLower = WidenSignedLower(a) - WidenSignedLower(vte) - carryLower;
                        resultUpper = WidenSignedUpper(a) - WidenSignedUpper(vte) - carryUpper;
                    }

                    _accL = Truncate(resultLower, resultUpper);
                    d = ClampSigned(resultLower, resultUpper);
                    _vco = 0;
                    break;
                }

            case 0x13: // VABS
                {
                    var negative = Vector128.LessThan(a, Vector128<short>.Zero);
                    var positive = Vector128.GreaterThan(a, Vector128<short>.Zero);
                    var result = Vector128.ConditionalSelect(negative, -vte, vte & positive);
                    _accL = result;
                    var overflow = negative & Vector128.Equals(vte, Vector128.Create(short.MinValue));
                    d = Vector128.ConditionalSelect(overflow, Vector128.Create(short.MaxValue), result);
                    break;
                }

            case 0x14: // VADDC
                {
                    var sumLower = WidenUnsignedLower(a) + WidenUnsignedLower(vte);
                    var sumUpper = WidenUnsignedUpper(a) + WidenUnsignedUpper(vte);
                    var result = Truncate(sumLower, sumUpper);
                    _accL = result;
                    d = result;
                    var carry = Vector128.Narrow(Vector128.ShiftRightLogical(sumLower, 16), Vector128.ShiftRightLogical(sumUpper, 16));
                    _vco = (ushort)Vector128.ExtractMostSignificantBits(-carry);
                    break;
                }

            case 0x15: // VSUBC
                {
                    var differenceLower = WidenUnsignedLower(a) - WidenUnsignedLower(vte);
                    var differenceUpper = WidenUnsignedUpper(a) - WidenUnsignedUpper(vte);
                    var result = Truncate(differenceLower, differenceUpper);
                    _accL = result;
                    d = result;
                    var borrow = Vector128.ExtractMostSignificantBits(Vector128.Narrow(
                        Vector128.LessThan(differenceLower, Vector128<int>.Zero),
                        Vector128.LessThan(differenceUpper, Vector128<int>.Zero)));
                    var notEqual = Vector128.ExtractMostSignificantBits(~Vector128.Equals(a, vte));
                    _vco = (ushort)(borrow | notEqual << 8);
                    break;
                }

            case 0x1D: // VSAR: reads the accumulator; leaves it unchanged
                d = e switch
                {
                    8 => Truncate(Vector128.ShiftRightArithmetic(_accHmLo, 16), Vector128.ShiftRightArithmetic(_accHmHi, 16)),
                    9 => Truncate(_accHmLo, _accHmHi),
                    10 => _accL,
                    _ => Vector128<short>.Zero,
                };

                // VSAR reads the accumulator without the datapath, so it leaves the latched operand alone.
                return;
            case 0x20: // VLT
            case 0x21: // VEQ
            case 0x22: // VNE
            case 0x23: // VGE
                {
                    var carry = FlagMasks[_vco & 0xFF];
                    var notEqualFlag = FlagMasks[_vco >> 8];
                    var equal = Vector128.Equals(a, vte);
                    var result = function switch
                    {
                        0x20 => Vector128.LessThan(a, vte) | (equal & carry & notEqualFlag),
                        0x21 => Vector128.AndNot(equal, notEqualFlag),
                        0x22 => ~equal | notEqualFlag,
                        _ => Vector128.GreaterThan(a, vte) | Vector128.AndNot(equal, carry & notEqualFlag),
                    };

                    var selected = Vector128.ConditionalSelect(result, a, vte);
                    _accL = selected;
                    d = selected;
                    _vcc = (ushort)Vector128.ExtractMostSignificantBits(result);
                    _vco = 0;
                    break;
                }

            case 0x24: ClipLow(a, vte, vd); break; // VCL
            case 0x25: ClipHigh(a, vte, vd); break; // VCH
            case 0x26: ClipReciprocal(a, vte, vd); break; // VCR
            case 0x27: // VMRG
                {
                    var selected = Vector128.ConditionalSelect(FlagMasks[_vcc & 0xFF], a, vte);
                    _accL = selected;
                    d = selected;
                    _vco = 0;
                    break;
                }

            case 0x28: _accL = a & vte; d = _accL; break; // VAND
            case 0x29: _accL = ~(a & vte); d = _accL; break; // VNAND
            case 0x2A: _accL = a | vte; d = _accL; break; // VOR
            case 0x2B: _accL = ~(a | vte); d = _accL; break; // VNOR
            case 0x2C: _accL = a ^ vte; d = _accL; break; // VXOR
            case 0x2D: _accL = ~(a ^ vte); d = _accL; break; // VNXOR
            case 0x30: Reciprocal(vt, e, vs, vd, vte, squareRoot: false, low: false); break; // VRCP
            case 0x31: Reciprocal(vt, e, vs, vd, vte, squareRoot: false, low: true); break; // VRCPL
            case 0x32: // VRCPH
            case 0x36: // VRSQH
                _accL = vte;
                _divInLoaded = true;
                _divIn = Lane(vt, e & 7);
                Lane(vd, vs & 7) = _divOut;
                break;
            case 0x33: // VMOV
                _accL = vte;
                Lane(vd, vs & 7) = vte.GetElement(vs & 7);
                break;
            case 0x34: Reciprocal(vt, e, vs, vd, vte, squareRoot: true, low: false); break; // VRSQ
            case 0x35: Reciprocal(vt, e, vs, vd, vte, squareRoot: true, low: true); break; // VRSQL
            case 0x37: // VNOP
                return;
            case 0x3F: // VNULL: clears the accumulator's low lane
                _accL = Vector128<short>.Zero;
                return;
            case 0x1C: // VSUM
            case 0x1E:
            case 0x1F:
            case 0x3C: // VINST
            case 0x3D: // VINSQ
            case 0x3E: // VINSN
                // These reserved opcodes add the previous vector instruction's vt operand into the accumulator's low
                // lane (PeterLemon's RSPTest VSAC, VACC, VEXTT/Q/N record this), and write zero.
                _accL += _lastVte;
                d = Vector128<short>.Zero;
                break;
            default:
                // The other reserved opcodes (VSUT, VADDB, VSUBB, VACCB, VSUCB, VSAD, VSAC, VEXTT/Q/N and the unnamed
                // ones): the adder still runs into the accumulator's low lane, and the result is zero.
                _accL = a + vte;
                d = Vector128<short>.Zero;
                break;
        }

        _lastVte = vte;
    }

    /// <summary>VMULQ (MPEG): the product, rounded toward zero by 31 when negative, into the accumulator's upper 32 bits.</summary>
    private void MultiplyQuantized(int vs, int vd, Vector128<short> vte)
    {
        Span<long> accumulators = stackalloc long[8];
        for (var n = 0; n < 8; n++)
        {
            var product = Lane(vs, n) * vte.GetElement(n);
            if (product < 0)
            {
                product += 31;
            }

            accumulators[n] = (long)product << 16;
            Lane(vd, n) = (short)(ClampSigned(product >> 1) & ~15);
        }

        WriteAccumulators(accumulators);
    }

    /// <summary>VMACQ (MPEG): rounds the accumulator's upper 32 bits by 32 toward zero, unless bit 5 is set.</summary>
    private void MultiplyAccumulateQuantized(int vd)
    {
        Span<long> accumulators = stackalloc long[8];
        ReadAccumulators(accumulators);
        for (var n = 0; n < 8; n++)
        {
            var product = (int)(accumulators[n] >> 16);
            if (product < 0 && (product & (1 << 5)) == 0)
            {
                product += 32;
            }
            else if (product >= 32 && (product & (1 << 5)) == 0)
            {
                product -= 32;
            }

            accumulators[n] = ((long)product << 16) | (ushort)accumulators[n];
            Lane(vd, n) = (short)(ClampSigned(product >> 1) & ~15);
        }

        WriteAccumulators(accumulators);
    }

    /// <summary>
    /// VRNDP/VRNDN (MPEG): adds vt (shifted up 16 when the vs field is odd) to accumulators that are non-negative
    /// (VRNDP) or negative (VRNDN).
    /// </summary>
    private void Round(uint instruction, Vector128<short> vte, bool positive)
    {
        var vsField = (int)((instruction >> 11) & 31);
        var vd = (int)((instruction >> 6) & 31);
        Span<long> accumulators = stackalloc long[8];
        ReadAccumulators(accumulators);
        for (var n = 0; n < 8; n++)
        {
            long product = vte.GetElement(n);
            if ((vsField & 1) != 0)
            {
                product <<= 16;
            }

            var acc = accumulators[n];
            if (positive ? acc >= 0 : acc < 0)
            {
                accumulators[n] = Wrap48(acc + product);
            }

            Lane(vd, n) = (short)SaturateLane(accumulators[n], false, 0x8000, 0x7FFF);
        }

        WriteAccumulators(accumulators);
    }

    /// <summary>VCL: the low half of a double-precision clip, using the flags VCH left.</summary>
    private void ClipLow(Vector128<short> source, Vector128<short> vte, int vd)
    {
        Span<short> results = stackalloc short[8];
        var vcc = _vcc;
        for (var n = 0; n < 8; n++)
        {
            var a = (ushort)source.GetElement(n);
            var b = (ushort)vte.GetElement(n);
            var low = 1 << n;
            var high = 1 << (n + 8);
            ushort result;
            if ((_vco & low) != 0)
            {
                if ((_vco & high) != 0)
                {
                    result = (vcc & low) != 0 ? (ushort)-b : a;
                }
                else
                {
                    var sum = a + b;
                    var carry = sum > 0xFFFF;
                    var zero = (ushort)sum == 0;
                    var lessOrEqual = (_vce & (1 << n)) != 0 ? zero || !carry : zero && !carry;
                    vcc = (ushort)(lessOrEqual ? vcc | low : vcc & ~low);
                    result = lessOrEqual ? (ushort)-b : a;
                }
            }
            else
            {
                if ((_vco & high) != 0)
                {
                    result = (vcc & high) != 0 ? b : a;
                }
                else
                {
                    var greaterOrEqual = a - b >= 0;
                    vcc = (ushort)(greaterOrEqual ? vcc | high : vcc & ~high);
                    result = greaterOrEqual ? b : a;
                }
            }

            results[n] = (short)result;
        }

        _accL = Vector128.Create((ReadOnlySpan<short>)results);
        Register(vd) = _accL;
        _vcc = vcc;
        _vco = 0;
        _vce = 0;
    }

    /// <summary>VCH: the high half of a double-precision clip (or a single-precision clip), setting VCO, VCC and VCE.</summary>
    private void ClipHigh(Vector128<short> source, Vector128<short> vte, int vd)
    {
        Span<short> results = stackalloc short[8];
        int vco = 0, vcc = 0, vce = 0;
        for (var n = 0; n < 8; n++)
        {
            var a = source.GetElement(n);
            var b = vte.GetElement(n);
            short result;
            if ((a ^ b) < 0)
            {
                var sum = (short)(a + b);
                result = sum <= 0 ? (short)-b : a;
                vcc |= (sum <= 0 ? 1 : 0) << n | (b < 0 ? 1 : 0) << (n + 8);
                vco |= 1 << n | (sum != 0 && (ushort)a != ((ushort)b ^ 0xFFFF) ? 1 : 0) << (n + 8);
                vce |= (sum == -1 ? 1 : 0) << n;
            }
            else
            {
                var difference = (short)(a - b);
                result = difference >= 0 ? b : a;
                vcc |= (b < 0 ? 1 : 0) << n | (difference >= 0 ? 1 : 0) << (n + 8);
                vco |= (difference != 0 && (ushort)a != ((ushort)b ^ 0xFFFF) ? 1 : 0) << (n + 8);
            }

            results[n] = result;
        }

        _accL = Vector128.Create((ReadOnlySpan<short>)results);
        Register(vd) = _accL;
        _vco = (ushort)vco;
        _vcc = (ushort)vcc;
        _vce = (byte)vce;
    }

    /// <summary>VCR: a single-precision clip against a one's-complement range.</summary>
    private void ClipReciprocal(Vector128<short> source, Vector128<short> vte, int vd)
    {
        Span<short> results = stackalloc short[8];
        var vcc = 0;
        for (var n = 0; n < 8; n++)
        {
            var a = source.GetElement(n);
            var b = vte.GetElement(n);
            short result;
            if ((a ^ b) < 0)
            {
                var lessOrEqual = a + b + 1 <= 0;
                vcc |= (lessOrEqual ? 1 : 0) << n | (b < 0 ? 1 : 0) << (n + 8);
                result = lessOrEqual ? (short)~b : a;
            }
            else
            {
                var greaterOrEqual = a - b >= 0;
                vcc |= (b < 0 ? 1 : 0) << n | (greaterOrEqual ? 1 : 0) << (n + 8);
                result = greaterOrEqual ? b : a;
            }

            results[n] = result;
        }

        _accL = Vector128.Create((ReadOnlySpan<short>)results);
        Register(vd) = _accL;
        _vcc = (ushort)vcc;
        _vco = 0;
        _vce = 0;
    }

    /// <summary>
    /// VRCP/VRCPL/VRSQ/VRSQL: looks the input up in the reciprocal (or inverse square root) ROM and writes the low 16
    /// bits of the 32-bit result to lane <paramref name="vsField"/> of vd; the high 16 bits wait in DIVOUT. The "L" forms
    /// take a 32-bit input when VRCPH/VRSQH loaded the high half first.
    /// </summary>
    private void Reciprocal(int vt, int e, int vsField, int vd, Vector128<short> vte, bool squareRoot, bool low)
    {
        var lane = (ushort)Lane(vt, e & 7);
        var input = low && _divInLoaded ? _divIn << 16 | lane : (short)lane;
        var mask = input >> 31;
        var data = input ^ mask;
        if (input > short.MinValue)
        {
            data -= mask;
        }

        int result;
        if (data == 0)
        {
            result = 0x7FFFFFFF;
        }
        else if (input == short.MinValue)
        {
            result = unchecked((int)0xFFFF0000);
        }
        else
        {
            var shift = BitOperations.LeadingZeroCount((uint)data);
            var index = (int)((((ulong)(uint)data << shift) & 0x7FC00000) >> 22);
            if (squareRoot)
            {
                result = (0x10000 | InverseSquareRoots[(index & 0x1FE) | (shift & 1)]) << 14;
                result = (int)((uint)result >> ((31 - shift) >> 1)) ^ mask;
            }
            else
            {
                result = (0x10000 | Reciprocals[index]) << 14;
                result = (int)((uint)result >> (31 - shift)) ^ mask;
            }
        }

        _divInLoaded = false;
        _divOut = (short)(result >> 16);
        _accL = vte;
        Lane(vd, vsField & 7) = (short)result;
    }

    // ---- Loads and stores (LWC2/SWC2) -----------------------------------------------------------------------------

    private void LoadVector(uint instruction, int rs, int vt)
    {
        var e = (int)((instruction >> 7) & 15);
        var offset = (int)((instruction & 0x7F) ^ 0x40) - 0x40; // signed 7 bits
        var baseAddress = G(rs);
        switch ((instruction >> 11) & 31)
        {
            case 0x00: LoadBytes(vt, e, baseAddress + (uint)offset, 1); break; // LBV
            case 0x01: LoadBytes(vt, e, baseAddress + (uint)(offset * 2), 2); break; // LSV
            case 0x02: LoadBytes(vt, e, baseAddress + (uint)(offset * 4), 4); break; // LLV
            case 0x03: LoadBytes(vt, e, baseAddress + (uint)(offset * 8), 8); break; // LDV
            case 0x04: // LQV: up to the end of the 16-byte block
                {
                    var address = baseAddress + (uint)(offset * 16);
                    if (e == 0 && (address & 15) == 0)
                    {
                        Register(vt) = ReadQuad((int)(address & 0xFF0));
                        break;
                    }

                    var end = Math.Min(16 + e - (int)(address & 15), 16);
                    for (var i = e; i < end; i++)
                    {
                        SetByte(vt, i, Dmem[address++ & 0xFFF]);
                    }

                    break;
                }

            case 0x05: // LRV: from the start of the 16-byte block up to the address
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var start = 16 - ((int)(address & 15) - e);
                    address &= ~15u;
                    for (var i = start; i < 16; i++)
                    {
                        SetByte(vt, i, Dmem[address++ & 0xFFF]);
                    }

                    break;
                }

            case 0x06: LoadPacked(vt, e, baseAddress + (uint)(offset * 8), 8, 1); break; // LPV
            case 0x07: LoadPacked(vt, e, baseAddress + (uint)(offset * 8), 7, 1); break; // LUV
            case 0x08: LoadPacked(vt, e, baseAddress + (uint)(offset * 16), 7, 2); break; // LHV
            case 0x09: LoadFourth(vt, e, baseAddress + (uint)(offset * 16)); break; // LFV
            case 0x0A: break; // LWV: does nothing on hardware (PeterLemon RSPTest LWV)
            case 0x0B: LoadTranspose(vt, e, baseAddress + (uint)(offset * 16)); break; // LTV
            default: break;
        }
    }

    private void LoadBytes(int vt, int e, uint address, int count)
    {
        var end = Math.Min(e + count, 16);
        for (var i = e; i < end; i++)
        {
            SetByte(vt, i, Dmem[address++ & 0xFFF]);
        }
    }

    /// <summary>LPV/LUV/LHV: one byte per lane, from an 8-byte-aligned block rotated by the element.</summary>
    private void LoadPacked(int vt, int e, uint address, int shift, int stride)
    {
        var index = (int)(address & 7) - e;
        address &= ~7u;
        for (var n = 0; n < 8; n++)
        {
            Lane(vt, n) = (short)(Dmem[(address + (uint)((index + n * stride) & 15)) & 0xFFF] << shift);
        }
    }

    /// <summary>LFV: every fourth byte into four lanes, then the bytes from the element on copied into vt.</summary>
    private void LoadFourth(int vt, int e, uint address)
    {
        Span<ushort> scratch = stackalloc ushort[8];
        var index = (int)(address & 7) - e;
        address &= ~7u;
        for (var n = 0; n < 4; n++)
        {
            scratch[n] = (ushort)(Dmem[(address + (uint)((index + n * 4) & 15)) & 0xFFF] << 7);
            scratch[n + 4] = (ushort)(Dmem[(address + (uint)((index + n * 4 + 8) & 15)) & 0xFFF] << 7);
        }

        var end = Math.Min(e + 8, 16);
        for (var i = e; i < end; i++)
        {
            var lane = scratch[i >> 1];
            SetByte(vt, i, (byte)((i & 1) == 0 ? lane >> 8 : lane));
        }
    }

    /// <summary>LTV: a diagonal of a block of eight registers, read from a 16-byte block that wraps.</summary>
    private void LoadTranspose(int vt, int e, uint address)
    {
        var begin = address & ~7u;
        address = begin + (uint)((e + (int)(address & 8)) & 15);
        var registerBase = vt & ~7;
        var registerOffset = e >> 1;
        for (var i = 0; i < 8; i++)
        {
            var register = registerBase + registerOffset;
            SetByte(register, i * 2, Dmem[address++ & 0xFFF]);
            if (address == begin + 16)
            {
                address = begin;
            }

            SetByte(register, i * 2 + 1, Dmem[address++ & 0xFFF]);
            if (address == begin + 16)
            {
                address = begin;
            }

            registerOffset = (registerOffset + 1) & 7;
        }
    }

    private void StoreVector(uint instruction, int rs, int vt)
    {
        var e = (int)((instruction >> 7) & 15);
        var offset = (int)((instruction & 0x7F) ^ 0x40) - 0x40;
        var baseAddress = G(rs);
        switch ((instruction >> 11) & 31)
        {
            case 0x00: StoreBytes(vt, e, baseAddress + (uint)offset, 1); break; // SBV
            case 0x01: StoreBytes(vt, e, baseAddress + (uint)(offset * 2), 2); break; // SSV
            case 0x02: StoreBytes(vt, e, baseAddress + (uint)(offset * 4), 4); break; // SLV
            case 0x03: StoreBytes(vt, e, baseAddress + (uint)(offset * 8), 8); break; // SDV
            case 0x04: // SQV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    if (e == 0 && (address & 15) == 0)
                    {
                        WriteQuad((int)(address & 0xFF0), Register(vt));
                        break;
                    }

                    var end = e + (16 - (int)(address & 15));
                    for (var i = e; i < end; i++)
                    {
                        Dmem[address++ & 0xFFF] = GetByte(vt, i & 15);
                    }

                    break;
                }

            case 0x05: // SRV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var end = e + (int)(address & 15);
                    var rotate = 16 - (int)(address & 15);
                    address &= ~15u;
                    for (var i = e; i < end; i++)
                    {
                        Dmem[address++ & 0xFFF] = GetByte(vt, (i + rotate) & 15);
                    }

                    break;
                }

            case 0x06: StorePacked(vt, e, baseAddress + (uint)(offset * 8), unsigned: false); break; // SPV
            case 0x07: StorePacked(vt, e, baseAddress + (uint)(offset * 8), unsigned: true); break; // SUV
            case 0x08: // SHV
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var index = (int)(address & 7);
                    address &= ~7u;
                    for (var n = 0; n < 8; n++)
                    {
                        var b = e + n * 2;
                        var value = (byte)(GetByte(vt, b & 15) << 1 | GetByte(vt, (b + 1) & 15) >> 7);
                        Dmem[(address + (uint)((index + n * 2) & 15)) & 0xFFF] = value;
                    }

                    break;
                }

            case 0x09: StoreFourth(vt, e, baseAddress + (uint)(offset * 16)); break; // SFV
            case 0x0A: // SWV: the register rotated by the element, wrapping within an 8-byte-aligned 16-byte block
                {
                    var address = baseAddress + (uint)(offset * 16);
                    var position = (int)(address & 7);
                    address &= ~7u;
                    for (var i = e; i < e + 16; i++)
                    {
                        Dmem[(address + (uint)(position & 15)) & 0xFFF] = GetByte(vt, i & 15);
                        position++;
                    }

                    break;
                }

            case 0x0B: StoreTranspose(vt, e, baseAddress + (uint)(offset * 16)); break; // STV
            default: break;
        }
    }

    private void StoreBytes(int vt, int e, uint address, int count)
    {
        for (var i = e; i < e + count; i++)
        {
            Dmem[address++ & 0xFFF] = GetByte(vt, i & 15);
        }
    }

    /// <summary>SPV/SUV: lanes 0–7 packed one byte each, as signed (SPV, the upper byte) or unsigned (SUV, bits 14–7).</summary>
    private void StorePacked(int vt, int e, uint address, bool unsigned)
    {
        for (var i = e; i < e + 8; i++)
        {
            var lane = (ushort)Lane(vt, i & 7);
            var upperByte = (byte)(lane >> 8);
            var shifted = (byte)(lane >> 7);
            var firstHalf = (i & 15) < 8;
            Dmem[address++ & 0xFFF] = firstHalf != unsigned ? upperByte : shifted;
        }
    }

    /// <summary>SFV: four lanes (bits 14–7), every fourth byte; which lanes depends on the element, and others store zeros.</summary>
    private void StoreFourth(int vt, int e, uint address)
    {
        var index = (int)(address & 7);
        address &= ~7u;
        ReadOnlySpan<int> order = e switch
        {
            0 or 15 => [0, 1, 2, 3],
            1 => [6, 7, 4, 5],
            4 => [1, 2, 3, 0],
            5 => [7, 4, 5, 6],
            8 => [4, 5, 6, 7],
            11 => [3, 0, 1, 2],
            12 => [5, 6, 7, 4],
            _ => [-1, -1, -1, -1],
        };

        for (var n = 0; n < 4; n++)
        {
            var value = order[n] < 0 ? (byte)0 : (byte)((ushort)Lane(vt, order[n]) >> 7);
            Dmem[(address + (uint)((index + n * 4) & 15)) & 0xFFF] = value;
        }
    }

    /// <summary>STV: a diagonal of a block of eight registers, written to a 16-byte block that wraps.</summary>
    private void StoreTranspose(int vt, int e, uint address)
    {
        var start = vt & ~7;
        var element = 16 - (e & ~1);
        var position = (int)(address & 7) - (e & ~1);
        address &= ~7u;
        for (var register = start; register < start + 8; register++)
        {
            Dmem[(address + (uint)(position++ & 15)) & 0xFFF] = GetByte(register, element++ & 15);
            Dmem[(address + (uint)(position++ & 15)) & 0xFFF] = GetByte(register, element++ & 15);
        }
    }

    // The state keeps the layout of the lane-by-lane version: registers as 256 lanes, then the eight 48-bit accumulators.
    private void SaveVectorState(StateWriter writer)
    {
        for (var register = 0; register < 32; register++)
        {
            for (var n = 0; n < 8; n++)
            {
                writer.WriteUInt16((ushort)Lane(register, n));
            }
        }

        for (var n = 0; n < 8; n++)
        {
            writer.WriteInt64(Accumulator(n));
        }

        writer.WriteUInt16(_vco);
        writer.WriteUInt16(_vcc);
        writer.WriteByte(_vce);
        writer.WriteUInt16((ushort)_divIn);
        writer.WriteUInt16((ushort)_divOut);
        writer.WriteBool(_divInLoaded);
        for (var n = 0; n < 8; n++)
        {
            writer.WriteUInt16((ushort)_lastVte.GetElement(n));
        }
    }

    private void LoadVectorState(StateReader reader)
    {
        for (var register = 0; register < 32; register++)
        {
            for (var n = 0; n < 8; n++)
            {
                Lane(register, n) = (short)reader.ReadUInt16();
            }
        }

        Span<long> accumulators = stackalloc long[8];
        for (var n = 0; n < 8; n++)
        {
            accumulators[n] = Wrap48(reader.ReadInt64());
        }

        WriteAccumulators(accumulators);
        _vco = reader.ReadUInt16();
        _vcc = reader.ReadUInt16();
        _vce = reader.ReadByte();
        _divIn = (short)reader.ReadUInt16();
        _divOut = (short)reader.ReadUInt16();
        _divInLoaded = reader.ReadBool();
        Span<short> lastVte = stackalloc short[8];
        for (var n = 0; n < 8; n++)
        {
            lastVte[n] = (short)reader.ReadUInt16();
        }

        _lastVte = Vector128.Create((ReadOnlySpan<short>)lastVte);
    }
}
