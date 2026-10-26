using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// A vector unit (SPEC §7.11, Phase 17 plan §2): VU0 (4 KB micro memory, 4 KB data memory, also the EE's COP2 in macro
/// mode) or VU1 (16 KB each, with the EFU and XGKICK). In micro mode it runs 64-bit instruction pairs from micro memory,
/// an upper FMAC operation and a lower operation each cycle, with the pipeline as programs see it: FMAC results and the
/// MAC, status and clipping flags appear four cycles after issue (readers of a result stall; flag instructions read the
/// old flags), Q after DIV/SQRT (7) or RSQRT (13), P after the EFU's latency, and the integer branch hazard.
/// </summary>
/// <remarks>
/// References: ps2tek "Vector Unit (VU)" (registers, instruction format, pipelining), "COP2 Special1/Special2"; the VU
/// User's Manual as Play! (BSD-2-Clause) and PCSX2 describe it; ps2autotests' VU programs (fdivdelay, clip, branch,
/// random, integer and vu0_macro), whose recorded results the unit tests reproduce.
/// Register writes go straight to the register file and readers stall until they are due, which gives the same values
/// as a pipelined write-back; only Q, P and the flags, which programs read without interlocks, are held back.
/// </remarks>
public sealed partial class Ps2Vu : IPs2VectorUnit, IR5900Cop2, IStateful
{
    public const int Vu0MemorySize = 4 * 1024;
    public const int Vu1MemorySize = 16 * 1024;

    /// <summary>Upper-word bits (ps2tek: "VU Instruction Format and Decoding").</summary>
    public const uint IBit = 0x80000000;
    public const uint EBit = 0x40000000;
    public const uint MBit = 0x20000000;
    public const uint DBit = 0x10000000;
    public const uint TBit = 0x08000000;

    /// <summary>FMAC results, MAC/status/clipping flags and LSU integer loads become visible this many cycles after issue.</summary>
    public const int FmacLatency = 4;

    /// <summary>DIV and SQRT write Q seven cycles after issue, RSQRT thirteen (ps2tek: "FDIV Pipeline"; ps2autotests fdivdelay).</summary>
    public const int DivLatency = 7;
    public const int RsqrtLatency = 13;

    private const uint One = 0x3F800000;
    private const int FlagQueueSize = 8;
    private const byte FlagKindMac = 1;
    private const byte FlagKindClip = 2;

    private static readonly StateTag Tag = StateTag.From("PVU ");

    private readonly IPs2VuHost _host;
    private readonly byte[] _micro;
    private readonly byte[] _data;
    private readonly uint _microMask;
    private readonly int _dataQwordMask;
    private readonly bool _isVu1;
    private readonly DecodedPair[] _decoded;

    // Registers. VF lanes are raw float bits, x first; VF00 is (0, 0, 0, 1) and ignores writes.
    private readonly uint[] _vf = new uint[32 * 4];
    private readonly uint[] _acc = new uint[4];
    private readonly ushort[] _vi = new ushort[16];
    private uint _i;
    private uint _q;
    private uint _p;
    private uint _r; // The random number's 23-bit mantissa (RGET gives 1.0 | R).
    private int _status;
    private int _mac;
    private int _clip;

    // Micro-mode execution.
    private uint _pc;
    private bool _running;
    private bool _endPending;
    private bool _branchPending;
    private uint _branchTarget;
    private long _cycle;
    private bool _dStop;
    private bool _tStop;
    private bool _forceBreak;

    // The pipeline: when each VF lane, VF register (any lane) and VI register (LSU loads) can be read.
    private readonly long[] _vfReady = new long[32 * 4];
    private readonly long[] _vfRegReady = new long[32];
    private readonly long[] _viReady = new long[16];

    // Flag updates in flight: MAC (with the status bits it implies) or a CLIP result, in issue order.
    private readonly long[] _flagReady = new long[FlagQueueSize];
    private readonly int[] _flagValue = new int[FlagQueueSize];
    private readonly byte[] _flagKind = new byte[FlagQueueSize];
    private int _flagHead;
    private int _flagCount;

    // Q and P in flight.
    private bool _qPending;
    private uint _qNext;
    private int _qNextFlags;
    private long _qReady;
    private bool _pPending;
    private uint _pNext;
    private long _pReady;

    // The integer branch hazard: the VI the previous pair's integer ALU wrote, with its old value.
    private int _intWriteReg = -1;
    private ushort _intWriteOld;
    private int _prevIntWriteReg = -1;
    private ushort _prevIntWriteOld;
    private bool _pairStalled;

    // The upper result waits here while the lower instruction of the same pair reads the old registers.
    private readonly uint[] _stage = new uint[4];
    private int _stageReg;
    private int _stageMask;

    // Set while a macro-mode instruction runs: its results are complete at once, so nothing is marked in flight.
    private bool _inMacro;

    // VU0's control registers for both units (CMSAR0/1, FBRST's enables) and the reserved register 24.
    private uint _cmsar0;
    private uint _cmsar1;
    private uint _reserved24;

    public Ps2Vu(int index, IPs2VuHost host)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 1);
        ArgumentNullException.ThrowIfNull(host);
        Index = index;
        _host = host;
        _isVu1 = index == 1;
        var size = _isVu1 ? Vu1MemorySize : Vu0MemorySize;
        _micro = new byte[size];
        _data = new byte[size];
        _microMask = (uint)size - 1;
        _dataQwordMask = size / 16 - 1;
        _decoded = new DecodedPair[size / 8];
        _vf[3] = One;
    }

    /// <inheritdoc/>
    public int Index { get; }

    /// <inheritdoc/>
    public byte[] MicroMemory => _micro;

    /// <inheritdoc/>
    public byte[] DataMemory => _data;

    /// <inheritdoc/>
    public bool Running => _running;

    /// <summary>The cycles this unit has run (stalls included), for tests and the scheduler.</summary>
    public long Cycles => _cycle;

    /// <summary>The micro-mode program counter in bytes (TPC × 8).</summary>
    public uint Pc => _pc;

    /// <summary>FBRST's DE bit for this unit: a D bit stops the program and interrupts.</summary>
    public bool DebugEnable { get; set; }

    /// <summary>FBRST's TE bit for this unit: a T bit stops the program and interrupts.</summary>
    public bool TrapEnable { get; set; }

    /// <summary>This unit's four VPU-STAT bits: running, stopped by D, stopped by T, force-broken.</summary>
    public int StatBits => (_running ? 1 : 0) | (_dStop ? 2 : 0) | (_tStop ? 4 : 0) | (_forceBreak ? 8 : 0);

    public uint Status => (uint)_status;

    public uint Mac => (uint)_mac;

    public uint Clipping => (uint)_clip;

    public uint I { get => _i; set => _i = value; }

    public uint Q { get => _q; set => _q = value; }

    public uint P { get => _p; set => _p = value; }

    /// <summary>R as RGET reads it: 1.0 with the generator's 23 bits as the mantissa.</summary>
    public uint R { get => One | _r; set => _r = value & 0x7FFFFF; }

    public ushort GetVi(int index) => _vi[index & 15];

    public void SetVi(int index, ushort value)
    {
        if ((index & 15) != 0)
        {
            _vi[index & 15] = value;
        }
    }

    public uint GetVf(int index, int lane) => _vf[((index & 31) << 2) | (lane & 3)];

    public void SetVf(int index, int lane, uint value)
    {
        if ((index & 31) != 0)
        {
            _vf[((index & 31) << 2) | (lane & 3)] = value;
        }
    }

    public uint GetAcc(int lane) => _acc[lane & 3];

    /// <inheritdoc/>
    public void StartMicro(uint address)
    {
        _pc = address & _microMask & ~7u;
        ContinueMicro();
    }

    /// <inheritdoc/>
    public void ContinueMicro()
    {
        _running = true;
        _dStop = _tStop = _forceBreak = false;
        _endPending = false;
        _branchPending = false;
        _intWriteReg = _prevIntWriteReg = -1;
    }

    /// <inheritdoc/>
    public long Run(long cycles)
    {
        if (!_running || cycles <= 0)
        {
            return 0;
        }

        var start = _cycle;
        var end = start + cycles;
        while (_running && _cycle < end)
        {
            Step();
        }

        return _cycle - start;
    }

    /// <summary>FBRST's force break: stops a running program.</summary>
    public void ForceBreak()
    {
        if (_running)
        {
            Stop();
            _forceBreak = true;
        }
    }

    /// <summary>FBRST's reset: stops the unit and clears its stop bits and interrupt enables.</summary>
    public void Reset()
    {
        if (_running)
        {
            Stop();
        }

        _dStop = _tStop = _forceBreak = false;
        DebugEnable = TrapEnable = false;
    }

    /// <summary>Executes one instruction pair (plus any stall before it).</summary>
    private void Step()
    {
        var pc = _pc;
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(_micro, (int)pc, 8));
        ref var d = ref _decoded[pc >> 3];
        if (d.Raw != raw || !d.Valid)
        {
            Decode(ref d, raw);
        }

        // Wait for the operands still in the pipeline, and for a busy FDIV/EFU (or WAITQ/WAITP).
        long ready = 0;
        if (d.UMaskA != 0)
        {
            ready = VfReady(d.UReadA, d.UMaskA);
        }

        if (d.UMaskB != 0)
        {
            ready = Math.Max(ready, VfReady(d.UReadB, d.UMaskB));
        }

        if (d.LMaskA != 0)
        {
            ready = Math.Max(ready, VfReady(d.LReadA, d.LMaskA));
        }

        if (d.LMaskB != 0)
        {
            ready = Math.Max(ready, VfReady(d.LReadB, d.LMaskB));
        }

        if (d.ViA != 0)
        {
            ready = Math.Max(ready, _viReady[d.ViA]);
        }

        if (d.ViB != 0)
        {
            ready = Math.Max(ready, _viReady[d.ViB]);
        }

        if (d.Waits != 0)
        {
            if (d.Waits == WaitsForFdiv)
            {
                if (_qPending)
                {
                    ready = Math.Max(ready, _qReady);
                }
            }
            else if (_pPending)
            {
                ready = Math.Max(ready, _pReady);
            }
        }

        _pairStalled = ready > _cycle;
        if (_pairStalled)
        {
            _cycle = ready;
        }

        CommitPipeline();
        _prevIntWriteReg = _intWriteReg;
        _prevIntWriteOld = _intWriteOld;
        _intWriteReg = -1;

        var nextPc = pc + 8;
        if (_branchPending)
        {
            nextPc = _branchTarget;
            _branchPending = false;
        }

        _stageReg = -1;
        ExecuteUpper(ref d);
        if (d.Immediate)
        {
            _i = (uint)raw;
        }
        else
        {
            ExecuteLower(ref d, pc);
        }

        CommitStage();
        _cycle++;
        _pc = nextPc & _microMask;

        // The E bit ends the program after the next pair; D and T stop it now when FBRST enables them.
        var bits = d.UpperBits;
        var end = _endPending;
        _endPending = !end && (bits & EBit) != 0;
        if ((bits & (DBit | TBit)) != 0)
        {
            if ((bits & DBit) != 0 && DebugEnable)
            {
                _dStop = true;
            }

            if ((bits & TBit) != 0 && TrapEnable)
            {
                _tStop = true;
            }

            if (_dStop || _tStop)
            {
                Stop();
                _host.RaiseInterrupt(Index);
                return;
            }
        }

        if (end)
        {
            Stop();
        }
    }

    /// <summary>Decodes an instruction pair into the cache entry for its address.</summary>
    private void Decode(ref DecodedPair d, ulong raw)
    {
        var upper = (uint)(raw >> 32);
        DecodeUpper(ref d, upper);
        d.UpperBits = upper & (EBit | DBit | TBit);
        d.Immediate = (upper & IBit) != 0;
        if (d.Immediate)
        {
            d.Lower = LowerOp.Nop;
            d.LMaskA = d.LMaskB = d.ViA = d.ViB = d.Waits = 0;
        }
        else
        {
            DecodeLower(ref d, (uint)raw);
        }

        d.Raw = raw;
        d.Valid = true;
    }

    /// <summary>
    /// A decoded instruction pair: what it does and what it waits for. Each micro-memory pair has one, checked against
    /// the raw doubleword before use, so MPG uploads and direct writes to <see cref="MicroMemory"/> need no flush.
    /// </summary>
    private struct DecodedPair
    {
        public ulong Raw;
        public bool Valid;
        public bool Immediate;
        public uint UpperBits;

        public UpperKind Upper;
        public byte Operand;
        public bool ToAcc;
        public byte UDest;
        public byte Fd;
        public byte Fs;
        public byte Ft;
        public byte Bc;
        public byte UReadA;
        public byte UMaskA;
        public byte UReadB;
        public byte UMaskB;

        public LowerOp Lower;
        public byte LDest;
        public byte It;
        public byte Is;
        public byte Id;
        public byte Fsf;
        public byte Ftf;
        public int Imm;
        public byte LReadA;
        public byte LMaskA;
        public byte LReadB;
        public byte LMaskB;
        public byte ViA;
        public byte ViB;
        public byte Waits;
    }

    private void Stop()
    {
        _running = false;
        _endPending = false;
        _branchPending = false;
        FlushPipeline();
    }

    /// <summary>Applies the flag updates, Q and P that are due by now.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitPipeline()
    {
        while (_flagCount > 0 && _flagReady[_flagHead] <= _cycle)
        {
            ApplyFlag(_flagHead);
            _flagHead = (_flagHead + 1) & (FlagQueueSize - 1);
            _flagCount--;
        }

        if (_qPending && _qReady <= _cycle)
        {
            ApplyQ();
        }

        if (_pPending && _pReady <= _cycle)
        {
            _p = _pNext;
            _pPending = false;
        }
    }

    /// <summary>Completes everything in flight at once, at a program's end.</summary>
    private void FlushPipeline()
    {
        CompletePending();
        Array.Clear(_vfReady);
        Array.Clear(_vfRegReady);
        Array.Clear(_viReady);
        _intWriteReg = _prevIntWriteReg = -1;
    }

    /// <summary>Applies every flag update, Q and P in flight (a macro instruction's results, at once).</summary>
    private void CompletePending()
    {
        while (_flagCount > 0)
        {
            ApplyFlag(_flagHead);
            _flagHead = (_flagHead + 1) & (FlagQueueSize - 1);
            _flagCount--;
        }

        if (_qPending)
        {
            ApplyQ();
        }

        if (_pPending)
        {
            _p = _pNext;
            _pPending = false;
        }
    }

    private void ApplyFlag(int slot)
    {
        var value = _flagValue[slot];
        if (_flagKind[slot] == FlagKindClip)
        {
            _clip = ((_clip << 6) | value) & 0xFFFFFF;
            return;
        }

        _mac = value;
        var zsuo = ((value & 0x000F) != 0 ? 1 : 0)
                   | ((value & 0x00F0) != 0 ? 2 : 0)
                   | ((value & 0x0F00) != 0 ? 4 : 0)
                   | ((value & 0xF000) != 0 ? 8 : 0);
        _status = (_status & 0xFF0) | zsuo | (zsuo << 6);
    }

    private void ApplyQ()
    {
        _q = _qNext;
        _status = (_status & ~0x30) | _qNextFlags | (_qNextFlags << 6);
        _qPending = false;
    }

    private void QueueFlag(byte kind, int value)
    {
        if (_flagCount == FlagQueueSize)
        {
            // Can't happen in micro mode (one FMAC per cycle, four in flight); keep the oldest update rather than lose it.
            ApplyFlag(_flagHead);
            _flagHead = (_flagHead + 1) & (FlagQueueSize - 1);
            _flagCount--;
        }

        var slot = (_flagHead + _flagCount) & (FlagQueueSize - 1);
        _flagReady[slot] = _cycle + FmacLatency;
        _flagValue[slot] = value;
        _flagKind[slot] = kind;
        _flagCount++;
    }

    /// <summary>The cycle when the given lanes of a VF register can be read (0 when they already can).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long VfReady(int reg, int mask)
    {
        if (_vfRegReady[reg] <= _cycle || reg == 0)
        {
            return 0;
        }

        long ready = 0;
        var baseIndex = reg << 2;
        for (var lane = 0; lane < 4; lane++)
        {
            if ((mask & (8 >> lane)) != 0 && _vfReady[baseIndex + lane] > ready)
            {
                ready = _vfReady[baseIndex + lane];
            }
        }

        return ready;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ViReady(int reg) => _viReady[reg & 15];

    /// <summary>Marks the lanes of a VF register written by an instruction issued this cycle.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkVfWrite(int reg, int mask)
    {
        if (reg == 0 || mask == 0 || _inMacro)
        {
            return;
        }

        var ready = _cycle + FmacLatency;
        _vfRegReady[reg] = ready;
        var baseIndex = reg << 2;
        if ((mask & 8) != 0)
        {
            _vfReady[baseIndex] = ready;
        }

        if ((mask & 4) != 0)
        {
            _vfReady[baseIndex + 1] = ready;
        }

        if ((mask & 2) != 0)
        {
            _vfReady[baseIndex + 2] = ready;
        }

        if ((mask & 1) != 0)
        {
            _vfReady[baseIndex + 3] = ready;
        }
    }

    /// <summary>Writes the <paramref name="mask"/> lanes of a VF register (not VF00) and marks them in flight.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteVfMasked(int reg, int mask, Vector128<uint> value)
    {
        ref var vf = ref MemoryMarshal.GetArrayDataReference(_vf);
        var index = (nuint)(reg << 2);
        Vector128.ConditionalSelect(s_laneMasks[mask], value, Vector128.LoadUnsafe(ref vf, index)).StoreUnsafe(ref vf, index);
        MarkVfWrite(reg, mask);
    }

    private void CommitStage()
    {
        var reg = _stageReg;
        if (reg <= 0)
        {
            return;
        }

        WriteVfMasked(reg, _stageMask, Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_stage)));
    }

    // Data memory, in quadwords. VU0's addresses $4000-$43FF (quadwords $400-$43F) reach VU1's registers.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadData(int qword, int lane)
    {
        if (!_isVu1 && (qword & 0x400) != 0)
        {
            return _host.Vu1 is Ps2Vu vu1 ? vu1.ReadRegisterLane(qword & 0x3F, lane) : 0;
        }

        var offset = ((qword & _dataQwordMask) << 4) | (lane << 2);
        return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_data, offset, 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteData(int qword, int lane, uint value)
    {
        if (!_isVu1 && (qword & 0x400) != 0)
        {
            if (_host.Vu1 is Ps2Vu vu1)
            {
                vu1.WriteRegisterLane(qword & 0x3F, lane, value);
            }

            return;
        }

        var offset = ((qword & _dataQwordMask) << 4) | (lane << 2);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(_data, offset, 4), value);
    }

    /// <summary>
    /// A register as VU0 sees it in its data space at $4000 + 16 × <paramref name="index"/>: VF00–VF31, then VI00–VI15,
    /// then the control registers (status, MAC, clipping, -, R, I, Q, P, -, -, TPC) in lane x.
    /// </summary>
    internal uint ReadRegisterLane(int index, int lane)
    {
        if (index < 32)
        {
            return _vf[(index << 2) | lane];
        }

        if (lane != 0)
        {
            return 0;
        }

        if (index < 48)
        {
            return _vi[index - 32];
        }

        return (index - 32) switch
        {
            16 => (uint)_status,
            17 => (uint)_mac,
            18 => (uint)_clip,
            20 => R,
            21 => _i,
            22 => _q,
            23 => _p,
            26 => _pc >> 3,
            _ => 0,
        };
    }

    internal void WriteRegisterLane(int index, int lane, uint value)
    {
        if (index < 32)
        {
            SetVf(index, lane, value);
            return;
        }

        if (lane != 0)
        {
            return;
        }

        if (index < 48)
        {
            SetVi(index - 32, (ushort)value);
            return;
        }

        switch (index - 32)
        {
            case 16:
                _status = (_status & 0x3F) | (int)(value & 0xFC0);
                break;
            case 18:
                _clip = (int)(value & 0xFFFFFF);
                break;
            case 20:
                _r = value & 0x7FFFFF;
                break;
            case 21:
                _i = value;
                break;
            case 22:
                _q = value;
                break;
            case 23:
                _p = value;
                break;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_micro);
        writer.WriteBytes(_data);
        foreach (var v in _vf)
        {
            writer.WriteUInt32(v);
        }

        foreach (var v in _acc)
        {
            writer.WriteUInt32(v);
        }

        foreach (var v in _vi)
        {
            writer.WriteUInt16(v);
        }

        writer.WriteUInt32(_i);
        writer.WriteUInt32(_q);
        writer.WriteUInt32(_p);
        writer.WriteUInt32(_r);
        writer.WriteInt32(_status);
        writer.WriteInt32(_mac);
        writer.WriteInt32(_clip);

        writer.WriteUInt32(_pc);
        writer.WriteBool(_running);
        writer.WriteBool(_endPending);
        writer.WriteBool(_branchPending);
        writer.WriteUInt32(_branchTarget);
        writer.WriteInt64(_cycle);
        writer.WriteBool(_dStop);
        writer.WriteBool(_tStop);
        writer.WriteBool(_forceBreak);
        writer.WriteBool(DebugEnable);
        writer.WriteBool(TrapEnable);

        foreach (var v in _vfReady)
        {
            writer.WriteInt64(v);
        }

        foreach (var v in _vfRegReady)
        {
            writer.WriteInt64(v);
        }

        foreach (var v in _viReady)
        {
            writer.WriteInt64(v);
        }

        writer.WriteInt32(_flagCount);
        for (var n = 0; n < _flagCount; n++)
        {
            var slot = (_flagHead + n) & (FlagQueueSize - 1);
            writer.WriteInt64(_flagReady[slot]);
            writer.WriteInt32(_flagValue[slot]);
            writer.WriteByte(_flagKind[slot]);
        }

        writer.WriteBool(_qPending);
        writer.WriteUInt32(_qNext);
        writer.WriteInt32(_qNextFlags);
        writer.WriteInt64(_qReady);
        writer.WriteBool(_pPending);
        writer.WriteUInt32(_pNext);
        writer.WriteInt64(_pReady);
        writer.WriteInt32(_intWriteReg);
        writer.WriteUInt16(_intWriteOld);

        writer.WriteUInt32(_cmsar0);
        writer.WriteUInt32(_cmsar1);
        writer.WriteUInt32(_reserved24);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_micro);
        reader.ReadBytes(_data);
        for (var n = 0; n < _vf.Length; n++)
        {
            _vf[n] = reader.ReadUInt32();
        }

        for (var n = 0; n < _acc.Length; n++)
        {
            _acc[n] = reader.ReadUInt32();
        }

        for (var n = 0; n < _vi.Length; n++)
        {
            _vi[n] = reader.ReadUInt16();
        }

        _vf[0] = _vf[1] = _vf[2] = 0;
        _vf[3] = One;
        _vi[0] = 0;
        _i = reader.ReadUInt32();
        _q = reader.ReadUInt32();
        _p = reader.ReadUInt32();
        _r = reader.ReadUInt32() & 0x7FFFFF;
        _status = reader.ReadInt32() & 0xFFF;
        _mac = reader.ReadInt32() & 0xFFFF;
        _clip = reader.ReadInt32() & 0xFFFFFF;

        _pc = reader.ReadUInt32() & _microMask & ~7u;
        _running = reader.ReadBool();
        _endPending = reader.ReadBool();
        _branchPending = reader.ReadBool();
        _branchTarget = reader.ReadUInt32() & _microMask;
        _cycle = reader.ReadInt64();
        _dStop = reader.ReadBool();
        _tStop = reader.ReadBool();
        _forceBreak = reader.ReadBool();
        DebugEnable = reader.ReadBool();
        TrapEnable = reader.ReadBool();

        for (var n = 0; n < _vfReady.Length; n++)
        {
            _vfReady[n] = reader.ReadInt64();
        }

        for (var n = 0; n < _vfRegReady.Length; n++)
        {
            _vfRegReady[n] = reader.ReadInt64();
        }

        for (var n = 0; n < _viReady.Length; n++)
        {
            _viReady[n] = reader.ReadInt64();
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > FlagQueueSize)
        {
            throw new StateFormatException($"VU flag queue length {count} is out of range.");
        }

        _flagHead = 0;
        _flagCount = count;
        for (var n = 0; n < count; n++)
        {
            _flagReady[n] = reader.ReadInt64();
            _flagValue[n] = reader.ReadInt32();
            _flagKind[n] = reader.ReadByte();
        }

        _qPending = reader.ReadBool();
        _qNext = reader.ReadUInt32();
        _qNextFlags = reader.ReadInt32() & 0x30;
        _qReady = reader.ReadInt64();
        _pPending = reader.ReadBool();
        _pNext = reader.ReadUInt32();
        _pReady = reader.ReadInt64();
        _intWriteReg = reader.ReadInt32();
        if (_intWriteReg is < -1 or > 15)
        {
            _intWriteReg = -1;
        }

        _intWriteOld = reader.ReadUInt16();
        _prevIntWriteReg = -1;

        _cmsar0 = reader.ReadUInt32();
        _cmsar1 = reader.ReadUInt32();
        _reserved24 = reader.ReadUInt32();
        reader.EndChunk();
    }
}
