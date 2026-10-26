using System.Runtime.CompilerServices;

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

    /// <summary>The cycle when the lower instruction can issue: its VF and LSU-loaded VI operands, and FDIV/EFU being free.</summary>
    private long LowerReady(uint instr)
    {
        var ft = (int)(instr >> 16) & 0x1F;
        var fs = (int)(instr >> 11) & 0x1F;
        var dest = (int)(instr >> 21) & 0xF;
        if ((instr & 0x80000000) == 0)
        {
            switch (instr >> 25)
            {
                case 0x00: // LQ
                case 0x04: // ILW
                case 0x08: // IADDIU
                case 0x09: // ISUBIU
                case 0x18: // FMEQ
                case 0x1A: // FMAND
                case 0x1B: // FMOR
                case 0x24: // JR
                case 0x25: // JALR
                case 0x2C: // IBLTZ
                case 0x2D: // IBGTZ
                case 0x2E: // IBLEZ
                case 0x2F: // IBGEZ
                    return ViReady(fs);
                case 0x01: // SQ
                    return Math.Max(VfReady(fs, dest), ViReady(ft));
                case 0x05: // ISW
                case 0x28: // IBEQ
                case 0x29: // IBNE
                    return Math.Max(ViReady(fs), ViReady(ft));
                default:
                    return 0;
            }
        }

        var function = (int)(instr & 0x3F);
        if (function < 0x3C)
        {
            return function switch
            {
                0x30 or 0x31 or 0x34 or 0x35 => Math.Max(ViReady(fs), ViReady(ft)),
                0x32 => ViReady(fs),
                _ => 0,
            };
        }

        var fsf = 8 >> (int)((instr >> 21) & 3);
        var ftf = 8 >> (int)((instr >> 23) & 3);
        var qBusy = _qPending ? _qReady : 0;
        var pBusy = _pPending ? _pReady : 0;
        switch (LowerSpecial(instr))
        {
            case 0x30: // MOVE
                return VfReady(fs, dest);
            case 0x31: // MR32 reads the next field round
                return VfReady(fs, ((dest >> 1) | (dest << 3)) & 0xF);
            case 0x34: // LQI
            case 0x36: // LQD
            case 0x3D: // MFIR
            case 0x3E: // ILWR
            case 0x6C: // XGKICK
                return ViReady(fs);
            case 0x35: // SQI
            case 0x37: // SQD
                return Math.Max(VfReady(fs, dest), ViReady(ft));
            case 0x3F: // ISWR
                return Math.Max(ViReady(fs), ViReady(ft));
            case 0x38: // DIV
            case 0x3A: // RSQRT
                return Math.Max(Math.Max(VfReady(fs, fsf), VfReady(ft, ftf)), qBusy);
            case 0x39: // SQRT
                return Math.Max(VfReady(ft, ftf), qBusy);
            case 0x3B: // WAITQ
                return qBusy;
            case 0x3C: // MTIR
            case 0x42: // RINIT
            case 0x43: // RXOR
                return VfReady(fs, fsf);
            case 0x70: // ESADD
            case 0x71: // ERSADD
            case 0x72: // ELENG
            case 0x73: // ERLENG
                return _isVu1 ? Math.Max(VfReady(fs, 0xE), pBusy) : 0;
            case 0x74: // EATANxy
                return _isVu1 ? Math.Max(VfReady(fs, 0xC), pBusy) : 0;
            case 0x75: // EATANxz
                return _isVu1 ? Math.Max(VfReady(fs, 0xA), pBusy) : 0;
            case 0x76: // ESUM
                return _isVu1 ? Math.Max(VfReady(fs, 0xF), pBusy) : 0;
            case 0x78: // ESQRT
            case 0x79: // ERSQRT
            case 0x7A: // ERCPR
            case 0x7C: // ESIN
            case 0x7D: // EATAN
            case 0x7E: // EEXP
                return _isVu1 ? Math.Max(VfReady(fs, fsf), pBusy) : 0;
            case 0x7B: // WAITP
                return _isVu1 ? pBusy : 0;
            default:
                return 0;
        }
    }

    /// <summary>Runs a lower instruction; <paramref name="pc"/> is its pair's byte address (for branches and links).</summary>
    private void ExecuteLower(uint instr, uint pc)
    {
        var ft = (int)(instr >> 16) & 0x1F;
        var fs = (int)(instr >> 11) & 0x1F;
        var it = ft & 15;
        var @is = fs & 15;
        var dest = (int)(instr >> 21) & 0xF;
        if ((instr & 0x80000000) == 0)
        {
            switch (instr >> 25)
            {
                case 0x00: // LQ.dest ft, imm(is)
                    Load(ft, dest, _vi[@is] + Imm11(instr));
                    break;
                case 0x01: // SQ.dest fs, imm(it)
                    Store(fs, dest, _vi[it] + Imm11(instr));
                    break;
                case 0x04: // ILW.dest it, imm(is)
                    LoadInteger(it, _vi[@is] + Imm11(instr), dest);
                    break;
                case 0x05: // ISW.dest it, imm(is)
                    StoreInteger(it, _vi[@is] + Imm11(instr), dest);
                    break;
                case 0x08: // IADDIU
                    AluWrite(it, _vi[@is] + Imm15(instr));
                    break;
                case 0x09: // ISUBIU
                    AluWrite(it, _vi[@is] - Imm15(instr));
                    break;
                case 0x10: // FCEQ
                    SetVi(1, (ushort)(_clip == (int)(instr & 0xFFFFFF) ? 1 : 0));
                    break;
                case 0x11: // FCSET
                    _clip = (int)(instr & 0xFFFFFF);
                    break;
                case 0x12: // FCAND
                    SetVi(1, (ushort)((_clip & (int)(instr & 0xFFFFFF)) != 0 ? 1 : 0));
                    break;
                case 0x13: // FCOR
                    SetVi(1, (ushort)(((_clip | (int)instr) & 0xFFFFFF) == 0xFFFFFF ? 1 : 0));
                    break;
                case 0x14: // FSEQ
                    SetVi(it, (ushort)(_status == Imm12(instr) ? 1 : 0));
                    break;
                case 0x15: // FSSET: the sticky bits only
                    _status = (_status & 0x3F) | (Imm12(instr) & 0xFC0);
                    break;
                case 0x16: // FSAND
                    SetVi(it, (ushort)(_status & Imm12(instr)));
                    break;
                case 0x17: // FSOR
                    SetVi(it, (ushort)(_status | Imm12(instr)));
                    break;
                case 0x18: // FMEQ
                    SetVi(it, (ushort)(_mac == _vi[@is] ? 1 : 0));
                    break;
                case 0x1A: // FMAND
                    SetVi(it, (ushort)(_mac & _vi[@is]));
                    break;
                case 0x1B: // FMOR
                    SetVi(it, (ushort)(_mac | _vi[@is]));
                    break;
                case 0x1C: // FCGET
                    SetVi(it, (ushort)(_clip & 0xFFF));
                    break;
                case 0x20: // B
                    Branch(pc, instr);
                    break;
                case 0x21: // BAL
                    SetVi(it, (ushort)((pc + 16) >> 3));
                    Branch(pc, instr);
                    break;
                case 0x24: // JR
                    Jump((uint)_vi[@is] << 3);
                    break;
                case 0x25: // JALR: the target is read before the link is written
                    var target = (uint)_vi[@is] << 3;
                    SetVi(it, (ushort)((pc + 16) >> 3));
                    Jump(target);
                    break;
                case 0x28: // IBEQ
                    if (BranchVi(it) == BranchVi(@is))
                    {
                        Branch(pc, instr);
                    }

                    break;
                case 0x29: // IBNE
                    if (BranchVi(it) != BranchVi(@is))
                    {
                        Branch(pc, instr);
                    }

                    break;
                case 0x2C: // IBLTZ
                    if ((short)BranchVi(@is) < 0)
                    {
                        Branch(pc, instr);
                    }

                    break;
                case 0x2D: // IBGTZ
                    if ((short)BranchVi(@is) > 0)
                    {
                        Branch(pc, instr);
                    }

                    break;
                case 0x2E: // IBLEZ
                    if ((short)BranchVi(@is) <= 0)
                    {
                        Branch(pc, instr);
                    }

                    break;
                case 0x2F: // IBGEZ
                    if ((short)BranchVi(@is) >= 0)
                    {
                        Branch(pc, instr);
                    }

                    break;
            }

            return;
        }

        var function = (int)(instr & 0x3F);
        if (function < 0x3C)
        {
            var id = (int)(instr >> 6) & 15;
            switch (function)
            {
                case 0x30: // IADD
                    AluWrite(id, _vi[@is] + _vi[it]);
                    break;
                case 0x31: // ISUB
                    AluWrite(id, _vi[@is] - _vi[it]);
                    break;
                case 0x32: // IADDI
                    AluWrite(it, _vi[@is] + Imm5(instr));
                    break;
                case 0x34: // IAND
                    AluWrite(id, _vi[@is] & _vi[it]);
                    break;
                case 0x35: // IOR
                    AluWrite(id, _vi[@is] | _vi[it]);
                    break;
            }

            return;
        }

        var fsfLane = (int)((instr >> 21) & 3);
        var ftfLane = (int)((instr >> 23) & 3);
        switch (LowerSpecial(instr))
        {
            case 0x30: // MOVE.dest ft, fs
                for (var lane = 0; lane < 4; lane++)
                {
                    if ((dest & (8 >> lane)) != 0)
                    {
                        SetVf(ft, lane, _vf[(fs << 2) | lane]);
                    }
                }

                MarkVfWrite(ft, dest);
                break;
            case 0x31: // MR32.dest ft, fs: each field from the next one round (x ← y, …, w ← x)
                {
                    var x = _vf[fs << 2];
                    var y = _vf[(fs << 2) | 1];
                    var z = _vf[(fs << 2) | 2];
                    var w = _vf[(fs << 2) | 3];
                    WriteLanes(ft, dest, y, z, w, x);
                }

                break;
            case 0x34: // LQI.dest ft, (is++)
                Load(ft, dest, _vi[@is]);
                AluWrite(@is, _vi[@is] + 1);
                break;
            case 0x35: // SQI.dest fs, (it++)
                Store(fs, dest, _vi[it]);
                AluWrite(it, _vi[it] + 1);
                break;
            case 0x36: // LQD.dest ft, (--is)
                AluWrite(@is, _vi[@is] - 1);
                Load(ft, dest, _vi[@is]);
                break;
            case 0x37: // SQD.dest fs, (--it)
                AluWrite(it, _vi[it] - 1);
                Store(fs, dest, _vi[it]);
                break;
            case 0x38: // DIV Q, fs.fsf, ft.ftf
                {
                    var q = Ps2VuFloat.Div(_vf[(fs << 2) | fsfLane], _vf[(ft << 2) | ftfLane], out var flags);
                    StartFdiv(q, flags, DivLatency);
                }

                break;
            case 0x39: // SQRT Q, ft.ftf
                {
                    var q = Ps2VuFloat.Sqrt(_vf[(ft << 2) | ftfLane], out var flags);
                    StartFdiv(q, flags, DivLatency);
                }

                break;
            case 0x3A: // RSQRT Q, fs.fsf, ft.ftf
                {
                    var q = Ps2VuFloat.Rsqrt(_vf[(fs << 2) | fsfLane], _vf[(ft << 2) | ftfLane], out var flags);
                    StartFdiv(q, flags, RsqrtLatency);
                }

                break;
            case 0x3B: // WAITQ: the stall did the work
                break;
            case 0x3C: // MTIR it, fs.fsf
                AluWrite(it, (int)_vf[(fs << 2) | fsfLane]);
                break;
            case 0x3D: // MFIR.dest ft, is: sign-extended
                {
                    var value = (uint)(short)_vi[@is];
                    WriteLanes(ft, dest, value, value, value, value);
                }

                break;
            case 0x3E: // ILWR.dest it, (is)
                LoadInteger(it, _vi[@is], dest);
                break;
            case 0x3F: // ISWR.dest it, (is)
                StoreInteger(it, _vi[@is], dest);
                break;
            case 0x40: // RNEXT.dest ft
                {
                    // A 23-bit LFSR with taps at bits 4 and 22 (ps2autotests random reproduces its sequence).
                    var feedback = ((_r >> 4) ^ (_r >> 22)) & 1;
                    _r = ((_r << 1) | feedback) & 0x7FFFFF;
                    WriteLanes(ft, dest, R, R, R, R);
                }

                break;
            case 0x41: // RGET.dest ft
                WriteLanes(ft, dest, R, R, R, R);
                break;
            case 0x42: // RINIT R, fs.fsf
                _r = _vf[(fs << 2) | fsfLane] & 0x7FFFFF;
                break;
            case 0x43: // RXOR R, fs.fsf
                _r ^= _vf[(fs << 2) | fsfLane] & 0x7FFFFF;
                break;
            case 0x64: // MFP.dest ft
                WriteLanes(ft, dest, _p, _p, _p, _p);
                break;
            case 0x68: // XTOP it
                SetVi(it, (ushort)(_host.ReadTop(Index) & 0x3FF));
                _viReady[it] = 0;
                break;
            case 0x69: // XITOP it
                SetVi(it, (ushort)(_host.ReadItop(Index) & (_isVu1 ? 0x3FFu : 0xFFu)));
                _viReady[it] = 0;
                break;
            case 0x6C: // XGKICK is
                if (_isVu1)
                {
                    _host.XgKick((uint)_vi[@is] & 0x3FF);
                }

                break;
            case 0x7B: // WAITP: the stall did the work
                break;
            default:
                if (_isVu1)
                {
                    ExecuteEfu(LowerSpecial(instr), fs, fsfLane);
                }

                break;
        }
    }

    private void Branch(uint pc, uint instr)
    {
        _branchPending = true;
        _branchTarget = (uint)(pc + 8 + (Imm11(instr) << 3)) & _microMask;
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

        for (var lane = 0; lane < 4; lane++)
        {
            if ((dest & (8 >> lane)) != 0)
            {
                _vf[(ft << 2) | lane] = ReadData(qword, lane);
            }
        }

        MarkVfWrite(ft, dest);
    }

    private void Store(int fs, int dest, int qword)
    {
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
        _viReady[it] = _cycle + FmacLatency;
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

        var baseIndex = ft << 2;
        if ((dest & 8) != 0)
        {
            _vf[baseIndex] = x;
        }

        if ((dest & 4) != 0)
        {
            _vf[baseIndex + 1] = y;
        }

        if ((dest & 2) != 0)
        {
            _vf[baseIndex + 2] = z;
        }

        if ((dest & 1) != 0)
        {
            _vf[baseIndex + 3] = w;
        }

        MarkVfWrite(ft, dest);
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
    private void ExecuteEfu(int op, int fs, int fsfLane)
    {
        var baseIndex = fs << 2;
        var x = _vf[baseIndex];
        var y = _vf[baseIndex + 1];
        var z = _vf[baseIndex + 2];
        var w = _vf[baseIndex + 3];
        var f = _vf[baseIndex + fsfLane];
        switch (op)
        {
            case 0x70: // ESADD
                StartEfu(Ps2VuEfu.SquareSum(x, y, z), LatencyEsadd);
                break;
            case 0x71: // ERSADD
                StartEfu(Ps2VuEfu.Reciprocal(Ps2VuEfu.SquareSum(x, y, z)), LatencyErsadd);
                break;
            case 0x72: // ELENG
                StartEfu(Ps2VuEfu.Sqrt(Ps2VuEfu.SquareSum(x, y, z)), LatencyEleng);
                break;
            case 0x73: // ERLENG
                StartEfu(Ps2VuEfu.ReciprocalSqrt(Ps2VuEfu.SquareSum(x, y, z)), LatencyErleng);
                break;
            case 0x74: // EATANxy
                StartEfu(Ps2VuEfu.ArcTanRatio(y, x), LatencyEatan);
                break;
            case 0x75: // EATANxz
                StartEfu(Ps2VuEfu.ArcTanRatio(z, x), LatencyEatan);
                break;
            case 0x76: // ESUM
                StartEfu(Ps2VuEfu.Sum(x, y, z, w), LatencyEsum);
                break;
            case 0x78: // ESQRT
                StartEfu(Ps2VuEfu.Sqrt(f), LatencyEsqrt);
                break;
            case 0x79: // ERSQRT
                StartEfu(Ps2VuEfu.ReciprocalSqrt(f), LatencyErsqrt);
                break;
            case 0x7A: // ERCPR
                StartEfu(Ps2VuEfu.Reciprocal(f), LatencyErcpr);
                break;
            case 0x7C: // ESIN
                StartEfu(Ps2VuEfu.Sin(f), LatencyEsin);
                break;
            case 0x7D: // EATAN
                StartEfu(Ps2VuEfu.ArcTanRatio(f, 0x3F800000), LatencyEatan);
                break;
            case 0x7E: // EEXP
                StartEfu(Ps2VuEfu.Exp(f), LatencyEexp);
                break;
        }
    }
}
