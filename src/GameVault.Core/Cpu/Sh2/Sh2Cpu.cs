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
/// SH7604's on-chip division unit, DMA controller, free-running timer, watchdog timer and its 4 KB cache used as RAM.
/// </summary>
/// <remarks>
/// One <see cref="Step"/> runs one instruction (a branch together with its delay slot) and returns its clocks, counted
/// as the SH-2 Programming Manual's issue cycles. Addresses: the top three bits pick the area (cached, cache-through,
/// purge, address array, data array at $C0000000, on-chip registers at $FFFFFE00); everything else goes to the bus
/// without them. The cache itself isn't modelled: memory is always read through. References: SH-1/SH-2 Programming
/// Manual; SH7604 Hardware Manual.
/// </remarks>
public sealed partial class Sh2Cpu : IStateful
{
    private const uint FlagT = 0x001, FlagS = 0x002, FlagQ = 0x100, FlagM = 0x200;
    private const uint SrMask = 0x3F3;

    private static readonly StateTag Tag = StateTag.From("SH2 ");

    private readonly ISh2Bus _bus;
    private readonly uint[] _r = new uint[16];
    private readonly byte[] _cacheRam = new byte[0x1000];
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

    /// <summary>A power-on or manual reset: PC and R15 from vectors 0 and 1, the mask at 15.</summary>
    public void Reset()
    {
        Array.Clear(_r);
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
        Reset();
    }

    // ---- Memory ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint address) => (address >> 29) switch
    {
        6 => _cacheRam[address & 0xFFF],
        7 => OnChip.Read8(address),
        _ => _bus.Read8(address & 0x1FFFFFFF),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint address)
    {
        address &= ~1u;
        return (address >> 29) switch
        {
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
            6 => ((uint)Read16(address) << 16) | Read16(address + 2),
            7 => OnChip.Read32(address),
            _ => _bus.Read32(address & 0x1FFFFFFF),
        };
    }

    public void Write8(uint address, byte value)
    {
        switch (address >> 29)
        {
            case 2: // cache purge
            case 3: // address array
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
            case 2:
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
            case 2:
            case 3:
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

        var opcode = Read16(Pc);
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
        var cycles = Execute(Read16(slot), delaySlot: true);
        Pc = target;
        return cycles;
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
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
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
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
        reader.EndChunk();
    }
}
