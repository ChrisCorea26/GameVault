using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.Sh2;

/// <summary>Memory as an SH-2 sees it: a 32-bit bus (the CPU keeps its cache area and on-chip registers itself).</summary>
public interface ISh2Bus
{
    byte Read8(uint address);

    ushort Read16(uint address);

    uint Read32(uint address);

    void Write8(uint address, byte value);

    void Write16(uint address, ushort value);

    void Write32(uint address, uint value);
}

/// <summary>
/// The Hitachi SH-2 (SH7604), the 32X's two CPUs (sega-cd-32x.md, milestone 2): every SH-1/SH-2 instruction with
/// delayed branches, the multiply-accumulate unit and step division, exceptions and interrupts through VBR, and the
/// SH7604's on-chip division unit, DMA controller, free-running timer, watchdog timer and its 4 KB cache.
/// </summary>
/// <remarks>
/// One <see cref="Step"/> runs one instruction (a branch together with its delay slot) and returns its clocks, counted
/// as the SH-2 Programming Manual's issue cycles. Addresses: the top three bits pick the area (cached, cache-through,
/// purge, address array, data array at $C0000000, on-chip registers at $FFFFFE00); everything else goes to the bus
/// without them. The cache (genesis-gaps.md §5) is four-way set associative, 64 sets of 16-byte lines, write-through,
/// with the hardware's six-bit LRU per set. Nothing outside the CPU (the other CPU, the DMA controller, the 68000)
/// updates it, so a line can go stale until the program purges it, as on the hardware. Timing is unchanged: a miss
/// costs no extra clocks. References: SH-1/SH-2 Programming Manual; SH7604 Hardware Manual, section 8 (Cache).
/// </remarks>
public sealed partial class Sh2Cpu : IStateful
{
    private const uint FlagT = 0x001, FlagS = 0x002, FlagQ = 0x100, FlagM = 0x200;
    private const uint SrMask = 0x3F3;

    private static readonly StateTag Tag = StateTag.From("SH2 ");

    private readonly ISh2Bus _bus;
    private readonly uint[] _r = new uint[16];
    private readonly byte[] _cacheRam = new byte[0x1000]; // the data array: way × $400 + set × 16 + byte
    private readonly uint[] _cacheTags = new uint[64 * 4]; // set × 4 + way: address bits 28–10, with CacheValid
    private readonly byte[] _cacheLru = new byte[64];
    private byte _ccr;
    private uint _sr = 0xF0;
    private bool _sleeping;

    public Sh2Cpu(ISh2Bus bus, string name = "SH-2")
    {
        _bus = bus;
        Name = name;
        OnChip = new Sh2OnChip(this);
    }

    public string Name { get; }

    public uint Pc { get; set; }

    public uint Pr { get; set; }

    public uint Gbr { get; set; }

    public uint Vbr { get; set; }

    public uint Mach { get; set; }

    public uint Macl { get; set; }

    public uint Sr
    {
        get => _sr;
        set => _sr = value & SrMask;
    }

    public bool T
    {
        get => (_sr & FlagT) != 0;
        set => _sr = value ? _sr | FlagT : _sr & ~FlagT;
    }

    private bool S => (_sr & FlagS) != 0;

    private bool Q
    {
        get => (_sr & FlagQ) != 0;
        set => _sr = value ? _sr | FlagQ : _sr & ~FlagQ;
    }

    private bool M
    {
        get => (_sr & FlagM) != 0;
        set => _sr = value ? _sr | FlagM : _sr & ~FlagM;
    }

    /// <summary>The interrupt mask, I3–I0.</summary>
    public int Mask => (int)((_sr >> 4) & 0x0F);

    /// <summary>The external interrupt request (the IRL pins' level, 0 for none); the system sets it.</summary>
    public int InterruptLevel { get; set; }

    /// <summary>The vector for an external interrupt of a level (the 32X's are auto-vectored: 64 + level / 2).</summary>
    public Func<int, int>? ExternalVector { get; set; }

    /// <summary>The CPU executed SLEEP and waits for an interrupt.</summary>
    public bool Sleeping => _sleeping;

    public long Cycles { get; private set; }

    /// <summary>The on-chip division unit, DMA controller and timers.</summary>
    public Sh2OnChip OnChip { get; }

    public uint GetR(int index) => _r[index];

    public void SetR(int index, uint value) => _r[index] = value;

    /// <summary>A power-on or manual reset: PC and R15 from vectors 0 and 1, the mask at 15, the cache off.</summary>
    public void Reset()
    {
        Array.Clear(_r);
        _ccr = 0; // the lines keep their contents; programs purge before turning the cache on
        _sr = 0xF0;
        Vbr = 0;
        Gbr = Pr = Mach = Macl = 0;
        _sleeping = false;
        OnChip.Reset();
        Pc = Read32(0);
        _r[15] = Read32(4);
    }

    public void PowerOn()
    {
        Array.Clear(_cacheRam);
        Array.Clear(_cacheTags);
        Array.Clear(_cacheLru);
        Reset();
    }

    // ---- Memory ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint address) => (address >> 29) switch
    {
        0 when CacheOn => CachedRead8(address, fetch: false),
        3 => (byte)(ReadAddressArray(address) >> (int)((3 - (address & 3)) * 8)),
        6 => _cacheRam[address & 0xFFF],
        7 => OnChip.Read8(address),
        _ => _bus.Read8(address & 0x1FFFFFFF),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint address) => Read16(address, fetch: false);

    /// <summary>An instruction fetch: as <see cref="Read16(uint)"/>, but CCR's ID (not OD) decides whether a miss fills a line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Fetch16(uint address) => Read16(address, fetch: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Read16(uint address, bool fetch)
    {
        address &= ~1u;
        return (address >> 29) switch
        {
            0 when CacheOn => CachedRead16(address, fetch),
            3 => (ushort)(ReadAddressArray(address) >> ((address & 2) == 0 ? 16 : 0)),
            6 => (ushort)((_cacheRam[address & 0xFFF] << 8) | _cacheRam[(address + 1) & 0xFFF]),
            7 => OnChip.Read16(address),
            _ => _bus.Read16(address & 0x1FFFFFFF),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint address)
    {
        address &= ~3u;
        return (address >> 29) switch
        {
            0 when CacheOn => CachedRead32(address),
            3 => ReadAddressArray(address),
            6 => ((uint)Read16(address) << 16) | Read16(address + 2),
            7 => OnChip.Read32(address),
            _ => _bus.Read32(address & 0x1FFFFFFF),
        };
    }

    public void Write8(uint address, byte value)
    {
        switch (address >> 29)
        {
            case 0:
                _bus.Write8(address, value);
                if (CacheOn && FindLine(address) is var hit and >= 0)
                {
                    _cacheRam[hit + (int)(address & 15)] = value;
                }

                return;
            case 2:
                Purge(address);
                return;
            case 3: // the address array takes long writes only
                return;
            case 6:
                _cacheRam[address & 0xFFF] = value;
                return;
            case 7:
                OnChip.Write8(address, value);
                return;
            default:
                _bus.Write8(address & 0x1FFFFFFF, value);
                return;
        }
    }

    public void Write16(uint address, ushort value)
    {
        address &= ~1u;
        switch (address >> 29)
        {
            case 0:
                _bus.Write16(address, value);
                if (CacheOn && FindLine(address) is var hit and >= 0)
                {
                    var at = hit + (int)(address & 15);
                    _cacheRam[at] = (byte)(value >> 8);
                    _cacheRam[at + 1] = (byte)value;
                }

                return;
            case 2:
                Purge(address);
                return;
            case 3:
                return;
            case 6:
                _cacheRam[address & 0xFFF] = (byte)(value >> 8);
                _cacheRam[(address + 1) & 0xFFF] = (byte)value;
                return;
            case 7:
                OnChip.Write16(address, value);
                return;
            default:
                _bus.Write16(address & 0x1FFFFFFF, value);
                return;
        }
    }

    public void Write32(uint address, uint value)
    {
        address &= ~3u;
        switch (address >> 29)
        {
            case 0:
                _bus.Write32(address, value);
                if (CacheOn && FindLine(address) is var hit and >= 0)
                {
                    var at = hit + (int)(address & 15);
                    _cacheRam[at] = (byte)(value >> 24);
                    _cacheRam[at + 1] = (byte)(value >> 16);
                    _cacheRam[at + 2] = (byte)(value >> 8);
                    _cacheRam[at + 3] = (byte)value;
                }

                return;
            case 2:
                Purge(address);
                return;
            case 3:
                WriteAddressArray(address, value);
                return;
            case 6:
                Write16(address, (ushort)(value >> 16));
                Write16(address + 2, (ushort)value);
                return;
            case 7:
                OnChip.Write32(address, value);
                return;
            default:
                _bus.Write32(address & 0x1FFFFFFF, value);
                return;
        }
    }

    // The DMA controller's side: it reaches memory itself, around the cache (SH7604 Hardware Manual §9).

    internal byte DmaRead8(uint address) => (address >> 29) switch
    {
        6 => _cacheRam[address & 0xFFF],
        7 => OnChip.Read8(address),
        _ => _bus.Read8(address & 0x1FFFFFFF),
    };

    internal ushort DmaRead16(uint address) => (address >> 29) switch
    {
        6 or 7 => Read16(address),
        _ => _bus.Read16(address & 0x1FFFFFFE),
    };

    internal uint DmaRead32(uint address) => (address >> 29) switch
    {
        6 or 7 => Read32(address),
        _ => _bus.Read32(address & 0x1FFFFFFC),
    };

    internal void DmaWrite8(uint address, byte value)
    {
        if ((address >> 29) is 6 or 7)
        {
            Write8(address, value);
        }
        else if ((address >> 29) is not (2 or 3))
        {
            _bus.Write8(address & 0x1FFFFFFF, value);
        }
    }

    internal void DmaWrite16(uint address, ushort value)
    {
        if ((address >> 29) is 6 or 7)
        {
            Write16(address, value);
        }
        else if ((address >> 29) is not (2 or 3))
        {
            _bus.Write16(address & 0x1FFFFFFE, value);
        }
    }

    internal void DmaWrite32(uint address, uint value)
    {
        if ((address >> 29) is 6 or 7)
        {
            Write32(address, value);
        }
        else if ((address >> 29) is not (2 or 3))
        {
            _bus.Write32(address & 0x1FFFFFFC, value);
        }
    }

    // ---- The cache ----

    private const byte CcrEnable = 0x01, CcrNoInstructionFill = 0x02, CcrNoDataFill = 0x04, CcrTwoWay = 0x08, CcrPurge = 0x10;
    private const uint CacheValid = 0x8000_0000;

    /// <summary>CCR ($FFFFFE92): W1–W0 (the way the address array shows), CP (purge: reads 0), TW, OD, ID and CE.</summary>
    public byte Ccr
    {
        get => _ccr;
        set
        {
            if ((value & CcrPurge) != 0)
            {
                Array.Clear(_cacheTags);
                Array.Clear(_cacheLru);
            }

            _ccr = (byte)(value & 0xCF);
        }
    }

    private bool CacheOn => (_ccr & CcrEnable) != 0;

    /// <summary>Two-way mode: ways 0 and 1 are 2 KB of RAM (the data array at $C0000000–$C00007FF); ways 2 and 3 cache.</summary>
    private int FirstWay => (_ccr & CcrTwoWay) != 0 ? 2 : 0;

    private static uint TagOf(uint address) => ((address >> 10) & 0x7FFFF) | CacheValid;

    /// <summary>The data array index of the line holding <paramref name="address"/>, or -1; a hit counts as a use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindLine(uint address)
    {
        var set = (int)(address >> 4) & 63;
        var tag = TagOf(address);
        for (var way = FirstWay; way < 4; way++)
        {
            if (_cacheTags[set * 4 + way] == tag)
            {
                Touch(set, way);
                return (way << 10) | (set << 4);
            }
        }

        return -1;
    }

    /// <summary>The line for a cached read, filled from the bus on a miss unless CCR says not to (then -1).</summary>
    private int LineFor(uint address, bool fetch)
    {
        var line = FindLine(address);
        if (line >= 0)
        {
            return line;
        }

        if ((_ccr & (fetch ? CcrNoInstructionFill : CcrNoDataFill)) != 0)
        {
            return -1;
        }

        var set = (int)(address >> 4) & 63;
        var way = Victim(set);
        var at = (way << 10) | (set << 4);
        var start = address & 0x1FFFFFF0;
        for (var i = 0; i < 16; i += 4)
        {
            var word = _bus.Read32(start + (uint)i);
            _cacheRam[at + i] = (byte)(word >> 24);
            _cacheRam[at + i + 1] = (byte)(word >> 16);
            _cacheRam[at + i + 2] = (byte)(word >> 8);
            _cacheRam[at + i + 3] = (byte)word;
        }

        _cacheTags[set * 4 + way] = TagOf(address);
        Touch(set, way);
        return at;
    }

    private byte CachedRead8(uint address, bool fetch)
    {
        var line = LineFor(address, fetch);
        return line < 0 ? _bus.Read8(address) : _cacheRam[line + (int)(address & 15)];
    }

    private ushort CachedRead16(uint address, bool fetch)
    {
        var line = LineFor(address, fetch);
        if (line < 0)
        {
            return _bus.Read16(address);
        }

        var at = line + (int)(address & 14);
        return (ushort)((_cacheRam[at] << 8) | _cacheRam[at + 1]);
    }

    private uint CachedRead32(uint address)
    {
        var line = LineFor(address, fetch: false);
        if (line < 0)
        {
            return _bus.Read32(address);
        }

        var at = line + (int)(address & 12);
        return (uint)((_cacheRam[at] << 24) | (_cacheRam[at + 1] << 16) | (_cacheRam[at + 2] << 8) | _cacheRam[at + 3]);
    }

    /// <summary>
    /// The LRU bits after a use of <paramref name="way"/> (SH7604 Hardware Manual, table 8.3): each bit orders one pair
    /// of ways (5: 0/1, 4: 0/2, 3: 0/3, 2: 1/2, 1: 1/3, 0: 2/3).
    /// </summary>
    private void Touch(int set, int way)
    {
        ref var lru = ref _cacheLru[set];
        lru = way switch
        {
            0 => (byte)(lru & 0x07),
            1 => (byte)((lru & ~0x06) | 0x20),
            2 => (byte)((lru & ~0x01) | 0x14),
            _ => (byte)(lru | 0x0B),
        };
    }

    /// <summary>The way a miss replaces (table 8.4): the least recently used, or of ways 2 and 3 in two-way mode.</summary>
    private int Victim(int set)
    {
        var lru = _cacheLru[set];
        if ((_ccr & CcrTwoWay) != 0)
        {
            return (lru & 0x01) != 0 ? 2 : 3;
        }

        if ((lru & 0x38) == 0x38)
        {
            return 0;
        }

        if ((lru & 0x26) == 0x06)
        {
            return 1;
        }

        if ((lru & 0x15) == 0x01)
        {
            return 2;
        }

        return 3; // also for combinations the hardware never makes itself (an address-array write can)
    }

    /// <summary>A write to the purge area ($40000000): the line holding the address is invalidated, in whichever way.</summary>
    private void Purge(uint address)
    {
        var set = (int)(address >> 4) & 63;
        var tag = TagOf(address);
        for (var way = 0; way < 4; way++)
        {
            if (_cacheTags[set * 4 + way] == tag)
            {
                _cacheTags[set * 4 + way] = 0;
            }
        }
    }

    /// <summary>The address array ($60000000): the set from bits 9–4, the way from CCR's W1–W0.</summary>
    private uint ReadAddressArray(uint address)
    {
        var set = (int)(address >> 4) & 63;
        var tag = _cacheTags[set * 4 + (_ccr >> 6)];
        return ((tag & 0x7FFFF) << 10) | ((uint)_cacheLru[set] << 4) | ((tag & CacheValid) != 0 ? 4u : 0);
    }

    /// <summary>An address-array write sets the way's tag (bits 28–10) and valid bit (bit 2), and the set's LRU (bits 9–4).</summary>
    private void WriteAddressArray(uint address, uint value)
    {
        var set = (int)(address >> 4) & 63;
        _cacheTags[set * 4 + (_ccr >> 6)] = ((value >> 10) & 0x7FFFF) | ((value & 4) != 0 ? CacheValid : 0);
        _cacheLru[set] = (byte)((value >> 4) & 0x3F);
    }

    // ---- Running ----

    /// <summary>Runs one instruction (or takes an interrupt). Returns its clocks.</summary>
    public int Step()
    {
        var external = InterruptLevel;
        var onChip = OnChip.InterruptLevel;
        if (Math.Max(external, onChip) > Mask)
        {
            int level, vector;
            if (onChip > external)
            {
                level = onChip;
                vector = OnChip.AcknowledgeInterrupt();
            }
            else
            {
                level = external;
                vector = ExternalVector?.Invoke(level) ?? 64 + (level >> 1);
            }

            _sleeping = false;
            Exception(vector, Pc);
            _sr = (_sr & ~0xF0u) | (uint)(level << 4);
            Cycles += 13;
            OnChip.Run(13);
            return 13;
        }

        if (_sleeping)
        {
            Cycles += 4;
            OnChip.Run(4);
            return 4;
        }

        var opcode = Fetch16(Pc);
        var cycles = Execute(opcode, delaySlot: false);
        Cycles += cycles;
        OnChip.Run(cycles);
        return cycles;
    }

    /// <summary>Pushes SR and <paramref name="returnPc"/>, then jumps through the vector table.</summary>
    private void Exception(int vector, uint returnPc)
    {
        _r[15] -= 4;
        Write32(_r[15], _sr);
        _r[15] -= 4;
        Write32(_r[15], returnPc);
        Pc = Read32(Vbr + (uint)(vector * 4));
    }

    /// <summary>A delayed branch: the next instruction runs before the jump.</summary>
    private int Delayed(uint target)
    {
        var slot = Pc + 2;
        Pc = slot;
        var cycles = Execute(Fetch16(slot), delaySlot: true);
        Pc = target;
        return cycles;
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        foreach (var r in _r)
        {
            writer.WriteUInt32(r);
        }

        writer.WriteUInt32(Pc);
        writer.WriteUInt32(Pr);
        writer.WriteUInt32(Gbr);
        writer.WriteUInt32(Vbr);
        writer.WriteUInt32(Mach);
        writer.WriteUInt32(Macl);
        writer.WriteUInt32(_sr);
        writer.WriteBool(_sleeping);
        writer.WriteInt64(Cycles);
        writer.WriteBytes(_cacheRam);
        OnChip.SaveState(writer);
        writer.WriteByte(_ccr);
        foreach (var tag in _cacheTags)
        {
            writer.WriteUInt32(tag);
        }

        writer.WriteBytes(_cacheLru);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        for (var i = 0; i < 16; i++)
        {
            _r[i] = reader.ReadUInt32();
        }

        Pc = reader.ReadUInt32();
        Pr = reader.ReadUInt32();
        Gbr = reader.ReadUInt32();
        Vbr = reader.ReadUInt32();
        Mach = reader.ReadUInt32();
        Macl = reader.ReadUInt32();
        _sr = reader.ReadUInt32() & SrMask;
        _sleeping = reader.ReadBool();
        Cycles = reader.ReadInt64();
        reader.ReadBytes(_cacheRam);
        OnChip.LoadState(reader);
        if (version >= 2)
        {
            _ccr = (byte)(reader.ReadByte() & 0xCF);
            for (var i = 0; i < _cacheTags.Length; i++)
            {
                _cacheTags[i] = reader.ReadUInt32() & (CacheValid | 0x7FFFF);
            }

            reader.ReadBytes(_cacheLru);
        }
        else
        {
            // Version 1 had no cache: it starts off and empty, which reads memory as version 1 did.
            _ccr = 0;
            Array.Clear(_cacheTags);
            Array.Clear(_cacheLru);
        }

        reader.EndChunk();
    }
}
