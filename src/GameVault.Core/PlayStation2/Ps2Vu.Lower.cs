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
        var efu = _isVu1;
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
            case 0x68: op = LowerOp.Xtop; break;
            case 0x69: op = LowerOp.Xitop; break;
            case 0x6C when efu: op = LowerOp.Xgkick; d.ViA = viS; break;
            case 0x70 when efu: op = LowerOp.Esadd; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x71 when efu: op = LowerOp.Ersadd; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x72 when efu: op = LowerOp.Eleng; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x73 when efu: op = LowerOp.Erleng; SetLowerReads(ref d, fs, 0xE, 0, 0); break;
            case 0x74 when efu: op = LowerOp.EatanXy; SetLowerReads(ref d, fs, 0xC, 0, 0); break;
            case 0x75 when efu: op = LowerOp.EatanXz; SetLowerReads(ref d, fs, 0xA, 0, 0); break;
            case 0x76 when efu: op = LowerOp.Esum; SetLowerReads(ref d, fs, 0xF, 0, 0); break;
            case 0x78 when efu: op = LowerOp.Esqrt; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x79 when efu: op = LowerOp.Ersqrt; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7A when efu: op = LowerOp.Ercpr; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7B when efu: op = LowerOp.Waitp; break;
            case 0x7C when efu: op = LowerOp.Esin; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7D when efu: op = LowerOp.Eatan; SetLowerReads(ref d, fs, fsf, 0, 0); break;
            case 0x7E when efu: op = LowerOp.Eexp; SetLowerReads(ref d, fs, fsf, 0, 0); break;
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
        int ft = d.It;
        int fs = d.Is;
        var it = ft & 15;
        var @is = fs & 15;
        int dest = d.LDest;
        var vi = _vi;
        switch (d.Lower)
        {
            case LowerOp.Nop:
                break;
            case LowerOp.Lq: // LQ.dest ft, imm(is)
                Load(ft, dest, vi[@is] + d.Imm);
                break;
            case LowerOp.Sq: // SQ.dest fs, imm(it)
                Store(fs, dest, vi[it] + d.Imm);
                break;
            case LowerOp.Ilw: // ILW.dest it, imm(is)
                LoadInteger(it, vi[@is] + d.Imm, dest);
                break;
            case LowerOp.Isw: // ISW.dest it, imm(is)
                StoreInteger(it, vi[@is] + d.Imm, dest);
                break;
            case LowerOp.Iaddiu:
                AluWrite(it, vi[@is] + d.Imm);
                break;
            case LowerOp.Isubiu:
                AluWrite(it, vi[@is] - d.Imm);
                break;
            case LowerOp.Fceq:
                SetVi(1, (ushort)(_clip == d.Imm ? 1 : 0));
                break;
            case LowerOp.Fcset:
                _clip = d.Imm;
                break;
            case LowerOp.Fcand:
                SetVi(1, (ushort)((_clip & d.Imm) != 0 ? 1 : 0));
                break;
            case LowerOp.Fcor:
                SetVi(1, (ushort)((_clip | d.Imm) == 0xFFFFFF ? 1 : 0));
                break;
            case LowerOp.Fseq:
                SetVi(it, (ushort)(_status == d.Imm ? 1 : 0));
                break;
            case LowerOp.Fsset: // the sticky bits only
                _status = (_status & 0x3F) | (d.Imm & 0xFC0);
                break;
            case LowerOp.Fsand:
                SetVi(it, (ushort)(_status & d.Imm));
                break;
            case LowerOp.Fsor:
                SetVi(it, (ushort)(_status | d.Imm));
                break;
            case LowerOp.Fmeq:
                SetVi(it, (ushort)(_mac == vi[@is] ? 1 : 0));
                break;
            case LowerOp.Fmand:
                SetVi(it, (ushort)(_mac & vi[@is]));
                break;
            case LowerOp.Fmor:
                SetVi(it, (ushort)(_mac | vi[@is]));
                break;
            case LowerOp.Fcget:
                SetVi(it, (ushort)(_clip & 0xFFF));
                break;
            case LowerOp.B:
                Branch(pc, d.Imm);
                break;
            case LowerOp.Bal:
                SetVi(it, (ushort)((pc + 16) >> 3));
                Branch(pc, d.Imm);
                break;
            case LowerOp.Jr:
                Jump((uint)vi[@is] << 3);
                break;
            case LowerOp.Jalr: // the target is read before the link is written
                var target = (uint)vi[@is] << 3;
                SetVi(it, (ushort)((pc + 16) >> 3));
                Jump(target);
                break;
            case LowerOp.Ibeq:
                if (BranchVi(it) == BranchVi(@is))
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Ibne:
                if (BranchVi(it) != BranchVi(@is))
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Ibltz:
                if ((short)BranchVi(@is) < 0)
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Ibgtz:
                if ((short)BranchVi(@is) > 0)
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Iblez:
                if ((short)BranchVi(@is) <= 0)
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Ibgez:
                if ((short)BranchVi(@is) >= 0)
                {
                    Branch(pc, d.Imm);
                }

                break;
            case LowerOp.Iadd:
                AluWrite(d.Id & 15, vi[@is] + vi[it]);
                break;
            case LowerOp.Isub:
                AluWrite(d.Id & 15, vi[@is] - vi[it]);
                break;
            case LowerOp.Iaddi:
                AluWrite(it, vi[@is] + d.Imm);
                break;
            case LowerOp.Iand:
                AluWrite(d.Id & 15, vi[@is] & vi[it]);
                break;
            case LowerOp.Ior:
                AluWrite(d.Id & 15, vi[@is] | vi[it]);
                break;
            case LowerOp.Move: // MOVE.dest ft, fs
                {
                    var b = fs << 2;
                    WriteLanes(ft, dest, _vf[b], _vf[b + 1], _vf[b + 2], _vf[b + 3]);
                }

                break;
            case LowerOp.Mr32: // MR32.dest ft, fs: each field from the next one round (x ← y, …, w ← x)
                {
                    var b = fs << 2;
                    WriteLanes(ft, dest, _vf[b + 1], _vf[b + 2], _vf[b + 3], _vf[b]);
                }

                break;
            case LowerOp.Lqi: // LQI.dest ft, (is++)
                Load(ft, dest, vi[@is]);
                AluWrite(@is, vi[@is] + 1);
                break;
            case LowerOp.Sqi: // SQI.dest fs, (it++)
                Store(fs, dest, vi[it]);
                AluWrite(it, vi[it] + 1);
                break;
            case LowerOp.Lqd: // LQD.dest ft, (--is)
                AluWrite(@is, vi[@is] - 1);
                Load(ft, dest, vi[@is]);
                break;
            case LowerOp.Sqd: // SQD.dest fs, (--it)
                AluWrite(it, vi[it] - 1);
                Store(fs, dest, vi[it]);
                break;
            case LowerOp.Div: // DIV Q, fs.fsf, ft.ftf
                {
                    var q = Ps2VuFloat.Div(_vf[(fs << 2) | d.Fsf], _vf[(ft << 2) | d.Ftf], out var flags);
                    StartFdiv(q, flags, DivLatency);
                }

                break;
            case LowerOp.Sqrt: // SQRT Q, ft.ftf
                {
                    var q = Ps2VuFloat.Sqrt(_vf[(ft << 2) | d.Ftf], out var flags);
                    StartFdiv(q, flags, DivLatency);
                }

                break;
            case LowerOp.Rsqrt: // RSQRT Q, fs.fsf, ft.ftf
                {
                    var q = Ps2VuFloat.Rsqrt(_vf[(fs << 2) | d.Fsf], _vf[(ft << 2) | d.Ftf], out var flags);
                    StartFdiv(q, flags, RsqrtLatency);
                }

                break;
            case LowerOp.Waitq: // the stall did the work
            case LowerOp.Waitp:
                break;
            case LowerOp.Mtir: // MTIR it, fs.fsf
                AluWrite(it, (int)_vf[(fs << 2) | d.Fsf]);
                break;
            case LowerOp.Mfir: // MFIR.dest ft, is: sign-extended
                {
                    var value = (uint)(short)vi[@is];
                    WriteLanes(ft, dest, value, value, value, value);
                }

                break;
            case LowerOp.Ilwr: // ILWR.dest it, (is)
                LoadInteger(it, vi[@is], dest);
                break;
            case LowerOp.Iswr: // ISWR.dest it, (is)
                StoreInteger(it, vi[@is], dest);
                break;
            case LowerOp.Rnext: // RNEXT.dest ft
                {
                    // A 23-bit LFSR with taps at bits 4 and 22 (ps2autotests random reproduces its sequence).
                    var feedback = ((_r >> 4) ^ (_r >> 22)) & 1;
                    _r = ((_r << 1) | feedback) & 0x7FFFFF;
                    WriteLanes(ft, dest, R, R, R, R);
                }

                break;
            case LowerOp.Rget:
                WriteLanes(ft, dest, R, R, R, R);
                break;
            case LowerOp.Rinit:
                _r = _vf[(fs << 2) | d.Fsf] & 0x7FFFFF;
                break;
            case LowerOp.Rxor:
                _r ^= _vf[(fs << 2) | d.Fsf] & 0x7FFFFF;
                break;
            case LowerOp.Mfp:
                WriteLanes(ft, dest, _p, _p, _p, _p);
                break;
            case LowerOp.Xtop:
                SetVi(it, (ushort)(_host.ReadTop(Index) & 0x3FF));
                _viReady[it] = 0;
                break;
            case LowerOp.Xitop:
                SetVi(it, (ushort)(_host.ReadItop(Index) & (_isVu1 ? 0x3FFu : 0xFFu)));
                _viReady[it] = 0;
                break;
            case LowerOp.Xgkick:
                _host.XgKick((uint)vi[@is] & 0x3FF);
                break;
            default:
                ExecuteEfu(d.Lower, fs, d.Fsf);
                break;
        }
    }

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

    private void WriteLanes(int ft, int dest, uint x, uint y, uint z, uint w)
    {
        if (ft == 0)
        {
            return;
        }

        WriteVfMasked(ft, dest, Vector128.Create(x, y, z, w));
    }

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
