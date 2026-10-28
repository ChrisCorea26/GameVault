using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// The EE's block recompiler: a run of instructions up to a branch and its delay slot (or a page's end) is translated
/// into IL once it has run a few times, and .NET's JIT turns that into machine code. A block does exactly what the
/// interpreter would, cycle for cycle (Cycles, the HI/LO and FPU stalls, idle-loop skipping, exceptions and the
/// interrupt checks between instructions), so a game runs the same with it on or off, and save states, replays,
/// rewind and netplay don't depend on it.
/// </summary>
/// <remarks>
/// <para>
/// How it stays exact: the interpreter's loop checks the slice's end, <see cref="StopRequested"/> and a pending
/// interrupt before every instruction. A block only starts when all of its instructions fit before the slice's end,
/// and inline code can't change the other two, so those checks can only fail after a call out of the block (an
/// instruction the block hands to the interpreter, the slow path of a load or store, a stall); after each such call
/// the block checks them all and leaves, with the CPU's fields exactly where the interpreter would have them, when
/// one fails.
/// </para>
/// <para>
/// Code changes: a block keeps the words it was built from and compares them with memory each time it starts, so a
/// game loading new code (by DMA, by the IOP, by the CPU itself) never runs a stale block. An EE store into a 256-byte
/// granule a block was built from leaves the block after the store, so a block that changes its own later
/// instructions behaves as the interpreter does.
/// </para>
/// <para>
/// The interpreter stays the reference: anything the recompiler doesn't translate inline (the FPU, MMI, COP0, COP2,
/// the unaligned loads and stores, traps, exceptions…) runs through <see cref="Execute"/> for that one instruction.
/// Turn the recompiler off with <see cref="RecompilerEnabled"/>.
/// </para>
/// </remarks>
public sealed partial class R5900Cpu
{
    /// <summary>Runs of a block before it is compiled (cold code isn't worth the JIT's time). Tests lower it.</summary>
    internal int CompileThreshold { get; set; } = 16;

    /// <summary>The most instructions in a block (a delay slot included).</summary>
    internal const int MaxBlockInstructions = 128;

    private const int BlockCacheSize = 4096;
    private const int CodeGranuleShift = 8;

    /// <summary>Blocks by virtual address (a game can map the same code at two addresses; each gets its own block).</summary>
    private readonly Dictionary<uint, Block> _blocks = [];

    /// <summary>A direct-mapped cache in front of <see cref="_blocks"/>.</summary>
    private readonly Block?[] _blockCache = new Block?[BlockCacheSize];

    /// <summary>One bit per 256 bytes of RDRAM: set where a block was built from.</summary>
    private readonly ulong[] _codeGranules;

    private bool _recompilerEnabled = RuntimeFeature.IsDynamicCodeCompiled;

    /// <summary>
    /// Whether blocks are recompiled (on by default where the runtime can compile code). The results are the same
    /// either way; only the speed differs. Off forgets the compiled blocks.
    /// </summary>
    public bool RecompilerEnabled
    {
        get => _recompilerEnabled;
        set
        {
            _recompilerEnabled = value && RuntimeFeature.IsDynamicCodeCompiled;
            if (!_recompilerEnabled)
            {
                _blocks.Clear();
                Array.Clear(_blockCache);
            }
        }
    }

    /// <summary>Blocks compiled so far (diagnostics and tests).</summary>
    public int CompiledBlocks { get; private set; }

    /// <summary>Instructions run in compiled blocks so far (diagnostics and tests; not part of the state).</summary>
    public long RecompiledInstructions { get; private set; }

    /// <summary>A run of instructions, with its compiled code once it is hot.</summary>
    private sealed class Block(uint pc, byte[] memory, uint offset, uint[] words)
    {
        public readonly uint Pc = pc;
        public readonly byte[] Memory = memory;
        public readonly uint Offset = offset;
        public readonly uint[] Words = words;
        public Action<R5900Cpu>? Code;
        public int Runs;

        /// <summary>The code still holds the words the block was built from, at the same place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(byte[] memory, uint offset) =>
            ReferenceEquals(memory, Memory) && offset == Offset
            && memory.AsSpan((int)offset, Words.Length * 4).SequenceEqual(MemoryMarshal.AsBytes(Words.AsSpan()));
    }

    /// <summary>
    /// Runs the compiled block at <see cref="_pc"/> if there is one and it fits in the slice; false when the
    /// interpreter should run the next instruction instead. The caller has checked the interrupt and stop flags.
    /// </summary>
    private bool TryRunBlock()
    {
        var pc = _pc;
        if ((pc & 0xFFFF_F000) != _fetchPage || _nextPc != pc + 4 || _nextIsDelaySlot)
        {
            return false; // the page isn't in the fetch cache (the interpreter's fetch fills it), or mid-branch
        }

        var memory = _fetchMemory;
        var offset = (_fetchEntry & 0xFFFF_F000) | (pc & 0xFFF);
        var slot = (int)(pc >> 2) & (BlockCacheSize - 1);
        var block = _blockCache[slot];
        if (block is null || block.Pc != pc)
        {
            if (!_blocks.TryGetValue(pc, out block))
            {
                block = Build(pc, memory, offset);
                _blocks[pc] = block;
            }

            _blockCache[slot] = block;
        }

        if (!block.Matches(memory, offset))
        {
            block = Build(pc, memory, offset); // new code here, or mapped from elsewhere
            _blocks[pc] = block;
            _blockCache[slot] = block;
        }

        if (block.Code is not { } code)
        {
            if (block.Words.Length == 0 || ++block.Runs < CompileThreshold)
            {
                return false;
            }

            code = block.Code = R5900Recompiler.Compile(block.Pc, block.Words);
            CompiledBlocks++;
            MarkCode(block);
        }

        if (Cycles + block.Words.Length > _sliceEnd)
        {
            return false; // the interpreter finishes the slice
        }

        RecompiledInstructions += block.Words.Length;
        code(this);
        return true;
    }

    /// <summary>Reads a block's words from <paramref name="pc"/> to its end (see <see cref="R5900Recompiler.BlockLength"/>).</summary>
    private static Block Build(uint pc, byte[] memory, uint offset)
    {
        var available = (int)(0x1000 - (pc & 0xFFF)) / 4;
        var words = MemoryMarshal.Cast<byte, uint>(memory.AsSpan((int)offset, available * 4));
        var length = R5900Recompiler.BlockLength(words);
        return new Block(pc, memory, offset, words[..length].ToArray());
    }

    private void MarkCode(Block block)
    {
        if (!ReferenceEquals(block.Memory, _rdram))
        {
            return; // the BIOS can't be written
        }

        var first = block.Offset >> CodeGranuleShift;
        var last = (block.Offset + (uint)block.Words.Length * 4 - 1) >> CodeGranuleShift;
        for (var granule = first; granule <= last; granule++)
        {
            _codeGranules[granule >> 6] |= 1UL << (int)(granule & 63);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCode(uint physical)
    {
        var granule = physical >> CodeGranuleShift;
        return (_codeGranules[granule >> 6] & (1UL << (int)(granule & 63))) != 0;
    }

    // ---- What compiled blocks call. Each mirrors the interpreter's loop for one instruction. ----

    /// <summary>
    /// Runs one instruction outside a delay slot as the interpreter's loop would (<see cref="Cycles"/> is the time before
    /// it). True when the block may go on with the next one: no exception or jump, no stop or interrupt, and the
    /// <paramref name="remaining"/> instructions still fit in the slice.
    /// </summary>
    internal bool JitStep(uint op, uint pc, int remaining)
    {
        _currentPc = pc;
        _inDelaySlot = false;
        _nextIsDelaySlot = false;
        Cycles++;
        _pc = pc + 4;
        _nextPc = pc + 8;
        Execute(op);
        return _pc == pc + 4 && _nextPc == pc + 8 && !_nextIsDelaySlot && !StopRequested && !_interruptPending
            && Cycles + remaining <= _sliceEnd;
    }

    /// <summary>
    /// Runs a branch the block doesn't translate (COP branches, branches to themselves). True when its delay slot
    /// should run next; <see cref="_nextPc"/> is then where the branch goes.
    /// </summary>
    internal bool JitBranch(uint op, uint pc)
    {
        _currentPc = pc;
        _inDelaySlot = false;
        _nextIsDelaySlot = false;
        Cycles++;
        _pc = pc + 4;
        _nextPc = pc + 8;
        Execute(op);
        return _nextIsDelaySlot && _pc == pc + 4 && !StopRequested && !_interruptPending && Cycles < _sliceEnd;
    }

    /// <summary>Runs a delay slot's instruction as the interpreter would, going on to <paramref name="next"/>; the block ends.</summary>
    internal void JitStepDelay(uint op, uint pc, uint next)
    {
        _currentPc = pc;
        _inDelaySlot = true;
        _nextIsDelaySlot = false;
        Cycles++;
        _pc = next;
        _nextPc = next + 4;
        Execute(op);
    }

    /// <summary>A register jump to itself: the idle-loop skip, as <see cref="Branch"/> does it.</summary>
    internal void JitIdle(uint pc)
    {
        _pc = pc + 4;
        SkipIdleLoop();
    }

    /// <summary>Translates a load's or store's address without raising anything; false for the slow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JitTranslate(uint address, bool store, out uint physical, out bool scratch)
    {
        if ((int)address < 0)
        {
            physical = address & 0x1FFF_FFFF;
            scratch = false;
            return address < 0xC000_0000 && _kernelMode && physical < _rdramSize;
        }

        var entry = _pageTable[address >> 12];
        var need = store ? PageValid | PageDirty : PageValid;
        physical = (entry & 0xFFFF_F000) | (address & 0xFFF);
        scratch = (entry & PageScratch) != 0;
        return (entry & need) == need && (scratch || physical < _rdramSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte JitData(uint physical, bool scratch) => ref scratch ? ref ScratchAt(physical) : ref RdramAt(physical);

    /// <summary>A store's target, or false for the slow path (unmapped, I/O, or code a block was built from).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool JitStoreTarget(uint address, out uint physical, out bool scratch) =>
        JitTranslate(address, true, out physical, out scratch) && (scratch || !IsCode(physical));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLoad8(uint address, out byte value)
    {
        if (JitTranslate(address, false, out var physical, out var scratch))
        {
            value = JitData(physical, scratch);
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLoad16(uint address, out ushort value)
    {
        if ((address & 1) == 0 && JitTranslate(address, false, out var physical, out var scratch))
        {
            value = Unsafe.ReadUnaligned<ushort>(ref JitData(physical, scratch));
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLoad32(uint address, out uint value)
    {
        if ((address & 3) == 0 && JitTranslate(address, false, out var physical, out var scratch))
        {
            value = Unsafe.ReadUnaligned<uint>(ref JitData(physical, scratch));
            return true;
        }

        value = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLoad64(uint address, out ulong value)
    {
        if ((address & 7) == 0 && JitTranslate(address, false, out var physical, out var scratch))
        {
            value = Unsafe.ReadUnaligned<ulong>(ref JitData(physical, scratch));
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>LQ into <paramref name="rt"/> (nothing for r0); false for the slow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLq(uint address, int rt)
    {
        if (!JitTranslate(address & ~15u, false, out var physical, out var scratch))
        {
            return false;
        }

        if (rt != 0)
        {
            ref var data = ref JitData(physical, scratch);
            Gpr(rt) = Unsafe.ReadUnaligned<ulong>(ref data);
            GprHigh(rt) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, 8));
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitStore8(uint address, byte value)
    {
        if (JitStoreTarget(address, out var physical, out var scratch))
        {
            JitData(physical, scratch) = value;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitStore16(uint address, ushort value)
    {
        if ((address & 1) == 0 && JitStoreTarget(address, out var physical, out var scratch))
        {
            Unsafe.WriteUnaligned(ref JitData(physical, scratch), value);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitStore32(uint address, uint value)
    {
        if ((address & 3) == 0 && JitStoreTarget(address, out var physical, out var scratch))
        {
            Unsafe.WriteUnaligned(ref JitData(physical, scratch), value);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitStore64(uint address, ulong value)
    {
        if ((address & 7) == 0 && JitStoreTarget(address, out var physical, out var scratch))
        {
            Unsafe.WriteUnaligned(ref JitData(physical, scratch), value);
            return true;
        }

        return false;
    }

    /// <summary>SQ from <paramref name="rt"/>; false for the slow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitSq(uint address, int rt)
    {
        if (!JitStoreTarget(address & ~15u, out var physical, out var scratch))
        {
            return false;
        }

        ref var data = ref JitData(physical, scratch);
        Unsafe.WriteUnaligned(ref data, Gpr(rt));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref data, 8), GprHigh(rt));
        return true;
    }

    /// <summary>LWC1: false (nothing done) for the slow path. It reads <see cref="Cycles"/> (the FPU's stall).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitLwc1(uint address, int ft)
    {
        if ((_status & StatusCu1) == 0 || !JitLoad32(address, out var value))
        {
            return false;
        }

        FpuWait();
        _fpr[ft] = value;
        return true;
    }

    /// <summary>
    /// The S-format functions <see cref="JitCop1"/> runs: the arithmetic, moves, compares, MAX/MIN and CVT.W.S, which
    /// raise no exception and don't branch.
    /// </summary>
    private const ulong JitFpuFunctions =
        0xFFul | 1ul << 0x16 | 0xF7ul << 0x18 /* 18–1A, 1C–1F */ | 1ul << 0x24 | 1ul << 0x28 | 1ul << 0x29
        | 1ul << 0x30 | 1ul << 0x32 | 1ul << 0x34 | 1ul << 0x36;

    /// <summary>
    /// The FPU without the interpreter's call around it (ps2-full-speed.md §2): MFC1, MTC1, CVT.S.W and the S format's
    /// <see cref="JitFpuFunctions"/>, as <see cref="ExecuteCop1"/> does them, with the FPU's stall. False (nothing done)
    /// for the slow path: COP1 unusable, its branches, CFC1/CTC1, a reserved encoding, or MFC1 to r0.
    /// </summary>
    internal bool JitCop1(uint op)
    {
        if ((_status & StatusCu1) == 0)
        {
            return false;
        }

        var fs = (int)(op >> 11) & 31;
        var rt = (int)(op >> 16) & 31;
        switch ((op >> 21) & 31)
        {
            case 0x00 when rt != 0: // MFC1
                FpuWait();
                Gpr(rt) = SignExtend32(_fpr[fs]);
                return true;
            case 0x04: // MTC1
                FpuWait();
                _fpr[fs] = (uint)Gpr(rt);
                return true;
            case 0x10 when (JitFpuFunctions >> (int)(op & 0x3F) & 1) != 0:
                FpuWait();
                ExecuteFpuSingle(op, fs, rt);
                return true;
            case 0x14 when (op & 0x3F) == 0x20: // CVT.S.W
                FpuWait();
                _fpr[(int)(op >> 6) & 31] = IntToFloat((int)_fpr[fs]);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The S-format functions a block calls <see cref="JitFpuSingle"/> for, with the function as a constant.</summary>
    internal const ulong JitInlineFpuFunctions =
        1ul << 0x00 | 1ul << 0x01 | 1ul << 0x02 | 1ul << 0x05 | 1ul << 0x06 | 1ul << 0x07
        | 1ul << 0x18 | 1ul << 0x19 | 1ul << 0x1A | 1ul << 0x1C | 1ul << 0x1D | 1ul << 0x1E | 1ul << 0x1F;

    /// <summary>
    /// One of <see cref="JitInlineFpuFunctions"/>, as <see cref="ExecuteFpuSingle"/> does it. Inlined into the block
    /// with <paramref name="function"/> a constant, so the switch folds away. False (nothing done) when COP1 is unusable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitFpuSingle(int function, int fd, int fs, int ft)
    {
        if ((_status & StatusCu1) == 0)
        {
            return false;
        }

        FpuWait();
        var s = _fpr[fs];
        var t = _fpr[ft];
        switch (function)
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
            default: // MSUBA.S
                _fpuAcc = MultiplyAdd(_fpuAcc, s, t, true);
                break;
        }

        return true;
    }

    /// <summary>SWC1: false (nothing done) for the slow path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool JitSwc1(uint address, int ft) => (_status & StatusCu1) != 0 && JitStore32(address, _fpr[ft]);

    /// <summary>The multiplies: HI/LO, rd, and when the result is ready (the interpreter's <see cref="SetHiLo"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void JitMult(int rs, int rt, int rd)
    {
        SetHiLo((long)(int)Gpr(rs) * (int)Gpr(rt), rd);
        _gpr[0] = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void JitMultu(int rs, int rt, int rd)
    {
        SetHiLo((long)((ulong)(uint)Gpr(rs) * (uint)Gpr(rt)), rd);
        _gpr[0] = 0;
    }

    /// <summary>MFHI/MFLO: the stall, then the move.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void JitMfHiLo(int rd, bool hi)
    {
        WaitHiLo();
        if (rd != 0)
        {
            Gpr(rd) = hi ? _hi : _lo;
        }
    }
}
