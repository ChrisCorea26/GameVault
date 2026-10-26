using GameVault.Core.State;

namespace GameVault.Core.Cpu.R4300;

/// <summary>A TLB entry: an even/odd pair of pages (VR4300 manual 5.4).</summary>
internal struct TlbEntry
{
    public uint PageMask;
    public ulong EntryHi;
    public uint EntryLo0;
    public uint EntryLo1;
    public bool Global;

    /// <summary>The EntryHi bits a virtual address must match: R and VPN2 above the page mask.</summary>
    public readonly ulong CompareMask => 0xC000_00FF_FFFF_E000 & ~(ulong)PageMask;

    /// <summary>The address bit that picks the odd page.</summary>
    public readonly ulong OddBit => ((PageMask >> 1) | 0xFFFu) + 1;
}

/// <summary>COP0: the system control registers, exceptions, the timer and the TLB.</summary>
public sealed partial class R4300Cpu
{
    internal readonly TlbEntry[] Tlb = new TlbEntry[32];

    private uint _index;
    private uint _entryLo0;
    private uint _entryLo1;
    private ulong _context;
    private uint _pageMask;
    private uint _wired;
    private ulong _badVAddr;
    private long _countOffset;
    private ulong _entryHi;
    private uint _compare;
    private uint _status;
    private uint _cause;
    private ulong _epc;
    private uint _config;
    private uint _llAddr;
    private uint _watchLo;
    private uint _watchHi;
    private ulong _xContext;
    private uint _tagLo;
    private uint _tagHi;
    private ulong _errorEpc;

    /// <summary>The value last written to any COP0 register, which the unused registers read back (n64-systemtest).</summary>
    private ulong _latch;

    /// <summary>When Random was last set to 31 (by a Wired write or reset).</summary>
    private long _randomBase;

    private long _compareCycle = long.MaxValue;
    private bool _interruptPending;

    // The last successful TLB translation (a 4 KB page), so loops over mapped memory skip the 32-entry search.
    private ulong _tlbCacheVirtual = ulong.MaxValue;
    private uint _tlbCachePhysical;
    private bool _tlbCacheDirty;

    public uint Status
    {
        get => _status;
        set
        {
            _status = value;
            Fpu.Fr = (value & StatusFr) != 0;
            _tlbCacheVirtual = ulong.MaxValue;
            UpdateInterruptPending();
        }
    }

    public uint Cause => _cause;

    public ulong Epc => _epc;

    public ulong ErrorEpc => _errorEpc;

    public ulong BadVAddr => _badVAddr;

    /// <summary>COP0 Count: ticks at half the CPU clock.</summary>
    public uint Count
    {
        get => (uint)((Cycles + _countOffset) >> 1);
        set
        {
            _countOffset = 2L * value - Cycles;
            ScheduleCompare();
        }
    }

    public uint Compare => _compare;

    private void ResetCop0()
    {
        Array.Clear(Tlb);
        _index = _entryLo0 = _entryLo1 = _pageMask = _wired = _compare = 0;
        _watchLo = _watchHi = _tagLo = _tagHi = 0;
        _cause = 0xB000_007C; // what the boot leaves, as PeterLemon's COP0Cause records it on hardware
        _llAddr = 0xFFFF_FFFF;
        _context = _entryHi = _xContext = _latch = 0;
        _errorEpc = _epc = _badVAddr = 0xFFFF_FFFF_FFFF_FFFF;
        _config = 0x7006_E463;
        _status = 0x3440_0004; // CU0, CU1, BEV and ERL, as after a cold reset
        Fpu.Fr = false;
        _randomBase = Cycles;
        _countOffset = -Cycles;
        _tlbCacheVirtual = ulong.MaxValue;
        ScheduleCompare();
        UpdateInterruptPending();
    }

    private bool KernelMode => (_status & (StatusExl | StatusErl)) != 0 || ((_status >> 3) & 3) == 0;

    private bool Cop0Usable => (_status & StatusCu0) != 0 || KernelMode;

    private void UpdateInterruptPending() =>
        _interruptPending = (_status & (StatusIe | StatusExl | StatusErl)) == StatusIe && (_status & _cause & 0xFF00) != 0;

    // ---- The timer ----

    private void ScheduleCompare()
    {
        var count = (Cycles + _countOffset) >> 1;
        var delta = (uint)(_compare - (uint)count);
        var ticks = delta == 0 ? 1L << 32 : delta;
        _compareCycle = (count + ticks) * 2 - _countOffset;
    }

    private void CompareReached()
    {
        _cause |= CauseIp7;
        UpdateInterruptPending();
        ScheduleCompare();
    }

    // ---- Exceptions ----

    /// <summary>
    /// Takes an exception: saves the PC in EPC (the branch's, for an instruction in a delay slot) unless EXL is
    /// already set, and jumps to the vector (VR4300 manual 6.4).
    /// </summary>
    internal void RaiseException(R4300Exception code, int coprocessor = 0, bool refill = false, bool wideRefill = false)
    {
        var offset = 0x180UL;
        if ((_status & StatusExl) == 0)
        {
            _epc = _inDelaySlot ? _currentPc - 4 : _currentPc;
            _cause = _inDelaySlot ? _cause | CauseBd : _cause & ~CauseBd;
            if (refill)
            {
                offset = wideRefill ? 0x080UL : 0x000UL;
            }
        }

        _status |= StatusExl;
        _cause = (_cause & ~0x3000_007Cu) | ((uint)code << 2) | ((uint)coprocessor << 28);
        var vectorBase = (_status & StatusBev) != 0 ? 0xFFFF_FFFF_BFC0_0200UL : 0xFFFF_FFFF_8000_0000UL;
        _tlbCacheVirtual = ulong.MaxValue;
        UpdateInterruptPending();
        Jump(vectorBase + offset);
    }

    private void AddressError(ulong address, R4300Exception code)
    {
        _badVAddr = address;
        RaiseException(code);
    }

    private void TlbException(ulong address, Access access, bool refill, bool wide, bool modified)
    {
        _badVAddr = address;
        var vpn2 = (address >> 13) & 0x7FF_FFFF;
        _context = (_context & ~0x7F_FFF0UL) | ((vpn2 & 0x7_FFFF) << 4);
        _xContext = (_xContext & ~0x1_FFFF_FFF0UL) | (vpn2 << 4) | (((address >> 62) & 3) << 31);
        _entryHi = (address & 0xC000_00FF_FFFF_E000) | (_entryHi & 0xFF);
        var code = modified ? R4300Exception.TlbModification
            : access == Access.Store ? R4300Exception.TlbStore : R4300Exception.TlbLoad;
        RaiseException(code, refill: refill, wideRefill: wide);
    }

    // ---- The TLB ----

    private bool TlbTranslate(ulong address, Access access, bool wide, out uint physical)
    {
        var page = address & ~0xFFFUL;
        if (page == _tlbCacheVirtual && (access != Access.Store || _tlbCacheDirty))
        {
            physical = _tlbCachePhysical | ((uint)address & 0xFFF);
            return true;
        }

        var asid = _entryHi & 0xFF;
        for (var i = 0; i < Tlb.Length; i++)
        {
            ref var entry = ref Tlb[i];
            var mask = entry.CompareMask;
            if ((address & mask) != (entry.EntryHi & mask) || (!entry.Global && (entry.EntryHi & 0xFF) != asid))
            {
                continue;
            }

            var oddBit = entry.OddBit;
            var lo = (address & oddBit) != 0 ? entry.EntryLo1 : entry.EntryLo0;
            if ((lo & 2) == 0)
            {
                TlbException(address, access, refill: false, wide, modified: false); // invalid
                physical = 0;
                return false;
            }

            if (access == Access.Store && (lo & 4) == 0)
            {
                TlbException(address, access, refill: false, wide, modified: true);
                physical = 0;
                return false;
            }

            var offsetMask = oddBit - 1;
            physical = (uint)((((ulong)(lo >> 6) & 0xF_FFFF) << 12) & ~offsetMask) | (uint)(address & offsetMask);
            _tlbCacheVirtual = page;
            _tlbCachePhysical = physical & ~0xFFFu;
            _tlbCacheDirty = (lo & 4) != 0;
            return true;
        }

        TlbException(address, access, refill: true, wide, modified: false);
        physical = 0;
        return false;
    }

    private void TlbRead()
    {
        ref var entry = ref Tlb[_index & 0x1F];
        _pageMask = entry.PageMask;
        _entryHi = entry.EntryHi & ~(ulong)entry.PageMask;
        var global = entry.Global ? 1u : 0u;
        _entryLo0 = (entry.EntryLo0 & ~1u) | global;
        _entryLo1 = (entry.EntryLo1 & ~1u) | global;
    }

    private void TlbWrite(int index)
    {
        ref var entry = ref Tlb[index & 0x1F];
        entry.PageMask = _pageMask;
        entry.EntryHi = _entryHi & (0xC000_00FF_FFFF_E0FF & ~(ulong)_pageMask);
        entry.EntryLo0 = _entryLo0 & 0x03FF_FFFE;
        entry.EntryLo1 = _entryLo1 & 0x03FF_FFFE;
        entry.Global = (_entryLo0 & _entryLo1 & 1) != 0;
        _tlbCacheVirtual = ulong.MaxValue;
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

    /// <summary>Random counts down from 31 to Wired, once per instruction.</summary>
    private uint Random
    {
        get
        {
            var range = 32 - (long)(_wired & 0x3F);
            var steps = Cycles - _randomBase;
            if (range <= 0)
            {
                return (uint)((31 - steps) & 0x3F);
            }

            return (uint)(31 - (steps % range));
        }
    }

    // ---- MTC0 / MFC0 ----

    /// <summary>Sets a COP0 register as MTC0 would (the simulated PIF boot, tests).</summary>
    public void WriteCop0Register(int register, ulong value) => WriteCop0(register, value);

    /// <summary>Reads a COP0 register as DMFC0 would (tests).</summary>
    public ulong ReadCop0Register(int register) => ReadCop0(register);

    private ulong ReadCop0(int register)
    {
        return register switch
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
            17 => _llAddr,
            18 => _watchLo,
            19 => _watchHi,
            20 => _xContext,
            26 => 0, // parity error
            27 => 0, // cache error
            28 => _tagLo,
            29 => _tagHi,
            30 => _errorEpc,
            _ => _latch,
        };
    }

    /// <summary>Whether a register holds 64 bits (DMFC0 reads them whole; the others are sign-extended).</summary>
    private static bool IsWideCop0(int register) => register is 4 or 8 or 10 or 14 or 20 or 30;

    private void WriteCop0(int register, ulong value)
    {
        _latch = value;
        var word = (uint)value;
        switch (register)
        {
            case 0:
                _index = (_index & 0x8000_0000) | (word & 0x3F);
                break;
            case 2:
                _entryLo0 = word & 0x3FFF_FFFF;
                break;
            case 3:
                _entryLo1 = word & 0x3FFF_FFFF;
                break;
            case 4:
                _context = (value & ~0x7F_FFFFUL) | (_context & 0x7F_FFF0);
                break;
            case 5:
                _pageMask = word & 0x01FF_E000;
                break;
            case 6:
                _wired = word & 0x3F;
                _randomBase = Cycles;
                break;
            case 9:
                Count = word;
                break;
            case 10:
                _entryHi = value & 0xC000_00FF_FFFF_E0FF;
                _tlbCacheVirtual = ulong.MaxValue;
                break;
            case 11:
                _compare = word;
                _cause &= ~CauseIp7;
                ScheduleCompare();
                UpdateInterruptPending();
                break;
            case 12:
                Status = (_status & ~StatusWriteMask) | (word & StatusWriteMask);
                break;
            case 13:
                _cause = (_cause & ~0x300u) | (word & 0x300);
                UpdateInterruptPending();
                break;
            case 14:
                _epc = value;
                break;
            case 16:
                _config = (_config & ~0x0F00_800Fu) | (word & 0x0F00_800F);
                break;
            case 17:
                _llAddr = word;
                break;
            case 18:
                _watchLo = word & 0xFFFF_FFFB;
                break;
            case 19:
                _watchHi = word & 0xF;
                break;
            case 20:
                _xContext = (value & ~0x1_FFFF_FFFFUL) | (_xContext & 0x1_FFFF_FFF0);
                break;
            case 28:
                _tagLo = word & 0x0FFF_FFC0;
                break;
            case 29:
                _tagHi = 0;
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
            writer.WriteUInt64(entry.EntryHi);
            writer.WriteUInt32(entry.EntryLo0);
            writer.WriteUInt32(entry.EntryLo1);
            writer.WriteBool(entry.Global);
        }

        writer.WriteUInt32(_index);
        writer.WriteUInt32(_entryLo0);
        writer.WriteUInt32(_entryLo1);
        writer.WriteUInt64(_context);
        writer.WriteUInt32(_pageMask);
        writer.WriteUInt32(_wired);
        writer.WriteUInt64(_badVAddr);
        writer.WriteInt64(_countOffset);
        writer.WriteUInt64(_entryHi);
        writer.WriteUInt32(_compare);
        writer.WriteUInt32(_status);
        writer.WriteUInt32(_cause);
        writer.WriteUInt64(_epc);
        writer.WriteUInt32(_config);
        writer.WriteUInt32(_llAddr);
        writer.WriteUInt32(_watchLo);
        writer.WriteUInt32(_watchHi);
        writer.WriteUInt64(_xContext);
        writer.WriteUInt32(_tagLo);
        writer.WriteUInt32(_tagHi);
        writer.WriteUInt64(_errorEpc);
        writer.WriteUInt64(_latch);
        writer.WriteInt64(_randomBase);
    }

    private void LoadCop0(StateReader reader)
    {
        for (var i = 0; i < Tlb.Length; i++)
        {
            Tlb[i] = new TlbEntry
            {
                PageMask = reader.ReadUInt32(),
                EntryHi = reader.ReadUInt64(),
                EntryLo0 = reader.ReadUInt32(),
                EntryLo1 = reader.ReadUInt32(),
                Global = reader.ReadBool(),
            };
        }

        _index = reader.ReadUInt32();
        _entryLo0 = reader.ReadUInt32();
        _entryLo1 = reader.ReadUInt32();
        _context = reader.ReadUInt64();
        _pageMask = reader.ReadUInt32();
        _wired = reader.ReadUInt32();
        _badVAddr = reader.ReadUInt64();
        _countOffset = reader.ReadInt64();
        _entryHi = reader.ReadUInt64();
        _compare = reader.ReadUInt32();
        _status = reader.ReadUInt32();
        _cause = reader.ReadUInt32();
        _epc = reader.ReadUInt64();
        _config = reader.ReadUInt32();
        _llAddr = reader.ReadUInt32();
        _watchLo = reader.ReadUInt32();
        _watchHi = reader.ReadUInt32();
        _xContext = reader.ReadUInt64();
        _tagLo = reader.ReadUInt32();
        _tagHi = reader.ReadUInt32();
        _errorEpc = reader.ReadUInt64();
        _latch = reader.ReadUInt64();
        _randomBase = reader.ReadInt64();
        _tlbCacheVirtual = ulong.MaxValue;
        ScheduleCompare();
        UpdateInterruptPending();
    }
}
