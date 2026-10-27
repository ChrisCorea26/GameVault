using System.Numerics;

namespace GameVault.Core.Cpu.Gekko;

public sealed partial class GekkoCpu
{
    // Program exception reasons (SRR1).
    private const uint ProgramIllegal = 0x00080000, ProgramPrivileged = 0x00040000, ProgramTrap = 0x00020000;

    private static int Rd(uint op) => (int)((op >> 21) & 31);

    private static int Ra(uint op) => (int)((op >> 16) & 31);

    private static int Rb(uint op) => (int)((op >> 11) & 31);

    private static int Rc(uint op) => (int)((op >> 6) & 31);

    private static uint Simm(uint op) => (uint)(short)op;

    private static uint Uimm(uint op) => op & 0xFFFF;

    /// <summary>rA, or 0 when the field names r0 (the "(rA|0)" of loads, stores and addi).</summary>
    private uint RaOrZero(uint op)
    {
        var a = Ra(op);
        return a == 0 ? 0 : _gpr[a];
    }

    private void Execute(uint op)
    {
        var next = Pc + 4;
        switch (op >> 26)
        {
            case 3: // twi
                if (Trap((int)((op >> 21) & 31), (int)_gpr[Ra(op)], (int)Simm(op)))
                {
                    return;
                }

                break;
            case 4:
                if (!PairedSingle(op))
                {
                    return;
                }

                break;
            case 7: _gpr[Rd(op)] = (uint)((int)_gpr[Ra(op)] * (int)Simm(op)); break; // mulli
            case 8: // subfic
            {
                var a = _gpr[Ra(op)];
                var imm = Simm(op);
                _gpr[Rd(op)] = imm - a;
                Ca = imm >= a;
                break;
            }

            case 10: CompareLogical((int)((op >> 23) & 7), _gpr[Ra(op)], Uimm(op)); break; // cmpli
            case 11: Compare((int)((op >> 23) & 7), (int)_gpr[Ra(op)], (int)Simm(op)); break; // cmpi
            case 12: // addic
            case 13: // addic.
            {
                var a = _gpr[Ra(op)];
                var result = a + Simm(op);
                Ca = result < a;
                _gpr[Rd(op)] = result;
                if (op >> 26 == 13)
                {
                    SetCr0(result);
                }

                break;
            }

            case 14: _gpr[Rd(op)] = RaOrZero(op) + Simm(op); break; // addi
            case 15: _gpr[Rd(op)] = RaOrZero(op) + (op << 16); break; // addis
            case 16: // bc
            {
                if (BranchCondition(op))
                {
                    var target = (uint)(short)(op & 0xFFFC);
                    next = (op & 2) != 0 ? target : Pc + target;
                }

                if ((op & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                break;
            }

            case 17: // sc
                Exception(VectorSystemCall, Pc + 4);
                return;
            case 18: // b
            {
                var offset = (uint)(((int)(op << 6)) >> 6) & ~3u;
                next = (op & 2) != 0 ? offset : Pc + offset;
                if ((op & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                break;
            }

            case 19:
                if (!Group19(op, ref next))
                {
                    return;
                }

                break;
            case 20: // rlwimi
            {
                var mask = Mask((int)((op >> 6) & 31), (int)((op >> 1) & 31));
                var rotated = BitOperations.RotateLeft(_gpr[Rd(op)], Rb(op));
                var result = (rotated & mask) | (_gpr[Ra(op)] & ~mask);
                _gpr[Ra(op)] = result;
                RecordIf(op, result);
                break;
            }

            case 21: // rlwinm
            {
                var result = BitOperations.RotateLeft(_gpr[Rd(op)], Rb(op)) & Mask((int)((op >> 6) & 31), (int)((op >> 1) & 31));
                _gpr[Ra(op)] = result;
                RecordIf(op, result);
                break;
            }

            case 23: // rlwnm
            {
                var result = BitOperations.RotateLeft(_gpr[Rd(op)], (int)(_gpr[Rb(op)] & 31)) & Mask((int)((op >> 6) & 31), (int)((op >> 1) & 31));
                _gpr[Ra(op)] = result;
                RecordIf(op, result);
                break;
            }

            case 24: _gpr[Ra(op)] = _gpr[Rd(op)] | Uimm(op); break; // ori
            case 25: _gpr[Ra(op)] = _gpr[Rd(op)] | (op << 16); break; // oris
            case 26: _gpr[Ra(op)] = _gpr[Rd(op)] ^ Uimm(op); break; // xori
            case 27: _gpr[Ra(op)] = _gpr[Rd(op)] ^ (op << 16); break; // xoris
            case 28: SetCr0(_gpr[Ra(op)] = _gpr[Rd(op)] & Uimm(op)); break; // andi.
            case 29: SetCr0(_gpr[Ra(op)] = _gpr[Rd(op)] & (op << 16)); break; // andis.
            case 31:
                if (!Group31(op, ref next))
                {
                    return;
                }

                break;
            case 32: _gpr[Rd(op)] = Read32(RaOrZero(op) + Simm(op)); break; // lwz
            case 33: LoadUpdate(op, 4); break; // lwzu
            case 34: _gpr[Rd(op)] = Read8(RaOrZero(op) + Simm(op)); break; // lbz
            case 35: LoadUpdate(op, 1); break;
            case 36: Write32(RaOrZero(op) + Simm(op), _gpr[Rd(op)]); break; // stw
            case 37: StoreUpdate(op, 4); break;
            case 38: Write8(RaOrZero(op) + Simm(op), (byte)_gpr[Rd(op)]); break; // stb
            case 39: StoreUpdate(op, 1); break;
            case 40: _gpr[Rd(op)] = Read16(RaOrZero(op) + Simm(op)); break; // lhz
            case 41: LoadUpdate(op, 2); break;
            case 42: _gpr[Rd(op)] = (uint)(short)Read16(RaOrZero(op) + Simm(op)); break; // lha
            case 43: LoadUpdate(op, -2); break;
            case 44: Write16(RaOrZero(op) + Simm(op), (ushort)_gpr[Rd(op)]); break; // sth
            case 45: StoreUpdate(op, 2); break;
            case 46: // lmw
            {
                var address = RaOrZero(op) + Simm(op);
                for (var r = Rd(op); r < 32; r++, address += 4)
                {
                    _gpr[r] = Read32(address);
                }

                break;
            }

            case 47: // stmw
            {
                var address = RaOrZero(op) + Simm(op);
                for (var r = Rd(op); r < 32; r++, address += 4)
                {
                    Write32(address, _gpr[r]);
                }

                break;
            }

            case >= 48 and <= 55:
            case 56 or 57 or 60 or 61:
            case 59:
            case 63:
                if (!FloatingPoint(op))
                {
                    return;
                }

                break;
            default:
                Program(ProgramIllegal);
                return;
        }

        Pc = next;
    }

    // ---- Helpers ----

    /// <summary>The rotate-and-mask instructions' mask: ones from bit <paramref name="mb"/> to <paramref name="me"/> (IBM order).</summary>
    private static uint Mask(int mb, int me)
    {
        var begin = 0xFFFFFFFFu >> mb;
        var end = me == 31 ? 0xFFFFFFFFu : ~(0xFFFFFFFFu >> (me + 1));
        return mb <= me ? begin & end : begin | end;
    }

    private void RecordIf(uint op, uint result)
    {
        if ((op & 1) != 0)
        {
            SetCr0(result);
        }
    }

    private void SetCr0(uint result)
    {
        var field = (int)result < 0 ? 8u : (int)result > 0 ? 4u : 2u;
        if ((Xer & 0x80000000) != 0)
        {
            field |= 1;
        }

        SetCrField(0, field);
    }

    private void SetCrField(int field, uint value)
    {
        var shift = (7 - field) * 4;
        Cr = (Cr & ~(0xFu << shift)) | ((value & 0xF) << shift);
    }

    private uint CrField(int field) => (Cr >> ((7 - field) * 4)) & 0xF;

    private void Compare(int field, int a, int b) =>
        SetCrField(field, (a < b ? 8u : a > b ? 4u : 2u) | ((Xer & 0x80000000) != 0 ? 1u : 0u));

    private void CompareLogical(int field, uint a, uint b) =>
        SetCrField(field, (a < b ? 8u : a > b ? 4u : 2u) | ((Xer & 0x80000000) != 0 ? 1u : 0u));

    private bool CrBit(int bit) => (Cr & (0x80000000u >> bit)) != 0;

    private void SetCrBit(int bit, bool value) => Cr = value ? Cr | (0x80000000u >> bit) : Cr & ~(0x80000000u >> bit);

    /// <summary>BO and BI: decrement CTR (and test it) and/or test a CR bit.</summary>
    private bool BranchCondition(uint op)
    {
        var bo = (int)((op >> 21) & 31);
        var bi = (int)((op >> 16) & 31);
        var ctrOk = true;
        if ((bo & 4) == 0)
        {
            Ctr--;
            ctrOk = (Ctr != 0) ^ ((bo & 2) != 0);
        }

        var condOk = (bo & 16) != 0 || CrBit(bi) == ((bo & 8) != 0);
        return ctrOk && condOk;
    }

    /// <returns>True when the trap was taken (PC is at the program exception's vector).</returns>
    private bool Trap(int to, int a, int b)
    {
        var trap = ((to & 16) != 0 && a < b) || ((to & 8) != 0 && a > b) || ((to & 4) != 0 && a == b)
            || ((to & 2) != 0 && (uint)a < (uint)b) || ((to & 1) != 0 && (uint)a > (uint)b);
        if (trap)
        {
            Program(ProgramTrap);
        }

        return trap;
    }

    private void LoadUpdate(uint op, int size)
    {
        var address = _gpr[Ra(op)] + Simm(op);
        _gpr[Rd(op)] = size switch
        {
            4 => Read32(address),
            1 => Read8(address),
            2 => Read16(address),
            _ => (uint)(short)Read16(address),
        };
        _gpr[Ra(op)] = address;
    }

    private void StoreUpdate(uint op, int size)
    {
        var address = _gpr[Ra(op)] + Simm(op);
        switch (size)
        {
            case 4: Write32(address, _gpr[Rd(op)]); break;
            case 1: Write8(address, (byte)_gpr[Rd(op)]); break;
            default: Write16(address, (ushort)_gpr[Rd(op)]); break;
        }

        _gpr[Ra(op)] = address;
    }

    // ---- Opcode 19: branches to LR/CTR, CR logic, rfi, isync ----

    private bool Group19(uint op, ref uint next)
    {
        var xo = (op >> 1) & 0x3FF;
        int d = Rd(op), a = Ra(op), b = Rb(op);
        switch (xo)
        {
            case 0: SetCrField(d >> 2, CrField(a >> 2)); break; // mcrf
            case 16: // bclr
            {
                var target = Lr & ~3u;
                if (BranchCondition(op))
                {
                    next = target;
                }

                if ((op & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                break;
            }

            case 528: // bcctr (BO's CTR test doesn't apply)
            {
                var bi = a;
                var bo = d;
                if ((bo & 16) != 0 || CrBit(bi) == ((bo & 8) != 0))
                {
                    next = Ctr & ~3u;
                }

                if ((op & 1) != 0)
                {
                    Lr = Pc + 4;
                }

                break;
            }

            case 50: // rfi
                Msr = (Msr & ~0x87C0FF73u) | (Srr1 & 0x87C0FF73u);
                next = Srr0 & ~3u;
                break;
            case 150: break; // isync
            case 33: SetCrBit(d, !(CrBit(a) || CrBit(b))); break; // crnor
            case 129: SetCrBit(d, CrBit(a) && !CrBit(b)); break; // crandc
            case 193: SetCrBit(d, CrBit(a) != CrBit(b)); break; // crxor
            case 225: SetCrBit(d, !(CrBit(a) && CrBit(b))); break; // crnand
            case 257: SetCrBit(d, CrBit(a) && CrBit(b)); break; // crand
            case 289: SetCrBit(d, CrBit(a) == CrBit(b)); break; // creqv
            case 417: SetCrBit(d, CrBit(a) || !CrBit(b)); break; // crorc
            case 449: SetCrBit(d, CrBit(a) || CrBit(b)); break; // cror
            default:
                Program(ProgramIllegal);
                return false;
        }

        return true;
    }

    // ---- Opcode 31 ----

    private bool Group31(uint op, ref uint next)
    {
        int d = Rd(op), a = Ra(op), b = Rb(op);
        var xo = (op >> 1) & 0x3FF;
        var g = _gpr;
        if (ArithmeticXo(op, (op >> 1) & 0x1FF))
        {
            return true;
        }

        switch (xo)
        {
            case 0: Compare(d >> 2, (int)g[a], (int)g[b]); break; // cmp
            case 32: CompareLogical(d >> 2, g[a], g[b]); break; // cmpl
            case 4: // tw
                if (Trap(d, (int)g[a], (int)g[b]))
                {
                    return false;
                }

                break;
            case 19: g[d] = Cr; break; // mfcr
            case 144: // mtcrf
            {
                var crm = (op >> 12) & 0xFF;
                uint mask = 0;
                for (var i = 0; i < 8; i++)
                {
                    if ((crm & (0x80u >> i)) != 0)
                    {
                        mask |= 0xF0000000u >> (i * 4);
                    }
                }

                Cr = (Cr & ~mask) | (g[d] & mask);
                break;
            }

            case 512: // mcrxr
                SetCrField(d >> 2, Xer >> 28);
                Xer &= 0x0FFFFFFF;
                break;
            case 83: g[d] = Msr; break; // mfmsr
            case 146: // mtmsr
                if ((Msr & MsrPr) != 0)
                {
                    Program(ProgramPrivileged);
                    return false;
                }

                Msr = g[d];
                break;
            case 339: g[d] = ReadSpr((int)(((op >> 16) & 31) | ((op >> 6) & 0x3E0))); break; // mfspr
            case 467: WriteSpr((int)(((op >> 16) & 31) | ((op >> 6) & 0x3E0)), g[d]); break; // mtspr
            case 371: // mftb
            {
                var tbr = (int)(((op >> 16) & 31) | ((op >> 6) & 0x3E0));
                g[d] = tbr == 269 ? (uint)(TimeBase >> 32) : (uint)TimeBase;
                break;
            }

            case 595: g[d] = _sr[(op >> 16) & 15]; break; // mfsr
            case 659: g[d] = _sr[g[b] >> 28]; break; // mfsrin
            case 210: _sr[(op >> 16) & 15] = g[d]; break; // mtsr
            case 242: _sr[g[b] >> 28] = g[d]; break; // mtsrin

            case 24: { var s = g[b] & 63; g[a] = s >= 32 ? 0 : g[d] << (int)s; RecordIf(op, g[a]); break; } // slw
            case 536: { var s = g[b] & 63; g[a] = s >= 32 ? 0 : g[d] >> (int)s; RecordIf(op, g[a]); break; } // srw
            case 792: // sraw
            {
                var s = (int)(g[b] & 63);
                var value = (int)g[d];
                int result;
                if (s >= 32)
                {
                    result = value >> 31;
                    Ca = value < 0;
                }
                else
                {
                    result = value >> s;
                    Ca = value < 0 && s > 0 && (value & ((1 << s) - 1)) != 0;
                }

                g[a] = (uint)result;
                RecordIf(op, g[a]);
                break;
            }

            case 824: // srawi
            {
                var s = b;
                var value = (int)g[d];
                g[a] = (uint)(value >> s);
                Ca = value < 0 && s > 0 && (value & ((1 << s) - 1)) != 0;
                RecordIf(op, g[a]);
                break;
            }

            case 26: g[a] = (uint)BitOperations.LeadingZeroCount(g[d]); RecordIf(op, g[a]); break; // cntlzw
            case 28: g[a] = g[d] & g[b]; RecordIf(op, g[a]); break; // and
            case 60: g[a] = g[d] & ~g[b]; RecordIf(op, g[a]); break; // andc
            case 124: g[a] = ~(g[d] | g[b]); RecordIf(op, g[a]); break; // nor
            case 284: g[a] = ~(g[d] ^ g[b]); RecordIf(op, g[a]); break; // eqv
            case 316: g[a] = g[d] ^ g[b]; RecordIf(op, g[a]); break; // xor
            case 412: g[a] = g[d] | ~g[b]; RecordIf(op, g[a]); break; // orc
            case 444: g[a] = g[d] | g[b]; RecordIf(op, g[a]); break; // or
            case 476: g[a] = ~(g[d] & g[b]); RecordIf(op, g[a]); break; // nand
            case 922: g[a] = (uint)(short)g[d]; RecordIf(op, g[a]); break; // extsh
            case 954: g[a] = (uint)(sbyte)g[d]; RecordIf(op, g[a]); break; // extsb

            // Indexed loads and stores.
            case 23: g[d] = Read32(RaOrZero(op) + g[b]); break; // lwzx
            case 55: { var ea = g[a] + g[b]; g[d] = Read32(ea); g[a] = ea; break; } // lwzux
            case 87: g[d] = Read8(RaOrZero(op) + g[b]); break; // lbzx
            case 119: { var ea = g[a] + g[b]; g[d] = Read8(ea); g[a] = ea; break; }
            case 279: g[d] = Read16(RaOrZero(op) + g[b]); break; // lhzx
            case 311: { var ea = g[a] + g[b]; g[d] = Read16(ea); g[a] = ea; break; }
            case 343: g[d] = (uint)(short)Read16(RaOrZero(op) + g[b]); break; // lhax
            case 375: { var ea = g[a] + g[b]; g[d] = (uint)(short)Read16(ea); g[a] = ea; break; }
            case 534: g[d] = BinarySwap(Read32(RaOrZero(op) + g[b])); break; // lwbrx
            case 790: g[d] = (uint)(ushort)((Read16(RaOrZero(op) + g[b]) >> 8) | (Read16(RaOrZero(op) + g[b]) << 8)); break; // lhbrx
            case 20: g[d] = Read32(RaOrZero(op) + g[b]); break; // lwarx (stwcx. always succeeds: there is one CPU)
            case 150: // stwcx.
                Write32(RaOrZero(op) + g[b], g[d]);
                SetCrField(0, 2u | ((Xer & 0x80000000) != 0 ? 1u : 0u)); // the store always succeeds (one CPU)
                break;
            case 151: Write32(RaOrZero(op) + g[b], g[d]); break; // stwx
            case 183: { var ea = g[a] + g[b]; Write32(ea, g[d]); g[a] = ea; break; }
            case 215: Write8(RaOrZero(op) + g[b], (byte)g[d]); break; // stbx
            case 247: { var ea = g[a] + g[b]; Write8(ea, (byte)g[d]); g[a] = ea; break; }
            case 407: Write16(RaOrZero(op) + g[b], (ushort)g[d]); break; // sthx
            case 439: { var ea = g[a] + g[b]; Write16(ea, (ushort)g[d]); g[a] = ea; break; }
            case 662: Write32(RaOrZero(op) + g[b], BinarySwap(g[d])); break; // stwbrx
            case 918: Write16(RaOrZero(op) + g[b], (ushort)((g[d] >> 8 & 0xFF) | (g[d] << 8 & 0xFF00))); break; // sthbrx
            case 533: LoadString(d, RaOrZero(op) + g[b], (int)(Xer & 0x7F)); break; // lswx
            case 597: LoadString(d, RaOrZero(op), b == 0 ? 32 : b); break; // lswi
            case 661: StoreString(d, RaOrZero(op) + g[b], (int)(Xer & 0x7F)); break; // stswx
            case 725: StoreString(d, RaOrZero(op), b == 0 ? 32 : b); break; // stswi

            // Cache and synchronisation: nothing to do without caches, except dcbz zeroing its 32 bytes.
            case 1014: // dcbz
            {
                var ea = (RaOrZero(op) + g[b]) & ~31u;
                for (var i = 0u; i < 32; i += 4)
                {
                    Write32(ea + i, 0);
                }

                break;
            }

            case 54: case 86: case 246: case 278: case 470: case 982: // dcbst, dcbf, dcbtst, dcbt, dcbi, icbi
            case 598: case 854: case 306: case 370: case 566: // sync, eieio, tlbie, tlbia, tlbsync
                break;
            case 310: g[d] = 0; break; // eciwx
            case 438: break; // ecowx

            // Floating-point indexed loads and stores.
            case 535: case 567: case 599: case 631: case 663: case 695: case 727: case 759: case 983:
                if (!IndexedFloat(op, xo))
                {
                    return false;
                }

                break;
            default:
                Program(ProgramIllegal);
                return false;
        }

        return true;
    }

    private static uint BinarySwap(uint value) => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);

    private void LoadString(int register, uint address, int count)
    {
        var r = register;
        for (var i = 0; i < count; i++, address++)
        {
            if (i % 4 == 0)
            {
                if (i > 0)
                {
                    r = (r + 1) & 31;
                }

                _gpr[r] = 0;
            }

            _gpr[r] |= (uint)Read8(address) << (24 - (i % 4) * 8);
        }
    }

    private void StoreString(int register, uint address, int count)
    {
        var r = register;
        for (var i = 0; i < count; i++, address++)
        {
            if (i > 0 && i % 4 == 0)
            {
                r = (r + 1) & 31;
            }

            Write8(address, (byte)(_gpr[r] >> (24 - (i % 4) * 8)));
        }
    }

    /// <summary>The XO-form arithmetic (add, subf, neg, the carrying and extended forms, multiply, divide), with OE and Rc.</summary>
    private bool ArithmeticXo(uint op, uint xo)
    {
        int d = Rd(op), a = Ra(op), b = Rb(op);
        var g = _gpr;
        var oe = (op & 0x400) != 0;
        uint ra = g[a], rb = g[b], result;
        var overflow = false;
        switch (xo)
        {
            case 266: // add
                result = ra + rb;
                overflow = ((~(ra ^ rb) & (ra ^ result)) >> 31) != 0;
                break;
            case 10: // addc
                result = ra + rb;
                Ca = result < ra;
                overflow = ((~(ra ^ rb) & (ra ^ result)) >> 31) != 0;
                break;
            case 138: // adde
            {
                var sum = (ulong)ra + rb + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = ((~(ra ^ rb) & (ra ^ result)) >> 31) != 0;
                break;
            }

            case 202: // addze
            {
                var sum = (ulong)ra + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = ra == 0x7FFFFFFF && Ca == false && result == 0x80000000;
                break;
            }

            case 234: // addme
            {
                var sum = (ulong)ra + 0xFFFFFFFF + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = ra == 0x80000000 && result == 0x7FFFFFFF;
                break;
            }

            case 40: // subf: rB - rA
                result = rb - ra;
                overflow = (((ra ^ rb) & (rb ^ result)) >> 31) != 0;
                break;
            case 8: // subfc
                result = rb - ra;
                Ca = rb >= ra;
                overflow = (((ra ^ rb) & (rb ^ result)) >> 31) != 0;
                break;
            case 136: // subfe: ~rA + rB + CA
            {
                var sum = (ulong)~ra + rb + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = (((ra ^ rb) & (rb ^ result)) >> 31) != 0;
                break;
            }

            case 200: // subfze
            {
                var sum = (ulong)~ra + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = ~ra == 0x7FFFFFFF && result == 0x80000000;
                break;
            }

            case 232: // subfme
            {
                var sum = (ulong)~ra + 0xFFFFFFFF + (Ca ? 1u : 0u);
                result = (uint)sum;
                Ca = sum > uint.MaxValue;
                overflow = ~ra == 0x80000000 && result == 0x7FFFFFFF;
                break;
            }

            case 104: // neg
                result = 0 - ra;
                overflow = ra == 0x80000000;
                break;
            case 235: // mullw
            {
                var product = (long)(int)ra * (int)rb;
                result = (uint)product;
                overflow = product is > int.MaxValue or < int.MinValue;
                break;
            }

            case 75: // mulhw
                result = (uint)(((long)(int)ra * (int)rb) >> 32);
                oe = false;
                break;
            case 11: // mulhwu
                result = (uint)(((ulong)ra * rb) >> 32);
                oe = false;
                break;
            case 491: // divw
                if (rb == 0 || (ra == 0x80000000 && rb == 0xFFFFFFFF))
                {
                    result = (int)ra < 0 && rb == 0 ? 0xFFFFFFFF : 0; // the 750's undefined result
                    overflow = true;
                }
                else
                {
                    result = (uint)((int)ra / (int)rb);
                }

                break;
            case 459: // divwu
                if (rb == 0)
                {
                    result = 0;
                    overflow = true;
                }
                else
                {
                    result = ra / rb;
                }

                break;
            default:
                return false;
        }

        g[d] = result;
        if (oe)
        {
            Xer = overflow ? Xer | 0xC0000000 : Xer & ~0x40000000u; // OV, and SO stays once set
        }

        RecordIf(op, result);
        return true;
    }

    // ---- SPRs ----

    private uint ReadSpr(int spr) => spr switch
    {
        1 => Xer,
        8 => Lr,
        9 => Ctr,
        18 => Dsisr,
        19 => Dar,
        22 => Dec,
        26 => Srr0,
        27 => Srr1,
        >= 272 and <= 275 => _sprg[spr - 272],
        268 => (uint)TimeBase,
        269 => (uint)(TimeBase >> 32),
        287 => 0x00083214, // PVR: the Gekko
        >= 528 and <= 535 => (spr & 1) == 0 ? _ibatU[(spr - 528) >> 1] : _ibatL[(spr - 528) >> 1],
        >= 536 and <= 543 => (spr & 1) == 0 ? _dbatU[(spr - 536) >> 1] : _dbatL[(spr - 536) >> 1],
        >= 912 and <= 919 => _gqr[spr - 912],
        920 => Hid2,
        921 => Wpar,
        1008 => Hid0,
        1009 => Hid1,
        1017 => L2cr & 0xFFFFFFFE, // L2IP: never busy invalidating
        _ => 0,
    };

    private void WriteSpr(int spr, uint value)
    {
        switch (spr)
        {
            case 1: Xer = value; break;
            case 8: Lr = value; break;
            case 9: Ctr = value; break;
            case 18: Dsisr = value; break;
            case 19: Dar = value; break;
            case 22: Dec = value; break;
            case 26: Srr0 = value; break;
            case 27: Srr1 = value; break;
            case >= 272 and <= 275: _sprg[spr - 272] = value; break;
            case 284: TimeBase = (TimeBase & 0xFFFFFFFF00000000) | value; break;
            case 285: TimeBase = (TimeBase & 0xFFFFFFFF) | ((ulong)value << 32); break;
            case >= 528 and <= 535:
                if ((spr & 1) == 0)
                {
                    _ibatU[(spr - 528) >> 1] = value;
                }
                else
                {
                    _ibatL[(spr - 528) >> 1] = value;
                }

                break;
            case >= 536 and <= 543:
                if ((spr & 1) == 0)
                {
                    _dbatU[(spr - 536) >> 1] = value;
                }
                else
                {
                    _dbatL[(spr - 536) >> 1] = value;
                }

                break;
            case >= 912 and <= 919: _gqr[spr - 912] = value; break;
            case 920: Hid2 = value; break;
            case 921: Wpar = value; break;
            case 1008: Hid0 = value; break;
            case 1009: Hid1 = value; break;
            case 1017: L2cr = value; break;
        }
    }
}
