namespace GameVault.Core.Cpu.Sh2;

public sealed partial class Sh2Cpu
{
    // Vectors: general illegal instruction, slot illegal instruction.
    private const int IllegalVector = 4, SlotIllegalVector = 6;

    /// <summary>Runs <paramref name="op"/> at PC and moves PC on (or to a branch's target). Returns its clocks.</summary>
    private int Execute(ushort op, bool delaySlot)
    {
        var n = (op >> 8) & 0x0F;
        var m = (op >> 4) & 0x0F;
        var r = _r;
        switch (op >> 12)
        {
            case 0x0:
                return Group0(op, n, m, delaySlot);

            case 0x1: // MOV.L Rm,@(disp,Rn)
                Write32(r[n] + (uint)((op & 0x0F) << 2), r[m]);
                Pc += 2;
                return 1;

            case 0x2:
                return Group2(op, n, m);

            case 0x3:
                return Group3(op, n, m);

            case 0x4:
                return Group4(op, n, m, delaySlot);

            case 0x5: // MOV.L @(disp,Rm),Rn
                r[n] = Read32(r[m] + (uint)((op & 0x0F) << 2));
                Pc += 2;
                return 1;

            case 0x6:
                return Group6(op, n, m);

            case 0x7: // ADD #imm,Rn
                r[n] += (uint)(sbyte)op;
                Pc += 2;
                return 1;

            case 0x8:
                return Group8(op, n, m, delaySlot);

            case 0x9: // MOV.W @(disp,PC),Rn
                r[n] = (uint)(short)Read16(Pc + 4 + (uint)((op & 0xFF) << 1));
                Pc += 2;
                return 1;

            case 0xA: // BRA
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                return 2 + Delayed(Pc + 4 + (uint)(((op & 0xFFF) ^ 0x800) - 0x800) * 2);

            case 0xB: // BSR
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                Pr = Pc + 4;
                return 2 + Delayed(Pc + 4 + (uint)(((op & 0xFFF) ^ 0x800) - 0x800) * 2);

            case 0xC:
                return GroupC(op, delaySlot);

            case 0xD: // MOV.L @(disp,PC),Rn
                r[n] = Read32(((Pc + 4) & ~3u) + (uint)((op & 0xFF) << 2));
                Pc += 2;
                return 1;

            case 0xE: // MOV #imm,Rn
                r[n] = (uint)(sbyte)op;
                Pc += 2;
                return 1;

            default:
                return Illegal(delaySlot);
        }
    }

    private int Illegal(bool delaySlot)
    {
        if (delaySlot)
        {
            return SlotIllegal();
        }

        Exception(IllegalVector, Pc);
        return 8;
    }

    private int SlotIllegal()
    {
        Exception(SlotIllegalVector, Pc - 2);
        return 8;
    }

    private int Group0(ushort op, int n, int m, bool delaySlot)
    {
        var r = _r;
        switch (op & 0x0F)
        {
            case 0x2:
                switch (m)
                {
                    case 0: r[n] = _sr; break; // STC SR,Rn
                    case 1: r[n] = Gbr; break;
                    case 2: r[n] = Vbr; break;
                    default: return Illegal(delaySlot);
                }

                Pc += 2;
                return 2;

            case 0x3:
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                switch (m)
                {
                    case 0: // BSRF Rm
                        Pr = Pc + 4;
                        return 2 + Delayed(Pc + 4 + r[n]);
                    case 2: // BRAF Rm
                        return 2 + Delayed(Pc + 4 + r[n]);
                    default:
                        return Illegal(delaySlot);
                }

            case 0x4: Write8(r[0] + r[n], (byte)r[m]); break; // MOV.B Rm,@(R0,Rn)
            case 0x5: Write16(r[0] + r[n], (ushort)r[m]); break;
            case 0x6: Write32(r[0] + r[n], r[m]); break;
            case 0x7: // MUL.L Rm,Rn
                Macl = r[n] * r[m];
                Pc += 2;
                return 2;
            case 0x8:
                switch (m)
                {
                    case 0: T = false; break; // CLRT
                    case 1: T = true; break; // SETT
                    case 2: Mach = Macl = 0; break; // CLRMAC
                    default: return Illegal(delaySlot);
                }

                break;
            case 0x9:
                switch (m)
                {
                    case 0: break; // NOP
                    case 1: _sr &= ~(FlagM | FlagQ | FlagT); break; // DIV0U
                    case 2: r[n] = T ? 1u : 0u; break; // MOVT Rn
                    default: return Illegal(delaySlot);
                }

                break;
            case 0xA:
                switch (m)
                {
                    case 0: r[n] = Mach; break; // STS MACH,Rn
                    case 1: r[n] = Macl; break;
                    case 2: r[n] = Pr; break;
                    default: return Illegal(delaySlot);
                }

                break;
            case 0xB:
                switch (m)
                {
                    case 0: // RTS
                        if (delaySlot)
                        {
                            return SlotIllegal();
                        }

                        return 2 + Delayed(Pr);
                    case 1: // SLEEP
                        _sleeping = true;
                        Pc += 2;
                        return 3;
                    case 2: // RTE
                    {
                        if (delaySlot)
                        {
                            return SlotIllegal();
                        }

                        var target = Read32(r[15]);
                        r[15] += 4;
                        _sr = Read32(r[15]) & SrMask;
                        r[15] += 4;
                        return 4 + Delayed(target);
                    }

                    default:
                        return Illegal(delaySlot);
                }

            case 0xC: r[n] = (uint)(sbyte)Read8(r[0] + r[m]); break; // MOV.B @(R0,Rm),Rn
            case 0xD: r[n] = (uint)(short)Read16(r[0] + r[m]); break;
            case 0xE: r[n] = Read32(r[0] + r[m]); break;
            case 0xF: // MAC.L @Rm+,@Rn+
            {
                var a = (long)(int)Read32(r[n]);
                r[n] += 4;
                var b = (long)(int)Read32(r[m]);
                r[m] += 4;
                var mac = (long)(((ulong)Mach << 32) | Macl) + a * b;
                if (S)
                {
                    mac = Math.Clamp(mac, -0x0000_8000_0000_0000L, 0x0000_7FFF_FFFF_FFFFL); // 48 bits
                }

                Mach = (uint)(mac >> 32);
                Macl = (uint)mac;
                Pc += 2;
                return 3;
            }

            default:
                return Illegal(delaySlot);
        }

        Pc += 2;
        return 1;
    }

    private int Group2(ushort op, int n, int m)
    {
        var r = _r;
        switch (op & 0x0F)
        {
            case 0x0: Write8(r[n], (byte)r[m]); break; // MOV.B Rm,@Rn
            case 0x1: Write16(r[n], (ushort)r[m]); break;
            case 0x2: Write32(r[n], r[m]); break;
            case 0x4: Write8(r[n] - 1, (byte)r[m]); r[n] -= 1; break; // MOV.B Rm,@-Rn
            case 0x5: Write16(r[n] - 2, (ushort)r[m]); r[n] -= 2; break;
            case 0x6: Write32(r[n] - 4, r[m]); r[n] -= 4; break;
            case 0x7: // DIV0S Rm,Rn
                Q = (r[n] & 0x80000000) != 0;
                M = (r[m] & 0x80000000) != 0;
                T = Q != M;
                break;
            case 0x8: T = (r[n] & r[m]) == 0; break; // TST
            case 0x9: r[n] &= r[m]; break;
            case 0xA: r[n] ^= r[m]; break;
            case 0xB: r[n] |= r[m]; break;
            case 0xC: // CMP/STR
            {
                var x = r[n] ^ r[m];
                T = (x & 0xFF000000) == 0 || (x & 0x00FF0000) == 0 || (x & 0x0000FF00) == 0 || (x & 0x000000FF) == 0;
                break;
            }

            case 0xD: r[n] = (r[m] << 16) | (r[n] >> 16); break; // XTRCT
            case 0xE: Macl = (uint)((ushort)r[n] * (ushort)r[m]); Pc += 2; return 2; // MULU.W
            case 0xF: Macl = (uint)((short)r[n] * (short)r[m]); Pc += 2; return 2; // MULS.W
            default: return Illegal(false);
        }

        Pc += 2;
        return 1;
    }

    private int Group3(ushort op, int n, int m)
    {
        var r = _r;
        switch (op & 0x0F)
        {
            case 0x0: T = r[n] == r[m]; break; // CMP/EQ
            case 0x2: T = r[n] >= r[m]; break; // CMP/HS
            case 0x3: T = (int)r[n] >= (int)r[m]; break; // CMP/GE
            case 0x4: Div1(n, m); break;
            case 0x5: // DMULU.L
            {
                var product = (ulong)r[n] * r[m];
                Mach = (uint)(product >> 32);
                Macl = (uint)product;
                Pc += 2;
                return 2;
            }

            case 0x6: T = r[n] > r[m]; break; // CMP/HI
            case 0x7: T = (int)r[n] > (int)r[m]; break; // CMP/GT
            case 0x8: r[n] -= r[m]; break; // SUB
            case 0xA: // SUBC
            {
                var before = r[n];
                var difference = before - r[m];
                var result = difference - (T ? 1u : 0u);
                T = before < difference || difference < result;
                r[n] = result;
                break;
            }

            case 0xB: // SUBV
            {
                var result = r[n] - r[m];
                T = (((r[n] ^ r[m]) & (r[n] ^ result)) & 0x80000000) != 0;
                r[n] = result;
                break;
            }

            case 0xC: r[n] += r[m]; break; // ADD
            case 0xD: // DMULS.L
            {
                var product = (long)(int)r[n] * (int)r[m];
                Mach = (uint)(product >> 32);
                Macl = (uint)product;
                Pc += 2;
                return 2;
            }

            case 0xE: // ADDC
            {
                var before = r[n];
                var sum = before + r[m];
                var result = sum + (T ? 1u : 0u);
                T = before > sum || sum > result;
                r[n] = result;
                break;
            }

            case 0xF: // ADDV
            {
                var result = r[n] + r[m];
                T = ((~(r[n] ^ r[m]) & (r[n] ^ result)) & 0x80000000) != 0;
                r[n] = result;
                break;
            }

            default:
                return Illegal(false);
        }

        Pc += 2;
        return 1;
    }

    /// <summary>DIV1: one step of non-restoring division (the SH-2 Programming Manual's algorithm).</summary>
    private void Div1(int n, int m)
    {
        var r = _r;
        var oldQ = Q;
        var q = (r[n] & 0x80000000) != 0;
        var divisor = r[m];
        r[n] = (r[n] << 1) | (T ? 1u : 0u);
        var before = r[n];
        bool carry;
        if (oldQ == M)
        {
            r[n] -= divisor;
            carry = r[n] > before; // a borrow
        }
        else
        {
            r[n] += divisor;
            carry = r[n] < before;
        }

        // The new Q is the shifted-out bit combined with the borrow or carry, as the manual's case table gives it.
        Q = M ? (q ? carry : !carry) : (q ? !carry : carry);
        T = Q == M;
    }

    private int Group4(ushort op, int n, int m, bool delaySlot)
    {
        var r = _r;
        if ((op & 0x0F) == 0x0F)
        {
            // MAC.W @Rm+,@Rn+
            var a = (int)(short)Read16(r[n]);
            r[n] += 2;
            var b = (int)(short)Read16(r[m]);
            r[m] += 2;
            var product = (long)a * b;
            if (S)
            {
                var sum = (long)(int)Macl + product;
                if (sum is > int.MaxValue or < int.MinValue)
                {
                    Mach |= 1;
                    sum = Math.Clamp(sum, int.MinValue, int.MaxValue);
                }

                Macl = (uint)sum;
            }
            else
            {
                var mac = (long)(((ulong)Mach << 32) | Macl) + product;
                Mach = (uint)(mac >> 32);
                Macl = (uint)mac;
            }

            Pc += 2;
            return 3;
        }

        switch (op & 0xFF)
        {
            case 0x00: T = (r[n] & 0x80000000) != 0; r[n] <<= 1; break; // SHLL
            case 0x01: T = (r[n] & 1) != 0; r[n] >>= 1; break; // SHLR
            case 0x02: r[n] -= 4; Write32(r[n], Mach); break; // STS.L MACH,@-Rn
            case 0x03: r[n] -= 4; Write32(r[n], _sr); Pc += 2; return 2; // STC.L SR,@-Rn
            case 0x04: T = (r[n] & 0x80000000) != 0; r[n] = (r[n] << 1) | (r[n] >> 31); break; // ROTL
            case 0x05: T = (r[n] & 1) != 0; r[n] = (r[n] >> 1) | (r[n] << 31); break; // ROTR
            case 0x06: Mach = Read32(r[n]); r[n] += 4; break; // LDS.L @Rm+,MACH
            case 0x07: _sr = Read32(r[n]) & SrMask; r[n] += 4; Pc += 2; return 3; // LDC.L @Rm+,SR
            case 0x08: r[n] <<= 2; break; // SHLL2
            case 0x09: r[n] >>= 2; break;
            case 0x0A: Mach = r[n]; break; // LDS Rm,MACH
            case 0x0B: // JSR @Rm
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                Pr = Pc + 4;
                return 2 + Delayed(r[n]);
            case 0x0E: _sr = r[n] & SrMask; break; // LDC Rm,SR
            case 0x10: r[n]--; T = r[n] == 0; break; // DT
            case 0x11: T = (int)r[n] >= 0; break; // CMP/PZ
            case 0x12: r[n] -= 4; Write32(r[n], Macl); break;
            case 0x13: r[n] -= 4; Write32(r[n], Gbr); Pc += 2; return 2;
            case 0x15: T = (int)r[n] > 0; break; // CMP/PL
            case 0x16: Macl = Read32(r[n]); r[n] += 4; break;
            case 0x17: Gbr = Read32(r[n]); r[n] += 4; Pc += 2; return 3;
            case 0x18: r[n] <<= 8; break;
            case 0x19: r[n] >>= 8; break;
            case 0x1A: Macl = r[n]; break;
            case 0x1B: // TAS.B @Rn
            {
                var value = Read8(r[n]);
                T = value == 0;
                Write8(r[n], (byte)(value | 0x80));
                Pc += 2;
                return 4;
            }

            case 0x1E: Gbr = r[n]; break;
            case 0x20: T = (r[n] & 0x80000000) != 0; r[n] <<= 1; break; // SHAL
            case 0x21: T = (r[n] & 1) != 0; r[n] = (uint)((int)r[n] >> 1); break; // SHAR
            case 0x22: r[n] -= 4; Write32(r[n], Pr); break;
            case 0x23: r[n] -= 4; Write32(r[n], Vbr); Pc += 2; return 2;
            case 0x24: // ROTCL
            {
                var carry = (r[n] & 0x80000000) != 0;
                r[n] = (r[n] << 1) | (T ? 1u : 0u);
                T = carry;
                break;
            }

            case 0x25: // ROTCR
            {
                var carry = (r[n] & 1) != 0;
                r[n] = (r[n] >> 1) | (T ? 0x80000000u : 0u);
                T = carry;
                break;
            }

            case 0x26: Pr = Read32(r[n]); r[n] += 4; break;
            case 0x27: Vbr = Read32(r[n]); r[n] += 4; Pc += 2; return 3;
            case 0x28: r[n] <<= 16; break;
            case 0x29: r[n] >>= 16; break;
            case 0x2A: Pr = r[n]; break;
            case 0x2B: // JMP @Rm
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                return 2 + Delayed(r[n]);
            case 0x2E: Vbr = r[n]; break;
            default:
                return Illegal(delaySlot);
        }

        Pc += 2;
        return 1;
    }

    private int Group6(ushort op, int n, int m)
    {
        var r = _r;
        switch (op & 0x0F)
        {
            case 0x0: r[n] = (uint)(sbyte)Read8(r[m]); break; // MOV.B @Rm,Rn
            case 0x1: r[n] = (uint)(short)Read16(r[m]); break;
            case 0x2: r[n] = Read32(r[m]); break;
            case 0x3: r[n] = r[m]; break; // MOV Rm,Rn
            case 0x4: // MOV.B @Rm+,Rn
            {
                var value = (uint)(sbyte)Read8(r[m]);
                if (n != m)
                {
                    r[m] += 1;
                }

                r[n] = value;
                break;
            }

            case 0x5:
            {
                var value = (uint)(short)Read16(r[m]);
                if (n != m)
                {
                    r[m] += 2;
                }

                r[n] = value;
                break;
            }

            case 0x6:
            {
                var value = Read32(r[m]);
                if (n != m)
                {
                    r[m] += 4;
                }

                r[n] = value;
                break;
            }

            case 0x7: r[n] = ~r[m]; break; // NOT
            case 0x8: r[n] = (r[m] & 0xFFFF0000) | ((r[m] & 0xFF) << 8) | ((r[m] >> 8) & 0xFF); break; // SWAP.B
            case 0x9: r[n] = (r[m] << 16) | (r[m] >> 16); break; // SWAP.W
            case 0xA: // NEGC
            {
                var negated = 0 - r[m];
                var result = negated - (T ? 1u : 0u);
                T = negated != 0 || negated < result;
                r[n] = result;
                break;
            }

            case 0xB: r[n] = 0 - r[m]; break; // NEG
            case 0xC: r[n] = r[m] & 0xFF; break; // EXTU.B
            case 0xD: r[n] = r[m] & 0xFFFF; break;
            case 0xE: r[n] = (uint)(sbyte)r[m]; break; // EXTS.B
            case 0xF: r[n] = (uint)(short)r[m]; break;
        }

        Pc += 2;
        return 1;
    }

    private int Group8(ushort op, int n, int m, bool delaySlot)
    {
        var r = _r;
        var disp = (uint)(op & 0x0F);
        var branch = (uint)(sbyte)op;
        switch (n)
        {
            case 0x0: Write8(r[m] + disp, (byte)r[0]); break; // MOV.B R0,@(disp,Rn)
            case 0x1: Write16(r[m] + (disp << 1), (ushort)r[0]); break;
            case 0x4: r[0] = (uint)(sbyte)Read8(r[m] + disp); break; // MOV.B @(disp,Rm),R0
            case 0x5: r[0] = (uint)(short)Read16(r[m] + (disp << 1)); break;
            case 0x8: T = r[0] == (uint)(sbyte)op; break; // CMP/EQ #imm,R0
            case 0x9: // BT
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                if (T)
                {
                    Pc = Pc + 4 + (branch << 1);
                    return 3;
                }

                break;
            case 0xB: // BF
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                if (!T)
                {
                    Pc = Pc + 4 + (branch << 1);
                    return 3;
                }

                break;
            case 0xD: // BT/S
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                if (T)
                {
                    return 2 + Delayed(Pc + 4 + (branch << 1));
                }

                break;
            case 0xF: // BF/S
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                if (!T)
                {
                    return 2 + Delayed(Pc + 4 + (branch << 1));
                }

                break;
            default:
                return Illegal(delaySlot);
        }

        Pc += 2;
        return 1;
    }

    private int GroupC(ushort op, bool delaySlot)
    {
        var r = _r;
        var imm = (uint)(op & 0xFF);
        switch ((op >> 8) & 0x0F)
        {
            case 0x0: Write8(Gbr + imm, (byte)r[0]); break; // MOV.B R0,@(disp,GBR)
            case 0x1: Write16(Gbr + (imm << 1), (ushort)r[0]); break;
            case 0x2: Write32(Gbr + (imm << 2), r[0]); break;
            case 0x3: // TRAPA #imm
                if (delaySlot)
                {
                    return SlotIllegal();
                }

                Exception((int)imm, Pc + 2);
                return 8;
            case 0x4: r[0] = (uint)(sbyte)Read8(Gbr + imm); break;
            case 0x5: r[0] = (uint)(short)Read16(Gbr + (imm << 1)); break;
            case 0x6: r[0] = Read32(Gbr + (imm << 2)); break;
            case 0x7: r[0] = ((Pc + 4) & ~3u) + (imm << 2); break; // MOVA
            case 0x8: T = (r[0] & imm) == 0; break; // TST #imm,R0
            case 0x9: r[0] &= imm; break;
            case 0xA: r[0] ^= imm; break;
            case 0xB: r[0] |= imm; break;
            case 0xC: T = (Read8(Gbr + r[0]) & imm) == 0; Pc += 2; return 3; // TST.B #imm,@(R0,GBR)
            case 0xD: Write8(Gbr + r[0], (byte)(Read8(Gbr + r[0]) & imm)); Pc += 2; return 3;
            case 0xE: Write8(Gbr + r[0], (byte)(Read8(Gbr + r[0]) ^ imm)); Pc += 2; return 3;
            case 0xF: Write8(Gbr + r[0], (byte)(Read8(Gbr + r[0]) | imm)); Pc += 2; return 3;
        }

        Pc += 2;
        return 1;
    }
}
