using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.Cpu.R4300;

/// <summary>The MIPS III instructions (n64brew: "MIPS III instructions"; VR4300 manual chapter 16).</summary>
public sealed partial class R4300Cpu
{
    // Multiply and divide latencies in CPU cycles (VR4300 manual 16.x: MULT 5, DMULT 8, DIV 37, DDIV 69).
    private const int MultCycles = 4;
    private const int DMultCycles = 7;
    private const int DivCycles = 36;
    private const int DDivCycles = 68;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExtend32(uint value) => (ulong)(long)(int)value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExtend32(ulong value) => (ulong)(long)(int)(uint)value;

    private void Execute(uint op)
    {
        var rs = (int)(op >> 21) & 31;
        var rt = (int)(op >> 16) & 31;
        ref var gpr = ref MemoryMarshal.GetArrayDataReference(_gpr);
        switch (op >> 26)
        {
            case 0x00:
                ExecuteSpecial(op, rs, rt);
                break;
            case 0x01:
                ExecuteRegimm(op, rs, rt);
                break;
            case 0x02: // J
                Branch(true, (_pc & ~0x0FFF_FFFFUL) | ((op & 0x03FF_FFFF) << 2));
                break;
            case 0x03: // JAL
                Unsafe.Add(ref gpr, 31) = _currentPc + 8;
                Branch(true, (_pc & ~0x0FFF_FFFFUL) | ((op & 0x03FF_FFFF) << 2));
                break;
            case 0x04: // BEQ
                Branch(Unsafe.Add(ref gpr, rs) == Unsafe.Add(ref gpr, rt), BranchTarget(op));
                break;
            case 0x05: // BNE
                Branch(Unsafe.Add(ref gpr, rs) != Unsafe.Add(ref gpr, rt), BranchTarget(op));
                break;
            case 0x06: // BLEZ
                Branch((long)Unsafe.Add(ref gpr, rs) <= 0, BranchTarget(op));
                break;
            case 0x07: // BGTZ
                Branch((long)Unsafe.Add(ref gpr, rs) > 0, BranchTarget(op));
                break;
            case 0x08: // ADDI
            {
                var a = (int)Unsafe.Add(ref gpr, rs);
                var b = (int)(short)op;
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rt) = (ulong)(long)sum;
                break;
            }

            case 0x09: // ADDIU
                Unsafe.Add(ref gpr, rt) = SignExtend32((uint)Unsafe.Add(ref gpr, rs) + (uint)(short)op);
                break;
            case 0x0A: // SLTI
                Unsafe.Add(ref gpr, rt) = (long)Unsafe.Add(ref gpr, rs) < (short)op ? 1UL : 0UL;
                break;
            case 0x0B: // SLTIU
                Unsafe.Add(ref gpr, rt) = Unsafe.Add(ref gpr, rs) < (ulong)(long)(short)op ? 1UL : 0UL;
                break;
            case 0x0C: // ANDI
                Unsafe.Add(ref gpr, rt) = Unsafe.Add(ref gpr, rs) & (op & 0xFFFF);
                break;
            case 0x0D: // ORI
                Unsafe.Add(ref gpr, rt) = Unsafe.Add(ref gpr, rs) | (op & 0xFFFF);
                break;
            case 0x0E: // XORI
                Unsafe.Add(ref gpr, rt) = Unsafe.Add(ref gpr, rs) ^ (op & 0xFFFF);
                break;
            case 0x0F: // LUI
                Unsafe.Add(ref gpr, rt) = SignExtend32(op << 16);
                break;
            case 0x10:
                ExecuteCop0(op, rs, rt);
                break;
            case 0x11:
                ExecuteCop1(op, rs, rt);
                break;
            case 0x12: // COP2: there is no coprocessor 2
                RaiseException(R4300Exception.CoprocessorUnusable, 2);
                break;
            case 0x13: // COP3
                RaiseException(R4300Exception.ReservedInstruction, 3);
                break;
            case 0x14: // BEQL
                BranchLikely(Unsafe.Add(ref gpr, rs) == Unsafe.Add(ref gpr, rt), BranchTarget(op));
                break;
            case 0x15: // BNEL
                BranchLikely(Unsafe.Add(ref gpr, rs) != Unsafe.Add(ref gpr, rt), BranchTarget(op));
                break;
            case 0x16: // BLEZL
                BranchLikely((long)Unsafe.Add(ref gpr, rs) <= 0, BranchTarget(op));
                break;
            case 0x17: // BGTZL
                BranchLikely((long)Unsafe.Add(ref gpr, rs) > 0, BranchTarget(op));
                break;
            case 0x18: // DADDI
            {
                var a = (long)Unsafe.Add(ref gpr, rs);
                var b = (long)(short)op;
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rt) = (ulong)sum;
                break;
            }

            case 0x19: // DADDIU
                Unsafe.Add(ref gpr, rt) = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                break;
            case 0x1A: // LDL
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!LoadAligned64(address, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                var mask = shift == 0 ? 0UL : ulong.MaxValue >> (64 - shift);
                Unsafe.Add(ref gpr, rt) = (Unsafe.Add(ref gpr, rt) & mask) | (data << shift);
                break;
            }

            case 0x1B: // LDR
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!LoadAligned64(address, out var data))
                {
                    return;
                }

                var shift = (int)(7 - (address & 7)) * 8;
                var mask = shift == 0 ? 0UL : ulong.MaxValue << (64 - shift);
                Unsafe.Add(ref gpr, rt) = (Unsafe.Add(ref gpr, rt) & mask) | (data >> shift);
                break;
            }

            case 0x20: // LB
            {
                if (Load8(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = (ulong)(long)(sbyte)value;
                }

                break;
            }

            case 0x21: // LH
            {
                if (Load16(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = (ulong)(long)(short)value;
                }

                break;
            }

            case 0x22: // LWL
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!Load32(address & ~3UL, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                var mask = shift == 0 ? 0u : uint.MaxValue >> (32 - shift);
                Unsafe.Add(ref gpr, rt) = SignExtend32(((uint)Unsafe.Add(ref gpr, rt) & mask) | (data << shift));
                break;
            }

            case 0x23: // LW
            {
                if (Load32(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = SignExtend32(value);
                }

                break;
            }

            case 0x24: // LBU
            {
                if (Load8(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = value;
                }

                break;
            }

            case 0x25: // LHU
            {
                if (Load16(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = value;
                }

                break;
            }

            case 0x26: // LWR
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!Load32(address & ~3UL, out var data))
                {
                    return;
                }

                var shift = (int)(3 - (address & 3)) * 8;
                var mask = shift == 0 ? 0u : uint.MaxValue << (32 - shift);
                Unsafe.Add(ref gpr, rt) = SignExtend32(((uint)Unsafe.Add(ref gpr, rt) & mask) | (data >> shift));
                break;
            }

            case 0x27: // LWU
            {
                if (Load32(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = value;
                }

                break;
            }

            case 0x28: // SB
                Store8(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, (byte)Unsafe.Add(ref gpr, rt));
                break;
            case 0x29: // SH
                Store16(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, (ushort)Unsafe.Add(ref gpr, rt));
                break;
            case 0x2A: // SWL
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!Load32(address & ~3UL, out var data))
                {
                    return;
                }

                var shift = (int)(address & 3) * 8;
                var mask = shift == 0 ? 0u : uint.MaxValue << (32 - shift);
                Store32(address & ~3UL, (data & mask) | ((uint)Unsafe.Add(ref gpr, rt) >> shift));
                break;
            }

            case 0x2B: // SW
                Store32(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, (uint)Unsafe.Add(ref gpr, rt));
                break;
            case 0x2C: // SDL
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!LoadAligned64(address, out var data))
                {
                    return;
                }

                var shift = (int)(address & 7) * 8;
                var mask = shift == 0 ? 0UL : ulong.MaxValue << (64 - shift);
                Store64(address & ~7UL, (data & mask) | (Unsafe.Add(ref gpr, rt) >> shift));
                break;
            }

            case 0x2D: // SDR
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!LoadAligned64(address, out var data))
                {
                    return;
                }

                var shift = (int)(7 - (address & 7)) * 8;
                var mask = shift == 0 ? 0UL : ulong.MaxValue >> (64 - shift);
                Store64(address & ~7UL, (data & mask) | (Unsafe.Add(ref gpr, rt) << shift));
                break;
            }

            case 0x2E: // SWR
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (!Load32(address & ~3UL, out var data))
                {
                    return;
                }

                var shift = (int)(3 - (address & 3)) * 8;
                var mask = shift == 0 ? 0u : uint.MaxValue >> (32 - shift);
                Store32(address & ~3UL, (data & mask) | ((uint)Unsafe.Add(ref gpr, rt) << shift));
                break;
            }

            case 0x2F: // CACHE: the caches' contents aren't modelled
                if (!Cop0Usable)
                {
                    RaiseException(R4300Exception.CoprocessorUnusable, 0);
                }

                break;
            case 0x30: // LL
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (Load32(address, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = SignExtend32(value);
                    _llBit = true;
                    _llAddr = LinkedAddress(address);
                }

                break;
            }

            case 0x31: // LWC1
            {
                if (!Cop1Usable())
                {
                    return;
                }

                if (Load32(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Fpu.WriteWord(rt, value);
                }

                break;
            }

            case 0x34: // LLD
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (Load64(address, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = value;
                    _llBit = true;
                    _llAddr = LinkedAddress(address);
                }

                break;
            }

            case 0x35: // LDC1
            {
                if (!Cop1Usable())
                {
                    return;
                }

                if (Load64(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Fpu.WriteDoubleword(rt, value);
                }

                break;
            }

            case 0x37: // LD
            {
                if (Load64(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, out var value))
                {
                    Unsafe.Add(ref gpr, rt) = value;
                }

                break;
            }

            case 0x38: // SC
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (_llBit)
                {
                    if (!Store32(address, (uint)Unsafe.Add(ref gpr, rt)))
                    {
                        return;
                    }
                }
                else if ((address & 3) != 0)
                {
                    AddressError(address, R4300Exception.AddressErrorStore);
                    return;
                }

                Unsafe.Add(ref gpr, rt) = _llBit ? 1UL : 0UL;
                break;
            }

            case 0x39: // SWC1
                if (Cop1Usable())
                {
                    Store32(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, Fpu.ReadWord(rt));
                }

                break;
            case 0x3C: // SCD
            {
                var address = Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op;
                if (_llBit)
                {
                    if (!Store64(address, Unsafe.Add(ref gpr, rt)))
                    {
                        return;
                    }
                }
                else if ((address & 7) != 0)
                {
                    AddressError(address, R4300Exception.AddressErrorStore);
                    return;
                }

                Unsafe.Add(ref gpr, rt) = _llBit ? 1UL : 0UL;
                break;
            }

            case 0x3D: // SDC1
                if (Cop1Usable())
                {
                    Store64(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, Fpu.ReadDoubleword(rt));
                }

                break;
            case 0x3F: // SD
                Store64(Unsafe.Add(ref gpr, rs) + (ulong)(long)(short)op, Unsafe.Add(ref gpr, rt));
                break;
            case 0x32 or 0x36 or 0x3A or 0x3E: // LWC2, LDC2, SWC2, SDC2
                RaiseException(R4300Exception.CoprocessorUnusable, 2);
                break;
            default: // 0x1C–0x1F, 0x33, 0x3B
                RaiseException(R4300Exception.ReservedInstruction);
                break;
        }
    }

    /// <summary>LLAddr holds the physical address of the linked load, shifted right by 4.</summary>
    private uint LinkedAddress(ulong address)
    {
        var low = (uint)address;
        return (address & 0xFFFF_FFFF_C000_0000) == 0xFFFF_FFFF_8000_0000 ? (low & 0x1FFF_FFFF) >> 4 : low >> 4;
    }

    private void ExecuteSpecial(uint op, int rs, int rt)
    {
        ref var gpr = ref MemoryMarshal.GetArrayDataReference(_gpr);
        var rd = (int)(op >> 11) & 31;
        var sa = (int)(op >> 6) & 31;
        switch (op & 0x3F)
        {
            case 0x00: // SLL
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rt) << sa);
                break;
            case 0x02: // SRL
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rt) >> sa);
                break;
            case 0x03: // SRA: shifts the whole 64-bit register, then keeps the low word (n64-systemtest)
                Unsafe.Add(ref gpr, rd) = SignExtend32((ulong)((long)Unsafe.Add(ref gpr, rt) >> sa));
                break;
            case 0x04: // SLLV
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rt) << (int)(Unsafe.Add(ref gpr, rs) & 31));
                break;
            case 0x06: // SRLV
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rt) >> (int)(Unsafe.Add(ref gpr, rs) & 31));
                break;
            case 0x07: // SRAV
                Unsafe.Add(ref gpr, rd) = SignExtend32((ulong)((long)Unsafe.Add(ref gpr, rt) >> (int)(Unsafe.Add(ref gpr, rs) & 31)));
                break;
            case 0x08: // JR
                Branch(true, Unsafe.Add(ref gpr, rs));
                break;
            case 0x09: // JALR
            {
                var target = Unsafe.Add(ref gpr, rs);
                Unsafe.Add(ref gpr, rd) = _currentPc + 8;
                Branch(true, target);
                break;
            }

            case 0x0C: // SYSCALL
                RaiseException(R4300Exception.Syscall);
                break;
            case 0x0D: // BREAK
                RaiseException(R4300Exception.Breakpoint);
                break;
            case 0x0F: // SYNC
                break;
            case 0x10: // MFHI
                Unsafe.Add(ref gpr, rd) = Hi;
                break;
            case 0x11: // MTHI
                Hi = Unsafe.Add(ref gpr, rs);
                break;
            case 0x12: // MFLO
                Unsafe.Add(ref gpr, rd) = Lo;
                break;
            case 0x13: // MTLO
                Lo = Unsafe.Add(ref gpr, rs);
                break;
            case 0x14: // DSLLV
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) << (int)(Unsafe.Add(ref gpr, rs) & 63);
                break;
            case 0x16: // DSRLV
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) >> (int)(Unsafe.Add(ref gpr, rs) & 63);
                break;
            case 0x17: // DSRAV
                Unsafe.Add(ref gpr, rd) = (ulong)((long)Unsafe.Add(ref gpr, rt) >> (int)(Unsafe.Add(ref gpr, rs) & 63));
                break;
            case 0x18: // MULT
            {
                var product = (long)(int)Unsafe.Add(ref gpr, rs) * (int)Unsafe.Add(ref gpr, rt);
                Lo = SignExtend32((ulong)product);
                Hi = SignExtend32((ulong)(product >> 32));
                Cycles += MultCycles;
                break;
            }

            case 0x19: // MULTU
            {
                var product = (ulong)(uint)Unsafe.Add(ref gpr, rs) * (uint)Unsafe.Add(ref gpr, rt);
                Lo = SignExtend32(product);
                Hi = SignExtend32(product >> 32);
                Cycles += MultCycles;
                break;
            }

            case 0x1A: // DIV
            {
                var dividend = (int)Unsafe.Add(ref gpr, rs);
                var divisor = (int)Unsafe.Add(ref gpr, rt);
                if (divisor == 0)
                {
                    Lo = dividend >= 0 ? ulong.MaxValue : 1UL;
                    Hi = SignExtend32((uint)dividend);
                }
                else if (dividend == int.MinValue && divisor == -1)
                {
                    Lo = 0xFFFF_FFFF_8000_0000;
                    Hi = 0;
                }
                else
                {
                    Lo = SignExtend32((uint)(dividend / divisor));
                    Hi = SignExtend32((uint)(dividend % divisor));
                }

                Cycles += DivCycles;
                break;
            }

            case 0x1B: // DIVU
            {
                var dividend = (uint)Unsafe.Add(ref gpr, rs);
                var divisor = (uint)Unsafe.Add(ref gpr, rt);
                if (divisor == 0)
                {
                    Lo = ulong.MaxValue;
                    Hi = SignExtend32(dividend);
                }
                else
                {
                    Lo = SignExtend32(dividend / divisor);
                    Hi = SignExtend32(dividend % divisor);
                }

                Cycles += DivCycles;
                break;
            }

            case 0x1C: // DMULT
            {
                var high = Math.BigMul((long)Unsafe.Add(ref gpr, rs), (long)Unsafe.Add(ref gpr, rt), out var low);
                Lo = (ulong)low;
                Hi = (ulong)high;
                Cycles += DMultCycles;
                break;
            }

            case 0x1D: // DMULTU
            {
                Hi = Math.BigMul(Unsafe.Add(ref gpr, rs), Unsafe.Add(ref gpr, rt), out var low);
                Lo = low;
                Cycles += DMultCycles;
                break;
            }

            case 0x1E: // DDIV
            {
                var dividend = (long)Unsafe.Add(ref gpr, rs);
                var divisor = (long)Unsafe.Add(ref gpr, rt);
                if (divisor == 0)
                {
                    Lo = dividend >= 0 ? ulong.MaxValue : 1UL;
                    Hi = (ulong)dividend;
                }
                else if (dividend == long.MinValue && divisor == -1)
                {
                    Lo = (ulong)dividend;
                    Hi = 0;
                }
                else
                {
                    Lo = (ulong)(dividend / divisor);
                    Hi = (ulong)(dividend % divisor);
                }

                Cycles += DDivCycles;
                break;
            }

            case 0x1F: // DDIVU
            {
                var dividend = Unsafe.Add(ref gpr, rs);
                var divisor = Unsafe.Add(ref gpr, rt);
                if (divisor == 0)
                {
                    Lo = ulong.MaxValue;
                    Hi = dividend;
                }
                else
                {
                    Lo = dividend / divisor;
                    Hi = dividend % divisor;
                }

                Cycles += DDivCycles;
                break;
            }

            case 0x20: // ADD
            {
                var a = (int)Unsafe.Add(ref gpr, rs);
                var b = (int)Unsafe.Add(ref gpr, rt);
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rd) = (ulong)(long)sum;
                break;
            }

            case 0x21: // ADDU
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rs) + (uint)Unsafe.Add(ref gpr, rt));
                break;
            case 0x22: // SUB
            {
                var a = (int)Unsafe.Add(ref gpr, rs);
                var b = (int)Unsafe.Add(ref gpr, rt);
                var difference = a - b;
                if (((a ^ b) & (a ^ difference)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rd) = (ulong)(long)difference;
                break;
            }

            case 0x23: // SUBU
                Unsafe.Add(ref gpr, rd) = SignExtend32((uint)Unsafe.Add(ref gpr, rs) - (uint)Unsafe.Add(ref gpr, rt));
                break;
            case 0x24: // AND
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) & Unsafe.Add(ref gpr, rt);
                break;
            case 0x25: // OR
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) | Unsafe.Add(ref gpr, rt);
                break;
            case 0x26: // XOR
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) ^ Unsafe.Add(ref gpr, rt);
                break;
            case 0x27: // NOR
                Unsafe.Add(ref gpr, rd) = ~(Unsafe.Add(ref gpr, rs) | Unsafe.Add(ref gpr, rt));
                break;
            case 0x2A: // SLT
                Unsafe.Add(ref gpr, rd) = (long)Unsafe.Add(ref gpr, rs) < (long)Unsafe.Add(ref gpr, rt) ? 1UL : 0UL;
                break;
            case 0x2B: // SLTU
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) < Unsafe.Add(ref gpr, rt) ? 1UL : 0UL;
                break;
            case 0x2C: // DADD
            {
                var a = (long)Unsafe.Add(ref gpr, rs);
                var b = (long)Unsafe.Add(ref gpr, rt);
                var sum = a + b;
                if (((a ^ sum) & (b ^ sum)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rd) = (ulong)sum;
                break;
            }

            case 0x2D: // DADDU
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) + Unsafe.Add(ref gpr, rt);
                break;
            case 0x2E: // DSUB
            {
                var a = (long)Unsafe.Add(ref gpr, rs);
                var b = (long)Unsafe.Add(ref gpr, rt);
                var difference = a - b;
                if (((a ^ b) & (a ^ difference)) < 0)
                {
                    RaiseException(R4300Exception.Overflow);
                    return;
                }

                Unsafe.Add(ref gpr, rd) = (ulong)difference;
                break;
            }

            case 0x2F: // DSUBU
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rs) - Unsafe.Add(ref gpr, rt);
                break;
            case 0x30: // TGE
                Trap((long)Unsafe.Add(ref gpr, rs) >= (long)Unsafe.Add(ref gpr, rt));
                break;
            case 0x31: // TGEU
                Trap(Unsafe.Add(ref gpr, rs) >= Unsafe.Add(ref gpr, rt));
                break;
            case 0x32: // TLT
                Trap((long)Unsafe.Add(ref gpr, rs) < (long)Unsafe.Add(ref gpr, rt));
                break;
            case 0x33: // TLTU
                Trap(Unsafe.Add(ref gpr, rs) < Unsafe.Add(ref gpr, rt));
                break;
            case 0x34: // TEQ
                Trap(Unsafe.Add(ref gpr, rs) == Unsafe.Add(ref gpr, rt));
                break;
            case 0x36: // TNE
                Trap(Unsafe.Add(ref gpr, rs) != Unsafe.Add(ref gpr, rt));
                break;
            case 0x38: // DSLL
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) << sa;
                break;
            case 0x3A: // DSRL
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) >> sa;
                break;
            case 0x3B: // DSRA
                Unsafe.Add(ref gpr, rd) = (ulong)((long)Unsafe.Add(ref gpr, rt) >> sa);
                break;
            case 0x3C: // DSLL32
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) << (sa + 32);
                break;
            case 0x3E: // DSRL32
                Unsafe.Add(ref gpr, rd) = Unsafe.Add(ref gpr, rt) >> (sa + 32);
                break;
            case 0x3F: // DSRA32
                Unsafe.Add(ref gpr, rd) = (ulong)((long)Unsafe.Add(ref gpr, rt) >> (sa + 32));
                break;
            default:
                RaiseException(R4300Exception.ReservedInstruction);
                break;
        }
    }

    private void ExecuteRegimm(uint op, int rs, int rt)
    {
        var value = (long)Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rs);
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
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), 31) = _currentPc + 8;
                Branch(value < 0, BranchTarget(op));
                break;
            case 0x11: // BGEZAL
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), 31) = _currentPc + 8;
                Branch(value >= 0, BranchTarget(op));
                break;
            case 0x12: // BLTZALL
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), 31) = _currentPc + 8;
                BranchLikely(value < 0, BranchTarget(op));
                break;
            case 0x13: // BGEZALL
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), 31) = _currentPc + 8;
                BranchLikely(value >= 0, BranchTarget(op));
                break;
            default:
                RaiseException(R4300Exception.ReservedInstruction);
                break;
        }
    }

    private void ExecuteCop0(uint op, int rs, int rt)
    {
        if (!Cop0Usable)
        {
            RaiseException(R4300Exception.CoprocessorUnusable, 0);
            return;
        }

        var rd = (int)(op >> 11) & 31;
        switch (rs)
        {
            case 0x00: // MFC0
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt) = SignExtend32(ReadCop0(rd));
                break;
            case 0x01: // DMFC0
            {
                var value = ReadCop0(rd);
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt) = IsWideCop0(rd) || rd is 7 or >= 21 and <= 25 or 31 ? value : SignExtend32(value);
                break;
            }

            case 0x04: // MTC0
                WriteCop0(rd, SignExtend32(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt)));
                break;
            case 0x05: // DMTC0
                WriteCop0(rd, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt));
                break;
            case >= 0x10:
                switch (op & 0x3F)
                {
                    case 0x01: // TLBR
                        TlbRead();
                        break;
                    case 0x02: // TLBWI
                        TlbWrite((int)_index);
                        break;
                    case 0x06: // TLBWR
                        TlbWrite((int)Random);
                        break;
                    case 0x08: // TLBP
                        TlbProbe();
                        break;
                    case 0x18: // ERET: no delay slot
                        if ((_status & StatusErl) != 0)
                        {
                            _status &= ~StatusErl;
                            Jump(_errorEpc);
                        }
                        else
                        {
                            _status &= ~StatusExl;
                            Jump(_epc);
                        }

                        _llBit = false;
                        _tlbCacheVirtual = ulong.MaxValue;
                        UpdateInterruptPending();
                        break;
                    default:
                        RaiseException(R4300Exception.ReservedInstruction);
                        break;
                }

                break;
            default:
                RaiseException(R4300Exception.ReservedInstruction);
                break;
        }
    }

    private bool Cop1Usable()
    {
        if ((_status & StatusCu1) != 0)
        {
            return true;
        }

        RaiseException(R4300Exception.CoprocessorUnusable, 1);
        return false;
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
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt) = SignExtend32(Fpu.ReadWord(fs));
                break;
            case 0x01: // DMFC1
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt) = Fpu.ReadDoubleword(fs);
                break;
            case 0x02: // CFC1
                Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt) = SignExtend32(Fpu.ReadControl(fs));
                break;
            case 0x04: // MTC1
                Fpu.WriteWord(fs, (uint)Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt));
                break;
            case 0x05: // DMTC1
                Fpu.WriteDoubleword(fs, Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt));
                break;
            case 0x06: // CTC1
                if (!Fpu.WriteControl(fs, (uint)Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_gpr), rt)))
                {
                    RaiseException(R4300Exception.FloatingPoint);
                }

                break;
            case 0x08: // BC1F, BC1T, BC1FL, BC1TL
            {
                var condition = Fpu.Condition == ((rt & 1) != 0);
                if ((rt & 2) != 0)
                {
                    BranchLikely(condition, BranchTarget(op));
                }
                else
                {
                    Branch(condition, BranchTarget(op));
                }

                break;
            }

            case 0x10 or 0x11 or 0x14 or 0x15:
                if (!Fpu.Execute(op))
                {
                    RaiseException(R4300Exception.FloatingPoint);
                }

                break;
            default: // DCFC1, DCTC1 and the reserved formats
                Fpu.Unimplemented();
                RaiseException(R4300Exception.FloatingPoint);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong BranchTarget(uint op) => _pc + ((ulong)(long)(short)op << 2);

    /// <summary>A branch or jump: the next instruction is its delay slot; then <paramref name="target"/> if taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Branch(bool taken, ulong target)
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
    /// A branch to itself with a NOP in its delay slot (libultra's idle thread, homebrew's final loops) only waits for
    /// an interrupt, which can only arrive at the next event: the CPU skips to the end of its slice. The cycles still
    /// pass, so Count and everything timed by them are as if the loop had run.
    /// </summary>
    private void SkipIdleLoop()
    {
        if ((_pc & 0xFFFF_FFFF_C000_0003) != 0xFFFF_FFFF_8000_0000)
        {
            return; // only loops in unmapped RDRAM, so peeking at the delay slot can't fault
        }

        var physical = (uint)_pc & 0x1FFF_FFFF;
        if (physical < (uint)_rdram.Length && ReadRdram32(physical) == 0 && _sliceEnd - 2 > Cycles)
        {
            Cycles = _sliceEnd - 2;
        }
    }

    /// <summary>A branch-likely: the delay slot only runs when the branch is taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BranchLikely(bool taken, ulong target)
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
            RaiseException(R4300Exception.Trap);
        }
    }
}
