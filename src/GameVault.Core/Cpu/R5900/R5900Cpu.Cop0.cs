using GameVault.Core.State;

namespace GameVault.Core.Cpu.R5900;

/// <summary>A TLB entry: an even/odd pair of pages (ps2tek: "EE COP0 Memory Management").</summary>
internal struct R5900TlbEntry
{
    public uint PageMask;

    /// <summary>VPN2 (above the page mask) and the ASID.</summary>
    public uint EntryHi;

    /// <summary>The even page: S (bit 31, the scratchpad), PFN, C, D, V.</summary>
    public uint EntryLo0;

    public uint EntryLo1;
    public bool Global;

    /// <summary>The EntryHi bits a virtual address must match: VPN2 above the page mask.</summary>
    public readonly uint CompareMask => 0xFFFF_E000 & ~PageMask;

    /// <summary>The address bit that picks the odd page.</summary>
    public readonly uint OddBit => ((PageMask >> 1) | 0xFFFu) + 1;

    public readonly bool Scratchpad => (EntryLo0 & 0x8000_0000) != 0;
}

/// <summary>COP0: the system control registers, exceptions, interrupts, the timer and the TLB.</summary>
public sealed partial class R5900Cpu
{
    /// <summary>COP0 register 15: the EE's implementation (0x2E) and revision.</summary>
    public const uint ProcessorId = 0x0000_2E20;

    /// <summary>The TLB's size.</summary>
    public const int TlbEntries = 48;

    // Status bits (ps2tek: "EE COP0 Exception Handling").
    public const uint StatusIe = 1 << 0;
    public const uint StatusExl = 1 << 1;
    public const uint StatusErl = 1 << 2;
    public const uint StatusIm2 = 1 << 10;
    public const uint StatusIm3 = 1 << 11;
    public const uint StatusBem = 1 << 12;
    public const uint StatusIm7 = 1 << 15;
    public const uint StatusEie = 1 << 16;
    public const uint StatusEdi = 1 << 17;
    public const uint StatusBev = 1 << 22;
    public const uint StatusDev = 1 << 23;
    public const uint StatusCu0 = 1 << 28;
    public const uint StatusCu1 = 1 << 29;
    public const uint StatusCu2 = 1 << 30;

    /// <summary>The Status bits software can write: IE, EXL, ERL, KSU, the IM bits, BEM, EIE, EDI, CH, BEV, DEV, CU.</summary>
    private const uint StatusWriteMask = 0xF0C7_9C1F;

    // Cause bits.
    public const uint CauseIp2 = 1 << 10;
    public const uint CauseIp3 = 1 << 11;
    public const uint CauseIp7 = 1 << 15;
    private const uint CauseBd2 = 1u << 30;
    private const uint CauseBd = 1u << 31;

    internal readonly R5900TlbEntry[] Tlb = new R5900TlbEntry[TlbEntries];

    private uint _index;
    private uint _entryLo0;
    private uint _entryLo1;
    private uint _context;
    private uint _pageMask;
    private uint _wired;
    private uint _badVAddr;
    private long _countOffset;
    private uint _entryHi;
    private uint _compare;
    private uint _status;
    private uint _cause;
    private uint _epc;
    private uint _config;
    private uint _badPAddr;
    private uint _tagLo;
    private uint _tagHi;
    private uint _errorEpc;

    // The debug registers (BPC, IAB, IABM, DAB, DABM, DVB, DVBM) are stored but don't break.
    private readonly uint[] _debug = new uint[8];

    // The performance counters: PCCR and PCR0/PCR1, counted when read (approximate: every enabled counter counts cycles).
    private uint _pccr;
    private long _pcr0Base;
    private long _pcr1Base;
    private uint _pcr0;
    private uint _pcr1;

    /// <summary>When Random was last set to 47 (by a Wired write or reset).</summary>
    private long _randomBase;

    private long _compareCycle = long.MaxValue;
    private bool _interruptPending;
    private bool _kernelMode;
    private bool _supervisorMode;
    private bool _int0;
    private bool _int1;

    /// <summary>The INTC's interrupt line (INT0, Cause IP2).</summary>
    public bool Int0
    {
        get => _int0;
        set
        {
            _int0 = value;
            _cause = value ? _cause | CauseIp2 : _cause & ~CauseIp2;
            UpdateInterruptPending();
        }
    }

    /// <summary>The DMAC's interrupt line (INT1, Cause IP3).</summary>
    public bool Int1
    {
        get => _int1;
        set
        {
            _int1 = value;
            _cause = value ? _cause | CauseIp3 : _cause & ~CauseIp3;
            UpdateInterruptPending();
        }
    }

    /// <summary>The DMAC's CPCOND0 output, which BC0F/BC0T test (D_PCR: every enabled channel idle).</summary>
    public bool Cpcond0;

    public uint Status
    {
        get => _status;
        set
        {
            _status = value;
            StatusChanged();
        }
    }

    public uint Cause => _cause;

    public uint Epc => _epc;

    public uint ErrorEpc => _errorEpc;

    public uint BadVAddr => _badVAddr;

    /// <summary>COP0 Count: one tick per EE cycle (ps2tek: "EE COP0 Timer").</summary>
    public uint Count
    {
        get => (uint)(Cycles + _countOffset);
        set
        {
            _countOffset = value - Cycles;
            ScheduleCompare();
        }
    }

    public uint Compare => _compare;

    /// <summary>When the timer interrupt (Count reaching Compare) fires next, in <see cref="Cycles"/>.</summary>
    public long NextCompareCycle => _compareCycle;

    private void ResetCop0()
    {
        Array.Clear(Tlb);
        Array.Clear(_debug);
        _index = _entryLo0 = _entryLo1 = _context = _pageMask = _wired = _compare = 0;
        _badVAddr = _entryHi = _epc = _errorEpc = _badPAddr = _tagLo = _tagHi = 0;
        _pccr = _pcr0 = _pcr1 = 0;
        _pcr0Base = _pcr1Base = Cycles;
        _cause = (_int0 ? CauseIp2 : 0) | (_int1 ? CauseIp3 : 0);
        _config = 0x0000_0440; // 16 KB instruction cache, 8 KB data cache
        _status = 0x7040_0004; // CU0–2, BEV and ERL, as after reset
        _randomBase = Cycles;
        _countOffset = -Cycles;
        ScheduleCompare();
        StatusChanged();
        RebuildPageTable();
    }

    private void StatusChanged()
    {
        var ksu = (_status >> 3) & 3;
        var exception = (_status & (StatusExl | StatusErl)) != 0;
        _kernelMode = exception || ksu == 0;
        _supervisorMode = !exception && ksu == 1;
        InvalidateFetch();
        UpdateInterruptPending();
    }

    private bool Cop0Usable => _kernelMode || (_status & StatusCu0) != 0;

    /// <summary>
    /// Interrupts are taken when IE and EIE are set and EXL and ERL clear, and an enabled line (IM2/IM3/IM7) is pending
    /// (ps2tek: "EE COP0 Exception Handling").
    /// </summary>
    private void UpdateInterruptPending() =>
        _interruptPending = (_status & (StatusIe | StatusEie | StatusExl | StatusErl)) == (StatusIe | StatusEie)
            && (_status & _cause & (CauseIp2 | CauseIp3 | CauseIp7)) != 0;

    // ---- The timer ----

    private void ScheduleCompare()
    {
        var delta = _compare - Count;
        _compareCycle = Cycles + (delta == 0 ? 1L << 32 : delta);
        if (_compareCycle < _sliceEnd)
        {
            _sliceEnd = _compareCycle; // a running slice stops at the new compare
        }
    }

    private void CompareReached()
    {
        _cause |= CauseIp7;
        UpdateInterruptPending();
        ScheduleCompare();
    }

    // ---- Exceptions ----

    private void TakeInterrupt() => RaiseException(R5900Exception.Interrupt);

    /// <summary>
    /// Takes a level 1 exception: sets ExcCode, saves the PC in EPC (the branch's, with BD, for an instruction in a
    /// delay slot) unless EXL is already set, sets EXL and jumps to the vector — the refill vector only for a refill
    /// with EXL clear, the interrupt vector for interrupts, the common one otherwise; at $BFC00200 instead of
    /// $80000000 while BEV is set (ps2tek: "EE COP0 Exception Handling").
    /// </summary>
    internal void RaiseException(R5900Exception code, int coprocessor = 0, bool refill = false)
    {
        if (ExceptionHook is { } hook && hook(code))
        {
            return;
        }

        _cause = (_cause & ~0x3000_007Cu) | ((uint)code << 2) | ((uint)coprocessor << 28);
        uint offset = code == R5900Exception.Interrupt ? 0x200u : 0x180u;
        if ((_status & StatusExl) == 0)
        {
            _epc = _inDelaySlot ? _currentPc - 4 : _currentPc;
            _cause = _inDelaySlot ? _cause | CauseBd : _cause & ~CauseBd;
            if (refill)
            {
                offset = 0x000;
            }
        }

        _status |= StatusExl;
        StatusChanged();
        var vectorBase = (_status & StatusBev) != 0 ? 0xBFC0_0200u : 0x8000_0000u;
        Jump(vectorBase + offset);
    }

    private void AddressError(uint address, R5900Exception code)
    {
        _badVAddr = address;
        RaiseException(code);
    }

    private void TlbException(uint address, Access access, bool refill, bool modified)
    {
        _badVAddr = address;
        _context = (_context & 0xFF80_0000) | ((address >> 9) & 0x007F_FFF0);
        _entryHi = (address & 0xFFFF_E000) | (_entryHi & 0xFF);
        var code = modified ? R5900Exception.TlbModified
            : access == Access.Store ? R5900Exception.TlbStore : R5900Exception.TlbLoad;
        RaiseException(code, refill: refill);
    }

    // ---- The TLB ----

    /// <summary>
    /// Raises the exception for an access the page table has no valid mapping for: TLB refill when no entry matches,
    /// TLB invalid when the page's V bit is clear, TLB modified for a store to a page whose D bit is clear.
    /// </summary>
    private void TlbFault(uint address, Access access)
    {
        var asid = _entryHi & 0xFF;
        for (var i = 0; i < Tlb.Length; i++)
        {
            ref var entry = ref Tlb[i];
            var mask = entry.CompareMask;
            if ((address & mask) != (entry.EntryHi & mask) || (!entry.Global && (entry.EntryHi & 0xFF) != asid))
            {
                continue;
            }

            var lo = (address & entry.OddBit) != 0 ? entry.EntryLo1 : entry.EntryLo0;
            var modified = access == Access.Store && (lo & 2) != 0;
            TlbException(address, access, refill: false, modified);
            return;
        }

        TlbException(address, access, refill: true, modified: false);
    }

    // The page-table ranges the last rebuild filled (two per entry at most), so the next one clears only those.
    private readonly uint[] _mappedFirstPage = new uint[TlbEntries * 2];
    private readonly uint[] _mappedPageCount = new uint[TlbEntries * 2];
    private int _mappedRanges;

    /// <summary>Rebuilds the flattened page table from the TLB (after a TLB write or an ASID change).</summary>
    private void RebuildPageTable()
    {
        for (var i = 0; i < _mappedRanges; i++)
        {
            Array.Clear(_pageTable, (int)_mappedFirstPage[i], (int)_mappedPageCount[i]);
        }

        _mappedRanges = 0;
        InvalidateFetch();
        var asid = _entryHi & 0xFF;

        // Lower entries win where entries overlap, as TLBP's search order does.
        for (var i = Tlb.Length - 1; i >= 0; i--)
        {
            ref var entry = ref Tlb[i];
            if (!entry.Global && (entry.EntryHi & 0xFF) != asid)
            {
                continue;
            }

            var half = entry.OddBit;
            var evenBase = entry.EntryHi & entry.CompareMask;
            if (entry.Scratchpad)
            {
                // The S bit maps the scratchpad over the even page, repeating every 16 KB (ps2tek: "EE COP0 Memory Management"),
                // and always the whole 16 KB: the kernel's entry has a 4 KB page mask, yet programs reach $70003FFF
                // (ps2autotests memory/ee/default, on hardware).
                half = Math.Max(half, (uint)ScratchpadSize);
                RecordRange(evenBase, half);
                for (uint offset = 0; offset < half; offset += 0x1000)
                {
                    _pageTable[(evenBase + offset) >> 12] = (offset & 0x3000) | PageValid | PageDirty | PageScratch;
                }

                continue;
            }

            MapHalf(evenBase, half, entry.EntryLo0);
            MapHalf(evenBase + half, half, entry.EntryLo1);
        }
    }

    private void RecordRange(uint virtualBase, uint size)
    {
        _mappedFirstPage[_mappedRanges] = virtualBase >> 12;
        _mappedPageCount[_mappedRanges] = Math.Min(size >> 12, (uint)_pageTable.Length - (virtualBase >> 12));
        _mappedRanges++;
    }

    private void MapHalf(uint virtualBase, uint size, uint lo)
    {
        if ((lo & 2) == 0)
        {
            return;
        }

        RecordRange(virtualBase, size);
        var physicalBase = ((lo >> 6) & 0xF_FFFF) << 12 & ~(size - 1);
        var flags = PageValid | ((lo & 4) != 0 ? PageDirty : 0);
        for (uint offset = 0; offset < size; offset += 0x1000)
        {
            _pageTable[(virtualBase + offset) >> 12] = ((physicalBase + offset) & 0xFFFF_F000) | flags;
        }
    }

    private void TlbRead()
    {
        ref var entry = ref Tlb[_index % TlbEntries];
        _pageMask = entry.PageMask;
        _entryHi = entry.EntryHi & ~entry.PageMask;
        var global = entry.Global ? 1u : 0u;
        _entryLo0 = (entry.EntryLo0 & ~1u) | global;
        _entryLo1 = (entry.EntryLo1 & ~1u) | global;
    }

    private void TlbWrite(uint index)
    {
        WriteTlbEntry((int)(index % TlbEntries), _pageMask, _entryHi, _entryLo0, _entryLo1);
    }

    /// <summary>
    /// Writes TLB entry <paramref name="index"/> as TLBWI would with these COP0 values (the kernel's initial mappings,
    /// tests). The entry is global when both EntryLo values have G set.
    /// </summary>
    public void WriteTlbEntry(int index, uint pageMask, uint entryHi, uint entryLo0, uint entryLo1)
    {
        ref var entry = ref Tlb[index];
        entry.PageMask = pageMask & 0x01FF_E000;
        entry.EntryHi = entryHi & (0xFFFF_E0FF & ~entry.PageMask);
        entry.EntryLo0 = entryLo0 & 0x83FF_FFFE;
        entry.EntryLo1 = entryLo1 & 0x03FF_FFFE;
        entry.Global = (entryLo0 & entryLo1 & 1) != 0;
        RebuildPageTable();
    }

    private void TlbProbe()
    {
        _index = 0x8000_0000;
        var asid = _entryHi & 0xFF;
        for (var i = 0; i < Tlb.Length; i++)
        {
            ref var entry = ref Tlb[i];
            var mask = entry.CompareMask;
            if ((_entryHi & mask) == (entry.EntryHi & mask) && (entry.Global || (entry.EntryHi & 0xFF) == asid))
            {
                _index = (uint)i;
                return;
            }
        }
    }

    /// <summary>Random counts down from 47 to Wired, once per instruction.</summary>
    private uint Random
    {
        get
        {
            var range = TlbEntries - (long)(_wired & 0x3F);
            var steps = Cycles - _randomBase;
            if (range <= 0)
            {
                return (uint)((47 - steps) & 0x3F);
            }

            return (uint)(47 - (steps % range));
        }
    }

    // ---- COP0 instructions ----

    private void ExecuteCop0(uint op, int rs, int rt)
    {
        if (!Cop0Usable)
        {
            RaiseException(R5900Exception.CoprocessorUnusable, 0);
            return;
        }

        var rd = (int)(op >> 11) & 31;
        switch (rs)
        {
            case 0x00: // MFC0 (and MFBPC…, MFPS/MFPC)
                SetGpr64(rt, (ulong)(long)(int)ReadCop0Sub(rd, op));
                break;
            case 0x04: // MTC0 (and MTBPC…, MTPS/MTPC)
                WriteCop0Sub(rd, op, (uint)_gpr[rt * 2]);
                break;
            case 0x08: // BC0F, BC0T, BC0FL, BC0TL
                switch (rt)
                {
                    case 0:
                        Branch(!Cpcond0, BranchTarget(op));
                        break;
                    case 1:
                        Branch(Cpcond0, BranchTarget(op));
                        break;
                    case 2:
                        BranchLikely(!Cpcond0, BranchTarget(op));
                        break;
                    case 3:
                        BranchLikely(Cpcond0, BranchTarget(op));
                        break;
                    default:
                        RaiseException(R5900Exception.ReservedInstruction);
                        break;
                }

                break;
            case 0x10: // C0: the TLB, ERET, EI/DI
                switch (op & 0x3F)
                {
                    case 0x01:
                        TlbRead();
                        break;
                    case 0x02:
                        TlbWrite(_index & 0x3F);
                        break;
                    case 0x06:
                        TlbWrite(Random);
                        break;
                    case 0x08:
                        TlbProbe();
                        break;
                    case 0x18:
                        Eret();
                        break;
                    case 0x38: // EI
                        if (_kernelMode || (_status & StatusEdi) != 0)
                        {
                            _status |= StatusEie;
                            UpdateInterruptPending();
                        }

                        break;
                    case 0x39: // DI
                        if (_kernelMode || (_status & StatusEdi) != 0)
                        {
                            _status &= ~StatusEie;
                            UpdateInterruptPending();
                        }

                        break;
                    default:
                        RaiseException(R5900Exception.ReservedInstruction);
                        break;
                }

                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    /// <summary>ERET: back to ErrorEPC (clearing ERL) or EPC (clearing EXL). It has no delay slot.</summary>
    private void Eret()
    {
        if ((_status & StatusErl) != 0)
        {
            _status &= ~StatusErl;
            StatusChanged();
            Jump(_errorEpc);
        }
        else
        {
            _status &= ~StatusExl;
            StatusChanged();
            Jump(_epc);
        }
    }

    /// <summary>MFC0 with the debug (24) and performance (25) registers' sub-register selected by the low bits.</summary>
    private uint ReadCop0Sub(int register, uint op) => register switch
    {
        24 => _debug[op & 7],
        25 => (op & 1) == 0 ? _pccr : (op & 2) == 0 ? PerfCounter(0) : PerfCounter(1),
        _ => ReadCop0(register),
    };

    private void WriteCop0Sub(int register, uint op, uint value)
    {
        switch (register)
        {
            case 24:
                _debug[op & 7] = value;
                break;
            case 25:
                if ((op & 1) == 0)
                {
                    if ((op & 0x3E) != 0)
                    {
                        break; // MTPS to any event specifier but 0 does nothing (ps2autotests cpu/ee_cop0/performance)
                    }

                    _pcr0 = PerfCounter(0);
                    _pcr1 = PerfCounter(1);
                    _pcr0Base = _pcr1Base = Cycles;
                    _pccr = value & 0x800F_FBFE;
                }
                else if ((op & 2) == 0)
                {
                    _pcr0 = value;
                    _pcr0Base = Cycles;
                }
                else
                {
                    _pcr1 = value;
                    _pcr1Base = Cycles;
                }

                break;
            default:
                WriteCop0(register, value);
                break;
        }
    }

    /// <summary>
    /// PCR0/PCR1: while PCCR's CTE (bit 31) is set and the counter has an event (1–15) and a mode bit (EXL, K, S, U)
    /// selected, it counts EE cycles. Approximate: the events aren't told apart and the modes aren't checked against
    /// the CPU's, and the counters' overflow interrupt (a level 2 exception) isn't raised.
    /// </summary>
    private uint PerfCounter(int counter)
    {
        var shift = counter == 0 ? 1 : 11;
        var events = (_pccr >> (shift + 4)) & 0x1F;
        var modes = (_pccr >> shift) & 0xF;
        var counting = (_pccr & 0x8000_0000) != 0 && events is > 0 and < 16 && modes != 0;
        if (counter == 0)
        {
            return counting ? _pcr0 + (uint)(Cycles - _pcr0Base) : _pcr0;
        }

        return counting ? _pcr1 + (uint)(Cycles - _pcr1Base) : _pcr1;
    }

    /// <summary>Reads a COP0 register as MFC0 would (the kernel, tests).</summary>
    public uint ReadCop0(int register) => register switch
    {
        0 => _index,
        1 => Random,
        2 => _entryLo0,
        3 => _entryLo1,
        4 => _context,
        5 => _pageMask,
        6 => _wired,
        8 => _badVAddr,
        9 => Count,
        10 => _entryHi,
        11 => _compare,
        12 => _status,
        13 => _cause,
        14 => _epc,
        15 => ProcessorId,
        16 => _config,
        23 => _badPAddr,
        24 => _debug[0],
        25 => _pccr,
        28 => _tagLo,
        29 => _tagHi,
        30 => _errorEpc,
        _ => 0,
    };

    /// <summary>Writes a COP0 register as MTC0 would (the kernel, tests).</summary>
    public void WriteCop0(int register, uint value)
    {
        switch (register)
        {
            case 0:
                _index = (_index & 0x8000_0000) | (value & 0x3F);
                break;
            case 2:
                _entryLo0 = value & 0x83FF_FFFF;
                break;
            case 3:
                _entryLo1 = value & 0x03FF_FFFF;
                break;
            case 4:
                _context = (value & 0xFF80_0000) | (_context & 0x007F_FFF0);
                break;
            case 5:
                _pageMask = value & 0x01FF_E000;
                break;
            case 6:
                _wired = value & 0x3F;
                _randomBase = Cycles;
                break;
            case 8:
                _badVAddr = value;
                break;
            case 9:
                Count = value;
                break;
            case 10:
            {
                var oldAsid = _entryHi & 0xFF;
                _entryHi = value & 0xFFFF_E0FF;
                if ((_entryHi & 0xFF) != oldAsid)
                {
                    RebuildPageTable();
                }

                break;
            }

            case 11:
                _compare = value;
                _cause &= ~CauseIp7;
                ScheduleCompare();
                UpdateInterruptPending();
                break;
            case 12:
                _status = (_status & ~StatusWriteMask) | (value & StatusWriteMask);
                StatusChanged();
                break;
            case 13:
                break; // the EE has no software interrupt bits; the IP bits follow their lines
            case 14:
                _epc = value;
                break;
            case 16:
                _config = (_config & ~0x0007_3007u) | (value & 0x0007_3007);
                break;
            case 23:
                _badPAddr = value;
                break;
            case 24:
                _debug[0] = value;
                break;
            case 28:
                _tagLo = value;
                break;
            case 29:
                _tagHi = value;
                break;
            case 30:
                _errorEpc = value;
                break;
        }
    }

    private void SaveCop0(StateWriter writer)
    {
        foreach (var entry in Tlb)
        {
            writer.WriteUInt32(entry.PageMask);
            writer.WriteUInt32(entry.EntryHi);
            writer.WriteUInt32(entry.EntryLo0);
            writer.WriteUInt32(entry.EntryLo1);
            writer.WriteBool(entry.Global);
        }

        writer.WriteUInt32(_index);
        writer.WriteUInt32(_entryLo0);
        writer.WriteUInt32(_entryLo1);
        writer.WriteUInt32(_context);
        writer.WriteUInt32(_pageMask);
        writer.WriteUInt32(_wired);
        writer.WriteUInt32(_badVAddr);
        writer.WriteInt64(_countOffset);
        writer.WriteUInt32(_entryHi);
        writer.WriteUInt32(_compare);
        writer.WriteUInt32(_status);
        writer.WriteUInt32(_cause);
        writer.WriteUInt32(_epc);
        writer.WriteUInt32(_config);
        writer.WriteUInt32(_badPAddr);
        writer.WriteUInt32(_tagLo);
        writer.WriteUInt32(_tagHi);
        writer.WriteUInt32(_errorEpc);
        foreach (var value in _debug)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteUInt32(_pccr);
        writer.WriteUInt32(_pcr0);
        writer.WriteUInt32(_pcr1);
        writer.WriteInt64(_pcr0Base);
        writer.WriteInt64(_pcr1Base);
        writer.WriteInt64(_randomBase);
    }

    private void LoadCop0(StateReader reader)
    {
        for (var i = 0; i < Tlb.Length; i++)
        {
            Tlb[i] = new R5900TlbEntry
            {
                PageMask = reader.ReadUInt32(),
                EntryHi = reader.ReadUInt32(),
                EntryLo0 = reader.ReadUInt32(),
                EntryLo1 = reader.ReadUInt32(),
                Global = reader.ReadBool(),
            };
        }

        _index = reader.ReadUInt32();
        _entryLo0 = reader.ReadUInt32();
        _entryLo1 = reader.ReadUInt32();
        _context = reader.ReadUInt32();
        _pageMask = reader.ReadUInt32();
        _wired = reader.ReadUInt32();
        _badVAddr = reader.ReadUInt32();
        _countOffset = reader.ReadInt64();
        _entryHi = reader.ReadUInt32();
        _compare = reader.ReadUInt32();
        _status = reader.ReadUInt32();
        _cause = reader.ReadUInt32();
        _epc = reader.ReadUInt32();
        _config = reader.ReadUInt32();
        _badPAddr = reader.ReadUInt32();
        _tagLo = reader.ReadUInt32();
        _tagHi = reader.ReadUInt32();
        _errorEpc = reader.ReadUInt32();
        for (var i = 0; i < _debug.Length; i++)
        {
            _debug[i] = reader.ReadUInt32();
        }

        _pccr = reader.ReadUInt32();
        _pcr0 = reader.ReadUInt32();
        _pcr1 = reader.ReadUInt32();
        _pcr0Base = reader.ReadInt64();
        _pcr1Base = reader.ReadInt64();
        _randomBase = reader.ReadInt64();
        ScheduleCompare();
        StatusChanged();
        RebuildPageTable();
    }
}
