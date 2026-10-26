using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// The MIPS III instructions as the R5900 has them (ps2tek: "EE Instruction Decoding"): no LL/SC, no 64-bit
/// multiply or divide, no doubleword FPU loads; plus the three-operand MULT/MULTU, MADD/MADDU, the SA register and
/// LQ/SQ. The MIPS III instructions read and write the low 64 bits of a GPR and leave its high 64 bits alone.
/// </summary>
public sealed partial class R5900Cpu
{
    // Multiply and divide latencies in EE cycles (the EE Core User's Manual: MULT/MADD 4, DIV 37).
    private const int MultCycles = 4;
    private const int DivCycles = 37;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExtend32(uint value) => (ulong)(long)(int)value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref ulong Gpr(int index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), index * 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref ulong GprHigh(int index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), index * 2 + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Execute(uint op)
    {
        Dispatch(op);
        _gpr[0] = 0;
        _gpr[1] = 0;
    }

    private void Dispatch(uint op)
    {
        var rs = (int)(op >> 21) & 31;
        var rt = (int)(op >> 16) & 31;
        switch (op >> 26)
        {
            case 0x00:
                ExecuteSpecial(op, rs, rt);
                break;
            case 0x01:
                ExecuteRegimm(op, rs, rt);
                break;
            case 0x02: // J
                Branch(true, ((_currentPc + 4) & 0xF000_0000) | ((op & 0x03FF_FFFF) << 2));
                break;
            case 0x03: // JAL
                Gpr(31) = SignExtend32(_currentPc + 8);
                Branch(true, ((_currentPc + 4) & 0xF000_0000) | ((op & 0x03FF_FFFF) << 2));
                break;
            case 0x04: // BEQ
                Branch(Gpr(rs) == Gpr(rt), BranchTarget(op));
                break;
            case 0x05: // BNE
                Branch(Gpr(rs) != Gpr(rt), BranchTarget(op));
                break;
            case 0x06: // BLEZ
                Branch((long)Gpr(rs) <= 0, BranchTarget(op));
                break;
            case 0x07: // BGTZ
                Branch((long)Gpr(rs) > 0, BranchTarget(op));
                break;
            case 0x08: // ADDI
            {
                var a = (int)Gpr(rs);
                var b = (int)(short)op;
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rt) = (ulong)(long)sum;
                break;
            }

            case 0x09: // ADDIU
                Gpr(rt) = SignExtend32((uint)Gpr(rs) + (uint)(short)op);
                break;
            case 0x0A: // SLTI
                Gpr(rt) = (long)Gpr(rs) < (short)op ? 1UL : 0UL;
                break;
            case 0x0B: // SLTIU
                Gpr(rt) = Gpr(rs) < (ulong)(long)(short)op ? 1UL : 0UL;
                break;
            case 0x0C: // ANDI
                Gpr(rt) = Gpr(rs) & (op & 0xFFFF);
                break;
            case 0x0D: // ORI
                Gpr(rt) = Gpr(rs) | (op & 0xFFFF);
                break;
            case 0x0E: // XORI
                Gpr(rt) = Gpr(rs) ^ (op & 0xFFFF);
                break;
            case 0x0F: // LUI
                Gpr(rt) = SignExtend32(op << 16);
                break;
            case 0x10:
                ExecuteCop0(op, rs, rt);
                break;
            case 0x11:
                ExecuteCop1(op, rs, rt);
                break;
            case 0x12:
                ExecuteCop2(op, rs, rt);
                break;
            case 0x14: // BEQL
                BranchLikely(Gpr(rs) == Gpr(rt), BranchTarget(op));
                break;
            case 0x15: // BNEL
                BranchLikely(Gpr(rs) != Gpr(rt), BranchTarget(op));
                break;
            case 0x16: // BLEZL
                BranchLikely((long)Gpr(rs) <= 0, BranchTarget(op));
                break;
            case 0x17: // BGTZL
                BranchLikely((long)Gpr(rs) > 0, BranchTarget(op));
                break;
            case 0x18: // DADDI
            {
                var a = (long)Gpr(rs);
                var b = (long)(short)op;
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rt) = (ulong)sum;
                break;
            }

            case 0x19: // DADDIU
                Gpr(rt) = Gpr(rs) + (ulong)(long)(short)op;
                break;
            case 0x1A: // LDL
            {
                var address = Address(op, rs);
                if (!Load64(address & ~7u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                Gpr(rt) = (Gpr(rt) & (0x00FF_FFFF_FFFF_FFFFUL >> shift)) | (data << (56 - shift));
                break;
            }

            case 0x1B: // LDR
            {
                var address = Address(op, rs);
                if (!Load64(address & ~7u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                var keep = shift == 0 ? 0UL : 0xFFFF_FFFF_FFFF_FF00UL << (56 - shift);
                Gpr(rt) = (Gpr(rt) & keep) | (data >> shift);
                break;
            }

            case 0x1C:
                ExecuteMmi(op, rs, rt);
                break;
            case 0x1E: // LQ
            {
                if (Load128(Address(op, rs), out var low, out var high))
                {
                    Gpr(rt) = low;
                    GprHigh(rt) = high;
                }

                break;
            }

            case 0x1F: // SQ
                Store128(Address(op, rs), Gpr(rt), GprHigh(rt));
                break;
            case 0x20: // LB
            {
                if (Load8(Address(op, rs), out var value))
                {
                    Gpr(rt) = (ulong)(long)(sbyte)value;
                }

                break;
            }

            case 0x21: // LH
            {
                if (Load16(Address(op, rs), out var value))
                {
                    Gpr(rt) = (ulong)(long)(short)value;
                }

                break;
            }

            case 0x22: // LWL
            {
                var address = Address(op, rs);
                if (!Load32(address & ~3u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                Gpr(rt) = SignExtend32(((uint)Gpr(rt) & (0x00FF_FFFFu >> shift)) | (data << (24 - shift)));
                break;
            }

            case 0x23: // LW
            {
                if (Load32(Address(op, rs), out var value))
                {
                    Gpr(rt) = SignExtend32(value);
                }

                break;
            }

            case 0x24: // LBU
            {
                if (Load8(Address(op, rs), out var value))
                {
                    Gpr(rt) = value;
                }

                break;
            }

            case 0x25: // LHU
            {
                if (Load16(Address(op, rs), out var value))
                {
                    Gpr(rt) = value;
                }

                break;
            }

            case 0x26: // LWR
            {
                var address = Address(op, rs);
                if (!Load32(address & ~3u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                if (shift == 0)
                {
                    Gpr(rt) = SignExtend32(data); // the whole word: sign-extended
                }
                else
                {
                    // Part of a word: only the low 32 bits change (the EE Core manual's LWR).
                    var merged = ((uint)Gpr(rt) & (0xFFFF_FF00u << (24 - shift))) | (data >> shift);
                    Gpr(rt) = (Gpr(rt) & 0xFFFF_FFFF_0000_0000UL) | merged;
                }

                break;
            }

            case 0x27: // LWU
            {
                if (Load32(Address(op, rs), out var value))
                {
                    Gpr(rt) = value;
                }

                break;
            }

            case 0x28: // SB
                Store8(Address(op, rs), (byte)Gpr(rt));
                break;
            case 0x29: // SH
                Store16(Address(op, rs), (ushort)Gpr(rt));
                break;
            case 0x2A: // SWL
            {
                var address = Address(op, rs);
                if (!Load32(address & ~3u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                var keep = shift == 24 ? 0u : 0xFFFF_FF00u << shift;
                Store32(address & ~3u, (data & keep) | ((uint)Gpr(rt) >> (24 - shift)));
                break;
            }

            case 0x2B: // SW
                Store32(Address(op, rs), (uint)Gpr(rt));
                break;
            case 0x2C: // SDL
            {
                var address = Address(op, rs);
                if (!Load64(address & ~7u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                var keep = shift == 56 ? 0UL : 0xFFFF_FFFF_FFFF_FF00UL << shift;
                Store64(address & ~7u, (data & keep) | (Gpr(rt) >> (56 - shift)));
                break;
            }

            case 0x2D: // SDR
            {
                var address = Address(op, rs);
                if (!Load64(address & ~7u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                var keep = shift == 0 ? 0UL : 0x00FF_FFFF_FFFF_FFFFUL >> (56 - shift);
                Store64(address & ~7u, (data & keep) | (Gpr(rt) << shift));
                break;
            }

            case 0x2E: // SWR
            {
                var address = Address(op, rs);
                if (!Load32(address & ~3u, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                var keep = shift == 0 ? 0u : 0x00FF_FFFFu >> (24 - shift);
                Store32(address & ~3u, (data & keep) | ((uint)Gpr(rt) << shift));
                break;
            }

            case 0x2F: // CACHE: no cache model, so nothing to do
            case 0x33: // PREF
                break;
            case 0x31: // LWC1
            {
                if (!Cop1Usable())
                {
                    return;
                }

                if (Load32(Address(op, rs), out var value))
                {
                    FpuWait();
                    _fpr[rt] = value;
                }

                break;
            }

            case 0x36: // LQC2
            {
                if (!Cop2Usable())
                {
                    return;
                }

                if (Load128(Address(op, rs), out var low, out var high))
                {
                    _cop2?.WriteVf(rt, new UInt128(high, low));
                }

                break;
            }

            case 0x37: // LD
            {
                if (Load64(Address(op, rs), out var value))
                {
                    Gpr(rt) = value;
                }

                break;
            }

            case 0x39: // SWC1
                if (Cop1Usable())
                {
                    Store32(Address(op, rs), _fpr[rt]);
                }

                break;
            case 0x3E: // SQC2
            {
                if (!Cop2Usable())
                {
                    return;
                }

                var value = _cop2?.ReadVf(rt) ?? UInt128.Zero;
                Store128(Address(op, rs), (ulong)value, (ulong)(value >> 64));
                break;
            }

            case 0x3F: // SD
                Store64(Address(op, rs), Gpr(rt));
                break;
            default: // COP3, LL/SC, LDC1/SDC1… don't exist on the EE
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Address(uint op, int rs) => (uint)Gpr(rs) + (uint)(short)op;

    private void ExecuteSpecial(uint op, int rs, int rt)
    {
        var rd = (int)(op >> 11) & 31;
        var sa = (int)(op >> 6) & 31;
        switch (op & 0x3F)
        {
            case 0x00: // SLL
                Gpr(rd) = SignExtend32((uint)Gpr(rt) << sa);
                break;
            case 0x02: // SRL
                Gpr(rd) = SignExtend32((uint)Gpr(rt) >> sa);
                break;
            case 0x03: // SRA
                Gpr(rd) = (ulong)(long)((int)Gpr(rt) >> sa);
                break;
            case 0x04: // SLLV
                Gpr(rd) = SignExtend32((uint)Gpr(rt) << (int)(Gpr(rs) & 31));
                break;
            case 0x06: // SRLV
                Gpr(rd) = SignExtend32((uint)Gpr(rt) >> (int)(Gpr(rs) & 31));
                break;
            case 0x07: // SRAV
                Gpr(rd) = (ulong)(long)((int)Gpr(rt) >> (int)(Gpr(rs) & 31));
                break;
            case 0x08: // JR
                Branch(true, (uint)Gpr(rs));
                break;
            case 0x09: // JALR
            {
                var target = (uint)Gpr(rs);
                Gpr(rd) = SignExtend32(_currentPc + 8);
                Branch(true, target);
                break;
            }

            case 0x0A: // MOVZ
                if (Gpr(rt) == 0)
                {
                    Gpr(rd) = Gpr(rs);
                }

                break;
            case 0x0B: // MOVN
                if (Gpr(rt) != 0)
                {
                    Gpr(rd) = Gpr(rs);
                }

                break;
            case 0x0C: // SYSCALL
                RaiseException(R5900Exception.Syscall);
                break;
            case 0x0D: // BREAK
                RaiseException(R5900Exception.Breakpoint);
                break;
            case 0x0F: // SYNC
                break;
            case 0x10: // MFHI
                WaitHiLo();
                Gpr(rd) = _hi;
                break;
            case 0x11: // MTHI
                _hi = Gpr(rs);
                break;
            case 0x12: // MFLO
                WaitHiLo();
                Gpr(rd) = _lo;
                break;
            case 0x13: // MTLO
                _lo = Gpr(rs);
                break;
            case 0x14: // DSLLV
                Gpr(rd) = Gpr(rt) << (int)(Gpr(rs) & 63);
                break;
            case 0x16: // DSRLV
                Gpr(rd) = Gpr(rt) >> (int)(Gpr(rs) & 63);
                break;
            case 0x17: // DSRAV
                Gpr(rd) = (ulong)((long)Gpr(rt) >> (int)(Gpr(rs) & 63));
                break;
            case 0x18: // MULT (rd gets LO too)
            {
                var product = (long)(int)Gpr(rs) * (int)Gpr(rt);
                SetHiLo(product, rd);
                break;
            }

            case 0x19: // MULTU
            {
                var product = (ulong)(uint)Gpr(rs) * (uint)Gpr(rt);
                SetHiLo((long)product, rd);
                break;
            }

            case 0x1A: // DIV
                Divide((int)Gpr(rs), (int)Gpr(rt), out _lo, out _hi);
                _hiLoReady = Cycles + DivCycles;
                break;
            case 0x1B: // DIVU
                DivideUnsigned((uint)Gpr(rs), (uint)Gpr(rt), out _lo, out _hi);
                _hiLoReady = Cycles + DivCycles;
                break;
            case 0x20: // ADD
            {
                var a = (int)Gpr(rs);
                var b = (int)Gpr(rt);
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rd) = (ulong)(long)sum;
                break;
            }

            case 0x21: // ADDU
                Gpr(rd) = SignExtend32((uint)Gpr(rs) + (uint)Gpr(rt));
                break;
            case 0x22: // SUB
            {
                var a = (int)Gpr(rs);
                var b = (int)Gpr(rt);
                var difference = a - b;
                if (((a ^ b) & (a ^ difference)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rd) = (ulong)(long)difference;
                break;
            }

            case 0x23: // SUBU
                Gpr(rd) = SignExtend32((uint)Gpr(rs) - (uint)Gpr(rt));
                break;
            case 0x24: // AND
                Gpr(rd) = Gpr(rs) & Gpr(rt);
                break;
            case 0x25: // OR
                Gpr(rd) = Gpr(rs) | Gpr(rt);
                break;
            case 0x26: // XOR
                Gpr(rd) = Gpr(rs) ^ Gpr(rt);
                break;
            case 0x27: // NOR
                Gpr(rd) = ~(Gpr(rs) | Gpr(rt));
                break;
            case 0x28: // MFSA
                Gpr(rd) = _sa;
                break;
            case 0x29: // MTSA: SA holds a byte count, 0–15 (ps2autotests cpu/ee_simd/funnel)
                _sa = (uint)Gpr(rs) & 0xF;
                break;
            case 0x2A: // SLT
                Gpr(rd) = (long)Gpr(rs) < (long)Gpr(rt) ? 1UL : 0UL;
                break;
            case 0x2B: // SLTU
                Gpr(rd) = Gpr(rs) < Gpr(rt) ? 1UL : 0UL;
                break;
            case 0x2C: // DADD
            {
                var a = (long)Gpr(rs);
                var b = (long)Gpr(rt);
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rd) = (ulong)sum;
                break;
            }

            case 0x2D: // DADDU
                Gpr(rd) = Gpr(rs) + Gpr(rt);
                break;
            case 0x2E: // DSUB
            {
                var a = (long)Gpr(rs);
                var b = (long)Gpr(rt);
                var difference = a - b;
                if (((a ^ b) & (a ^ difference)) < 0)
                {
                    RaiseException(R5900Exception.Overflow);
                    return;
                }

                Gpr(rd) = (ulong)difference;
                break;
            }

            case 0x2F: // DSUBU
                Gpr(rd) = Gpr(rs) - Gpr(rt);
                break;
            case 0x30: // TGE
                Trap((long)Gpr(rs) >= (long)Gpr(rt));
                break;
            case 0x31: // TGEU
                Trap(Gpr(rs) >= Gpr(rt));
                break;
            case 0x32: // TLT
                Trap((long)Gpr(rs) < (long)Gpr(rt));
                break;
            case 0x33: // TLTU
                Trap(Gpr(rs) < Gpr(rt));
                break;
            case 0x34: // TEQ
                Trap(Gpr(rs) == Gpr(rt));
                break;
            case 0x36: // TNE
                Trap(Gpr(rs) != Gpr(rt));
                break;
            case 0x38: // DSLL
                Gpr(rd) = Gpr(rt) << sa;
                break;
            case 0x3A: // DSRL
                Gpr(rd) = Gpr(rt) >> sa;
                break;
            case 0x3B: // DSRA
                Gpr(rd) = (ulong)((long)Gpr(rt) >> sa);
                break;
            case 0x3C: // DSLL32
                Gpr(rd) = Gpr(rt) << (sa + 32);
                break;
            case 0x3E: // DSRL32
                Gpr(rd) = Gpr(rt) >> (sa + 32);
                break;
            case 0x3F: // DSRA32
                Gpr(rd) = (ulong)((long)Gpr(rt) >> (sa + 32));
                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    private void ExecuteRegimm(uint op, int rs, int rt)
    {
        var value = (long)Gpr(rs);
        var immediate = (long)(short)op;
        switch (rt)
        {
            case 0x00: // BLTZ
                Branch(value < 0, BranchTarget(op));
                break;
            case 0x01: // BGEZ
                Branch(value >= 0, BranchTarget(op));
                break;
            case 0x02: // BLTZL
                BranchLikely(value < 0, BranchTarget(op));
                break;
            case 0x03: // BGEZL
                BranchLikely(value >= 0, BranchTarget(op));
                break;
            case 0x08: // TGEI
                Trap(value >= immediate);
                break;
            case 0x09: // TGEIU
                Trap((ulong)value >= (ulong)immediate);
                break;
            case 0x0A: // TLTI
                Trap(value < immediate);
                break;
            case 0x0B: // TLTIU
                Trap((ulong)value < (ulong)immediate);
                break;
            case 0x0C: // TEQI
                Trap(value == immediate);
                break;
            case 0x0E: // TNEI
                Trap(value != immediate);
                break;
            case 0x10: // BLTZAL
                Gpr(31) = SignExtend32(_currentPc + 8);
                Branch(value < 0, BranchTarget(op));
                break;
            case 0x11: // BGEZAL
                Gpr(31) = SignExtend32(_currentPc + 8);
                Branch(value >= 0, BranchTarget(op));
                break;
            case 0x12: // BLTZALL
                Gpr(31) = SignExtend32(_currentPc + 8);
                BranchLikely(value < 0, BranchTarget(op));
                break;
            case 0x13: // BGEZALL
                Gpr(31) = SignExtend32(_currentPc + 8);
                BranchLikely(value >= 0, BranchTarget(op));
                break;
            case 0x18: // MTSAB: SA = (rs ^ imm) & 15 bytes
                _sa = ((uint)value ^ (uint)immediate) & 0xF;
                break;
            case 0x19: // MTSAH: SA = ((rs ^ imm) & 7) halfwords
                _sa = (((uint)value ^ (uint)immediate) & 0x7) * 2;
                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    // ---- Multiply and divide ----

    /// <summary>HI/LO hold their low and high words sign-extended; the three-operand forms copy LO into rd.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetHiLo(long product, int rd)
    {
        _lo = SignExtend32((uint)product);
        _hi = SignExtend32((uint)(product >> 32));
        _hiLoReady = Cycles + MultCycles;
        Gpr(rd) = _lo;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetHiLo1(long product, int rd)
    {
        _lo1 = SignExtend32((uint)product);
        _hi1 = SignExtend32((uint)(product >> 32));
        _hiLo1Ready = Cycles + MultCycles;
        Gpr(rd) = _lo1;
    }

    /// <summary>
    /// DIV: by zero gives LO = −1 (1 for a negative dividend) and HI = the dividend; −2³¹ / −1 gives LO = −2³¹, HI = 0
    /// (ps2autotests cpu/ee/muldiv).
    /// </summary>
    private static void Divide(int dividend, int divisor, out ulong lo, out ulong hi)
    {
        if (divisor == 0)
        {
            lo = dividend >= 0 ? ulong.MaxValue : 1;
            hi = (ulong)(long)dividend;
        }
        else if (dividend == int.MinValue && divisor == -1)
        {
            lo = 0xFFFF_FFFF_8000_0000;
            hi = 0;
        }
        else
        {
            lo = (ulong)(long)(dividend / divisor);
            hi = (ulong)(long)(dividend % divisor);
        }
    }

    private static void DivideUnsigned(uint dividend, uint divisor, out ulong lo, out ulong hi)
    {
        if (divisor == 0)
        {
            lo = ulong.MaxValue;
            hi = SignExtend32(dividend);
        }
        else
        {
            lo = SignExtend32(dividend / divisor);
            hi = SignExtend32(dividend % divisor);
        }
    }

    /// <summary>Stalls until HI/LO hold the last multiply's or divide's result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WaitHiLo()
    {
        if (Cycles < _hiLoReady)
        {
            Cycles = _hiLoReady;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WaitHiLo1()
    {
        if (Cycles < _hiLo1Ready)
        {
            Cycles = _hiLo1Ready;
        }
    }

    // ---- Branches ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint BranchTarget(uint op) => _currentPc + 4 + ((uint)(int)(short)op << 2);

    /// <summary>A branch or jump: the next instruction is its delay slot; then <paramref name="target"/> if taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Branch(bool taken, uint target)
    {
        _nextIsDelaySlot = true;
        if (taken)
        {
            _nextPc = target;
            if (target == _currentPc)
            {
                SkipIdleLoop();
            }
        }
    }

    /// <summary>
    /// A branch to itself with a NOP in its delay slot only waits for an interrupt, which can only come at the next
    /// event: the CPU skips to the end of its slice. The cycles still pass, so Count is as if the loop had run.
    /// </summary>
    private void SkipIdleLoop()
    {
        if ((_pc & 0xFFFF_F000) != _fetchPage)
        {
            return; // only in a page the fetch cache holds, so peeking at the delay slot can't fault
        }

        var offset = (_fetchEntry & 0xFFFF_F000) | (_pc & 0xFFF);
        var delaySlot = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_fetchMemory), (nint)offset));
        if (delaySlot == 0 && _sliceEnd - 2 > Cycles && !_interruptPending)
        {
            Cycles = _sliceEnd - 2;
        }
    }

    /// <summary>A branch-likely: the delay slot only runs when the branch is taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BranchLikely(bool taken, uint target)
    {
        if (taken)
        {
            _nextIsDelaySlot = true;
            _nextPc = target;
        }
        else
        {
            _pc = _nextPc;
            _nextPc = _pc + 4;
        }
    }

    private void Trap(bool condition)
    {
        if (condition)
        {
            RaiseException(R5900Exception.Trap);
        }
    }
}
