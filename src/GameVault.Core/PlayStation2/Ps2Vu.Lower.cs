using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace GameVault.Core.PlayStation2;

// The lower instructions: loads and stores, the integer unit, branches, flags, FDIV, the random unit, the EFU, XGKICK.
public sealed partial class Ps2Vu
{
    // EFU latencies: when P is written, counted from issue (ps2tek "EFU Pipeline" lists them one higher, as the
    // stall ends a cycle before the write-back stage; these are Play!'s).
    private const int LatencyEatan = 53;
    private const int LatencyEexp = 43;
    private const int LatencyEleng = 17;
    private const int LatencyErcpr = 11;
    private const int LatencyErleng = 23;
    private const int LatencyErsadd = 17;
    private const int LatencyErsqrt = 17;
    private const int LatencyEsadd = 10;
    private const int LatencyEsin = 28;
    private const int LatencyEsqrt = 11;
    private const int LatencyEsum = 11;

    private const byte WaitsForFdiv = 1;
    private const byte WaitsForEfu = 2;

    private enum LowerOp : byte
    {
        Nop,
        Lq,
        Sq,
        Ilw,
        Isw,
        Iaddiu,
        Isubiu,
        Fceq,
        Fcset,
        Fcand,
        Fcor,
        Fseq,
        Fsset,
        Fsand,
        Fsor,
        Fmeq,
        Fmand,
        Fmor,
        Fcget,
        B,
        Bal,
        Jr,
        Jalr,
        Ibeq,
        Ibne,
        Ibltz,
        Ibgtz,
        Iblez,
        Ibgez,
        Iadd,
        Isub,
        Iaddi,
        Iand,
        Ior,
        Move,
        Mr32,
        Lqi,
        Sqi,
        Lqd,
        Sqd,
        Div,
        Sqrt,
        Rsqrt,
        Waitq,
        Mtir,
        Mfir,
        Ilwr,
        Iswr,
        Rnext,
        Rget,
        Rinit,
        Rxor,
        Mfp,
        Xtop,
        Xitop,
        Xgkick,
        Waitp,
        Esadd,
        Ersadd,
        Eleng,
        Erleng,
        EatanXy,
        EatanXz,
        Esum,
        Esqrt,
        Ersqrt,
        Ercpr,
        Esin,
        Eatan,
        Eexp,
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Imm11(uint instr) => ((int)instr << 21) >> 21;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Imm5(uint instr) => ((int)instr << 21) >> 27;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Imm12(uint instr) => (int)((instr & 0x7FF) | ((instr >> 10) & 0x800));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Imm15(uint instr) => (int)((instr & 0x7FF) | ((instr >> 10) & 0x7800));

    /// <summary>The Special2-style lower opcode (fhi × 4 + flo).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LowerSpecial(uint instr) => (int)((instr & 3) | ((instr >> 4) & 0x7C));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstLane(int dest) => (dest & 8) != 0 ? 0 : (dest & 4) != 0 ? 1 : (dest & 2) != 0 ? 2 : 3;

    /// <summary>
    /// Decodes a lower instruction with the operands it waits for: VF fields (FMAC hazards), VI registers an LSU load
    /// is still writing, and a busy FDIV or EFU.
    /// </summary>
    private void DecodeLower(ref DecodedPair d, uint instr)
    {
        var ft = (byte)((instr >> 16) & 0x1F);
        var fs = (byte)((instr >> 11) & 0x1F);
        var dest = (byte)((instr >> 21) & 0xF);
        d.LDest = dest;
        d.It = ft;
        d.Is = fs;
        d.Id = (byte)((instr >> 6) & 0x1F);
        d.Fsf = (byte)((instr >> 21) & 3);
        d.Ftf = (byte)((instr >> 23) & 3);
        d.Imm = Imm11(instr);
        d.LReadA = d.LMaskA = d.LReadB = d.LMaskB = d.ViA = d.ViB = d.Waits = 0;
        var op = LowerOp.Nop;
        var viS = (byte)(fs & 15);
        var viT = (byte)(ft & 15);
        if ((instr & 0x80000000) == 0)
        {
            switch (instr >> 25)
            {
                case 0x00: op = LowerOp.Lq; d.ViA = viS; break;
                case 0x01: op = LowerOp.Sq; SetLowerReads(ref d, fs, dest, 0, 0); d.ViA = viT; break;
                case 0x04: op = LowerOp.Ilw; d.ViA = viS; break;
                case 0x05: op = LowerOp.Isw; d.ViA = viS; d.ViB = viT; break;
                case 0x08: op = LowerOp.Iaddiu; d.Imm = Imm15(instr); d.ViA = viS; break;
                case 0x09: op = LowerOp.Isubiu; d.Imm = Imm15(instr); d.ViA = viS; break;
                case 0x10: op = LowerOp.Fceq; d.Imm = (int)(instr & 0xFFFFFF); break;
                case 0x11: op = LowerOp.Fcset; d.Imm = (int)(instr & 0xFFFFFF); break;
                case 0x12: op = LowerOp.Fcand; d.Imm = (int)(instr & 0xFFFFFF); break;
                case 0x13: op = LowerOp.Fcor; d.Imm = (int)(instr & 0xFFFFFF); break;
                case 0x14: op = LowerOp.Fseq; d.Imm = Imm12(instr); break;
                case 0x15: op = LowerOp.Fsset; d.Imm = Imm12(instr); break;
                case 0x16: op = LowerOp.Fsand; d.Imm = Imm12(instr); break;
                case 0x17: op = LowerOp.Fsor; d.Imm = Imm12(instr); break;
                case 0x18: op = LowerOp.Fmeq; d.ViA = viS; break;
                case 0x1A: op = LowerOp.Fmand; d.ViA = viS; break;
                case 0x1B: op = LowerOp.Fmor; d.ViA = viS; break;
                case 0x1C: op = LowerOp.Fcget; break;
                case 0x20: op = LowerOp.B; break;
                case 0x21: op = LowerOp.Bal; break;
                case 0x24: op = LowerOp.Jr; d.ViA = viS; break;
                case 0x25: op = LowerOp.Jalr; d.ViA = viS; break;
                case 0x28: op = LowerOp.Ibeq; d.ViA = viS; d.ViB = viT; break;
                case 0x29: op = LowerOp.Ibne; d.ViA = viS; d.ViB = viT; break;
                case 0x2C: op = LowerOp.Ibltz; d.ViA = viS; break;
                case 0x2D: op = LowerOp.Ibgtz; d.ViA = viS; break;
                case 0x2E: op = LowerOp.Iblez; d.ViA = viS; break;
                case 0x2F: op = LowerOp.Ibgez; d.ViA = viS; break;
            }

            d.Lower = op;
            return;
        }

        var function = (int)(instr & 0x3F);
        if (function < 0x3C)
        {
            switch (function)
            {
                case 0x30: op = LowerOp.Iadd; d.ViA = viS; d.ViB = viT; break;
                case 0x31: op = LowerOp.Isub; d.ViA = viS; d.ViB = viT; break;
                case 0x32: op = LowerOp.Iaddi; d.Imm = Imm5(instr); d.ViA = viS; break;
                case 0x34: op = LowerOp.Iand; d.ViA = viS; d.ViB = viT; break;
                case 0x35: op = LowerOp.Ior; d.ViA = viS; d.ViB = viT; break;
            }

            d.Lower = op;
            return;
        }

        var fsf = 8 >> d.Fsf;
        var ftf = 8 >> d.Ftf;
        var vu1 = _isVu1; // The EFU, XGKICK and XTOP are VU1's alone.
        switch (LowerSpecial(instr))
        {
            case 0x30: op = LowerOp.Move; SetLowerReads(ref d, fs, dest, 0, 0); break;
            case 0x31: op = LowerOp.Mr32; SetLowerReads(ref d, fs, ((dest >> 1) | (dest << 3)) & 0xF, 0, 0); break;
            case 0x34: op = LowerOp.Lqi; d.ViA = viS; break;
            case 0x35: op = LowerOp.Sqi; SetLowerReads(ref d, fs, dest, 0, 0); d.ViA = viT; break;
            case 0x36: op = LowerOp.Lqd; d.ViA = viS; break;
            case 0x37: op = LowerOp.Sqd; SetLowerReads(ref d, fs, dest, 0, 0); d.ViA = viT; break;
            case 0x38: op = LowerOp.Div; SetLowerReads(ref d, fs, fsf, ft, ftf); d.Waits = WaitsForFdiv; break;
            case 0x39: op = LowerOp.Sqrt; SetLowerReads(ref d, ft, ftf, 0, 0); d.Waits = WaitsForFdiv; break;
            case 0x3A: op = LowerOp.Rsqrt; SetLowerReads(ref d, fs, fsf, ft, ftf); d.Waits = WaitsForFdiv; break;
            case 0x3B: op = LowerOp.Waitq; d.Waits = WaitsForFdiv; break;
            case 0x3C: op = LowerOp.Mtir; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x3D: op = LowerOp.Mfir; d.ViA = viS; break;
            case 0x3E: op = LowerOp.Ilwr; d.ViA = viS; break;
            case 0x3F: op = LowerOp.Iswr; d.ViA = viS; d.ViB = viT; break;
            case 0x40: op = LowerOp.Rnext; break;
            case 0x41: op = LowerOp.Rget; break;
            case 0x42: op = LowerOp.Rinit; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x43: op = LowerOp.Rxor; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x64: op = LowerOp.Mfp; break;
            case 0x68 when vu1: op = LowerOp.Xtop; break; // VIF0 has no TOP
            case 0x69: op = LowerOp.Xitop; break;
            case 0x6C when vu1: op = LowerOp.Xgkick; d.ViA = viS; break;
            case 0x70 when vu1: op = LowerOp.Esadd; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x71 when vu1: op = LowerOp.Ersadd; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x72 when vu1: op = LowerOp.Eleng; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x73 when vu1: op = LowerOp.Erleng; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x74 when vu1: op = LowerOp.EatanXy; SetLowerReads(ref d, fs, 0xC, 0, 0); break;
            case 0x75 when vu1: op = LowerOp.EatanXz; SetLowerReads(ref d, fs, 0xA, 0, 0); break;
            case 0x76 when vu1: op = LowerOp.Esum; SetLowerReads(ref d, fs, 0xF, 0, 0); break;
            case 0x78 when vu1: op = LowerOp.Esqrt; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x79 when vu1: op = LowerOp.Ersqrt; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7A when vu1: op = LowerOp.Ercpr; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7B when vu1: op = LowerOp.Waitp; break;
            case 0x7C when vu1: op = LowerOp.Esin; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7D when vu1: op = LowerOp.Eatan; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7E when vu1: op = LowerOp.Eexp; SetLowerReads(ref d, fs, fsf, 0, 0); break;
        }

        if (op is >= LowerOp.Esadd and <= LowerOp.Eexp or LowerOp.Waitp)
        {
            d.Waits = WaitsForEfu;
        }

        d.Lower = op;
    }

    private static void SetLowerReads(ref DecodedPair d, byte a, int maskA, byte b, int maskB)
    {
        d.LReadA = a;
        d.LMaskA = a == 0 ? (byte)0 : (byte)maskA;
        d.LReadB = b;
        d.LMaskB = b == 0 ? (byte)0 : (byte)maskB;
    }

    /// <summary>Runs a lower instruction; <paramref name="pc"/> is its pair's byte address (for branches and links).</summary>
    private void ExecuteLower(ref DecodedPair d, uint pc)
    {
        switch (d.Lower)
        {
            case LowerOp.Nop:
                LowerNop(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Lq:
                LowerLq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Sq:
                LowerSq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ilw:
                LowerIlw(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Isw:
                LowerIsw(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iaddiu:
                LowerIaddiu(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Isubiu:
                LowerIsubiu(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fceq:
                LowerFceq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fcset:
                LowerFcset(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fcand:
                LowerFcand(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fcor:
                LowerFcor(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fseq:
                LowerFseq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fsset:
                LowerFsset(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fsand:
                LowerFsand(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fsor:
                LowerFsor(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fmeq:
                LowerFmeq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fmand:
                LowerFmand(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fmor:
                LowerFmor(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Fcget:
                LowerFcget(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.B:
                LowerB(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Bal:
                LowerBal(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Jr:
                LowerJr(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Jalr:
                LowerJalr(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ibeq:
                LowerIbeq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ibne:
                LowerIbne(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ibltz:
                LowerIbltz(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ibgtz:
                LowerIbgtz(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iblez:
                LowerIblez(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ibgez:
                LowerIbgez(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iadd:
                LowerIadd(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Isub:
                LowerIsub(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iaddi:
                LowerIaddi(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iand:
                LowerIand(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ior:
                LowerIor(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Move:
                LowerMove(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Mr32:
                LowerMr32(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Lqi:
                LowerLqi(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Sqi:
                LowerSqi(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Lqd:
                LowerLqd(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Sqd:
                LowerSqd(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Div:
                LowerDiv(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Sqrt:
                LowerSqrt(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Rsqrt:
                LowerRsqrt(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Waitq:
                LowerWaitq(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Waitp:
                LowerWaitp(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Mtir:
                LowerMtir(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Mfir:
                LowerMfir(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Ilwr:
                LowerIlwr(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Iswr:
                LowerIswr(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Rnext:
                LowerRnext(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Rget:
                LowerRget(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Rinit:
                LowerRinit(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Rxor:
                LowerRxor(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Mfp:
                LowerMfp(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Xtop:
                LowerXtop(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Xitop:
                LowerXitop(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            case LowerOp.Xgkick:
                LowerXgkick(d.It, d.Is, d.Id, d.LDest, d.Fsf, d.Ftf, d.Imm, pc);
                break;
            default:
                ExecuteEfu(d.Lower, d.Is, d.Fsf);
                break;
        }
    }

    // One method per lower instruction, all with the same parameters (ft and fs are the 5-bit fields; the VI forms use
    // their low 4 bits), so the recompiler can call each with constants.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerNop(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
    }

    // LQ.dest ft, imm(is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerLq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        Load(ft, dest, vi[@is] + imm);
    }

    // SQ.dest fs, imm(it)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerSq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var vi = _vi;
        Store(fs, dest, vi[it] + imm);
    }

    // ILW.dest it, imm(is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIlw(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        LoadInteger(it, vi[@is] + imm, dest);
    }

    // ISW.dest it, imm(is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIsw(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        StoreInteger(it, vi[@is] + imm, dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIaddiu(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(it, vi[@is] + imm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIsubiu(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(it, vi[@is] - imm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFceq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        SetVi(1, (ushort)(_clip == imm ? 1 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFcset(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        _clip = imm;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFcand(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        SetVi(1, (ushort)((_clip & imm) != 0 ? 1 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFcor(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        SetVi(1, (ushort)((_clip | imm) == 0xFFFFFF ? 1 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFseq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_status == imm ? 1 : 0));
    }

    // the sticky bits only
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFsset(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        _status = (_status & 0x3F) | (imm & 0xFC0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFsand(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_status & imm));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFsor(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_status | imm));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFmeq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        SetVi(it, (ushort)(_mac == vi[@is] ? 1 : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFmand(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        SetVi(it, (ushort)(_mac & vi[@is]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFmor(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        SetVi(it, (ushort)(_mac | vi[@is]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerFcget(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_clip & 0xFFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerB(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        Branch(pc, imm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerBal(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)((pc + 16) >> 3));
        Branch(pc, imm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerJr(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        Jump((uint)vi[@is] << 3);
    }

    // the target is read before the link is written
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerJalr(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        var target = (uint)vi[@is] << 3;
        SetVi(it, (ushort)((pc + 16) >> 3));
        Jump(target);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIbeq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        if (BranchVi(it) == BranchVi(@is))
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIbne(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        if (BranchVi(it) != BranchVi(@is))
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIbltz(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        if ((short)BranchVi(@is) < 0)
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIbgtz(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        if ((short)BranchVi(@is) > 0)
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIblez(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        if ((short)BranchVi(@is) <= 0)
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIbgez(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        if ((short)BranchVi(@is) >= 0)
        {
            Branch(pc, imm);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIadd(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(id & 15, vi[@is] + vi[it]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIsub(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(id & 15, vi[@is] - vi[it]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIaddi(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(it, vi[@is] + imm);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIand(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(id & 15, vi[@is] & vi[it]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIor(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(id & 15, vi[@is] | vi[it]);
    }

    // MOVE.dest ft, fs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerMove(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var b = fs << 2;
        WriteLanes(ft, dest, _vf[b], _vf[b + 1], _vf[b + 2], _vf[b + 3]);
    }

    // MR32.dest ft, fs: each field from the next one round (x ← y, …, w ← x)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerMr32(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var b = fs << 2;
        WriteLanes(ft, dest, _vf[b + 1], _vf[b + 2], _vf[b + 3], _vf[b]);
    }

    // LQI.dest ft, (is++)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerLqi(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        Load(ft, dest, vi[@is]);
        AluWrite(@is, vi[@is] + 1);
    }

    // SQI.dest fs, (it++)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerSqi(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var vi = _vi;
        Store(fs, dest, vi[it]);
        AluWrite(it, vi[it] + 1);
    }

    // LQD.dest ft, (--is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerLqd(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        AluWrite(@is, vi[@is] - 1);
        Load(ft, dest, vi[@is]);
    }

    // SQD.dest fs, (--it)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerSqd(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var vi = _vi;
        AluWrite(it, vi[it] - 1);
        Store(fs, dest, vi[it]);
    }

    // DIV Q, fs.fsf, ft.ftf
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerDiv(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var q = Ps2VuFloat.Div(_vf[(fs << 2) | fsf], _vf[(ft << 2) | ftf], out var flags);
        StartFdiv(q, flags, DivLatency);
    }

    // SQRT Q, ft.ftf
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerSqrt(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var q = Ps2VuFloat.Sqrt(_vf[(ft << 2) | ftf], out var flags);
        StartFdiv(q, flags, DivLatency);
    }

    // RSQRT Q, fs.fsf, ft.ftf
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerRsqrt(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var q = Ps2VuFloat.Rsqrt(_vf[(fs << 2) | fsf], _vf[(ft << 2) | ftf], out var flags);
        StartFdiv(q, flags, RsqrtLatency);
    }

    // the stall did the work
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerWaitq(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerWaitp(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
    }

    // MTIR it, fs.fsf
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerMtir(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        AluWrite(it, (int)_vf[(fs << 2) | fsf]);
    }

    // MFIR.dest ft, is: sign-extended
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerMfir(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        var value = (uint)(short)vi[@is];
        WriteLanes(ft, dest, value, value, value, value);
    }

    // ILWR.dest it, (is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIlwr(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        LoadInteger(it, vi[@is], dest);
    }

    // ISWR.dest it, (is)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerIswr(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        var @is = fs & 15;
        var vi = _vi;
        StoreInteger(it, vi[@is], dest);
    }

    // RNEXT.dest ft
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerRnext(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        // A 23-bit LFSR with taps at bits 4 and 22 (ps2autotests random reproduces its sequence).
        var feedback = ((_r >> 4) ^ (_r >> 22)) & 1;
        _r = ((_r << 1) | feedback) & 0x7FFFFF;
        WriteLanes(ft, dest, R, R, R, R);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerRget(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        WriteLanes(ft, dest, R, R, R, R);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerRinit(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        _r = _vf[(fs << 2) | fsf] & 0x7FFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerRxor(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        _r ^= _vf[(fs << 2) | fsf] & 0x7FFFFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerMfp(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        WriteLanes(ft, dest, _p, _p, _p, _p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerXtop(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_host.ReadTop(Index) & 0x3FF));
        _viReady[it] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerXitop(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var it = ft & 15;
        SetVi(it, (ushort)(_host.ReadItop(Index) & (_isVu1 ? 0x3FFu : 0xFFu)));
        _viReady[it] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LowerXgkick(int ft, int fs, int id, int dest, int fsf, int ftf, int imm, uint pc)
    {
        var @is = fs & 15;
        var vi = _vi;
        _host.XgKick((uint)vi[@is] & 0x3FF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Branch(uint pc, int offset)
    {
        _branchPending = true;
        _branchTarget = (uint)(pc + 8 + (offset << 3)) & _microMask;
    }

    private void Jump(uint target)
    {
        _branchPending = true;
        _branchTarget = target & _microMask;
    }

    /// <summary>
    /// A conditional branch's VI operand. When the previous pair's integer ALU instruction wrote it and this pair didn't
    /// stall, the branch still sees the value from before that write (the VU User's Manual's integer branch hazard,
    /// as Play! and PCSX2 model it).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort BranchVi(int reg) =>
        reg == _prevIntWriteReg && !_pairStalled ? _prevIntWriteOld : _vi[reg];

    /// <summary>An integer ALU result (1-cycle latency), remembered for the branch hazard.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AluWrite(int reg, int value)
    {
        if (reg == 0)
        {
            return;
        }

        _intWriteReg = reg;
        _intWriteOld = _vi[reg];
        _vi[reg] = (ushort)value;
        _viReady[reg] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Load(int ft, int dest, int qword)
    {
        if (ft == 0 || dest == 0)
        {
            return;
        }

        Vector128<uint> value;
        if (BitConverter.IsLittleEndian && (_isVu1 || (qword & 0x400) == 0))
        {
            value = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_data), (nuint)((qword & _dataQwordMask) << 4)).AsUInt32();
        }
        else
        {
            value = Vector128.Create(ReadData(qword, 0), ReadData(qword, 1), ReadData(qword, 2), ReadData(qword, 3));
        }

        WriteVfMasked(ft, dest, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Store(int fs, int dest, int qword)
    {
        if (BitConverter.IsLittleEndian && (_isVu1 || (qword & 0x400) == 0))
        {
            ref var data = ref MemoryMarshal.GetArrayDataReference(_data);
            var offset = (nuint)((qword & _dataQwordMask) << 4);
            var value = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_vf), (nuint)(fs << 2));
            var old = Vector128.LoadUnsafe(ref data, offset).AsUInt32();
            Vector128.ConditionalSelect(s_laneMasks[dest], value, old).AsByte().StoreUnsafe(ref data, offset);
            return;
        }

        for (var lane = 0; lane < 4; lane++)
        {
            if ((dest & (8 >> lane)) != 0)
            {
                WriteData(qword, lane, _vf[(fs << 2) | lane]);
            }
        }
    }

    /// <summary>ILW/ILWR: the low 16 bits of one field; the value is due four cycles later (an LSU load).</summary>
    private void LoadInteger(int it, int qword, int dest)
    {
        if (it == 0)
        {
            return;
        }

        _vi[it] = (ushort)ReadData(qword, FirstLane(dest));
        _viReady[it] = _inMacro ? 0 : _cycle + FmacLatency;
    }

    /// <summary>ISW/ISWR: the VI register, zero-extended, into every selected field.</summary>
    private void StoreInteger(int it, int qword, int dest)
    {
        for (var lane = 0; lane < 4; lane++)
        {
            if ((dest & (8 >> lane)) != 0)
            {
                WriteData(qword, lane, _vi[it]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteLanes(int ft, int dest, uint x, uint y, uint z, uint w)
    {
        if (ft == 0)
        {
            return;
        }

        WriteVfMasked(ft, dest, Vector128.Create(x, y, z, w));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartFdiv(uint value, int flags, int latency)
    {
        _qNext = value;
        _qNextFlags = flags;
        _qReady = _cycle + latency;
        _qPending = true;
    }

    private void StartEfu(uint value, int latency)
    {
        _pNext = value;
        _pReady = _cycle + latency;
        _pPending = true;
    }

    /// <summary>VU1's elementary functions into P (ps2tek: "EFU Pipeline"; the series are Play!'s, BSD-2-Clause).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecuteEfu(LowerOp op, int fs, int fsfLane)
    {
        var baseIndex = fs << 2;
        var x = _vf[baseIndex];
        var y = _vf[baseIndex + 1];
        var z = _vf[baseIndex + 2];
        var w = _vf[baseIndex + 3];
        var f = _vf[baseIndex + fsfLane];
        switch (op)
        {
            case LowerOp.Esadd:
                StartEfu(Ps2VuEfu.SquareSum(x, y, z), LatencyEsadd);
                break;
            case LowerOp.Ersadd:
                StartEfu(Ps2VuEfu.Reciprocal(Ps2VuEfu.SquareSum(x, y, z)), LatencyErsadd);
                break;
            case LowerOp.Eleng:
                StartEfu(Ps2VuEfu.Sqrt(Ps2VuEfu.SquareSum(x, y, z)), LatencyEleng);
                break;
            case LowerOp.Erleng:
                StartEfu(Ps2VuEfu.ReciprocalSqrt(Ps2VuEfu.SquareSum(x, y, z)), LatencyErleng);
                break;
            case LowerOp.EatanXy:
                StartEfu(Ps2VuEfu.ArcTanRatio(y, x), LatencyEatan);
                break;
            case LowerOp.EatanXz:
                StartEfu(Ps2VuEfu.ArcTanRatio(z, x), LatencyEatan);
                break;
            case LowerOp.Esum:
                StartEfu(Ps2VuEfu.Sum(x, y, z, w), LatencyEsum);
                break;
            case LowerOp.Esqrt:
                StartEfu(Ps2VuEfu.Sqrt(f), LatencyEsqrt);
                break;
            case LowerOp.Ersqrt:
                StartEfu(Ps2VuEfu.ReciprocalSqrt(f), LatencyErsqrt);
                break;
            case LowerOp.Ercpr:
                StartEfu(Ps2VuEfu.Reciprocal(f), LatencyErcpr);
                break;
            case LowerOp.Esin:
                StartEfu(Ps2VuEfu.Sin(f), LatencyEsin);
                break;
            case LowerOp.Eatan:
                StartEfu(Ps2VuEfu.ArcTanRatio(f, 0x3F800000), LatencyEatan);
                break;
            case LowerOp.Eexp:
                StartEfu(Ps2VuEfu.Exp(f), LatencyEexp);
                break;
        }
    }
}
