using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.Gekko;

/// <summary>Physical memory as the Gekko sees it (big-endian).</summary>
public interface IGekkoBus
{
    byte Read8(uint address);

    ushort Read16(uint address);

    uint Read32(uint address);

    void Write8(uint address, byte value);

    void Write16(uint address, ushort value);

    void Write32(uint address, uint value);

    /// <summary>The processor interface's interrupt output (an external exception when MSR[EE] allows).</summary>
    bool ExternalInterrupt { get; }
}

/// <summary>
/// The IBM Gekko (a PowerPC 750CL), the GameCube's CPU (phase-18.md, milestone 1): the integer, branch, condition
/// register, load/store, floating-point and paired-single instructions, the SPRs games use, exceptions with
/// <c>rfi</c>, the decrementer and time base, and BAT address translation.
/// </summary>
/// <remarks>
/// One <see cref="Step"/> runs one instruction and returns one cycle (the interpreter doesn't model the 750's pipeline;
/// the time base and decrementer step every twelve cycles, a quarter of the 162 MHz bus clock at 486 MHz). Floating
/// point follows the hardware's choices: single-precision results are doubles rounded to single, written to both
/// halves of the register when they're paired. References: IBM PowerPC 750CL User's Manual; the PowerPC Microprocessor
/// Family Programming Environments Manual (32-bit); YAGCD for the GameCube's choices.
/// </remarks>
public sealed partial class GekkoCpu : IStateful
{
    public const double Clock = 486_000_000;

    // MSR bits.
    private const uint MsrEe = 0x8000, MsrPr = 0x4000, MsrFp = 0x2000, MsrMe = 0x1000, MsrIp = 0x40, MsrIr = 0x20, MsrDr = 0x10;

    // Exception vectors.
    private const uint VectorExternal = 0x500, VectorProgram = 0x700, VectorFpUnavailable = 0x800,
        VectorDecrementer = 0x900, VectorSystemCall = 0xC00;

    private static readonly StateTag Tag = StateTag.From("GEKO");

    private readonly IGekkoBus _bus;
    private readonly uint[] _gpr = new uint[32];
    private readonly double[] _ps0 = new double[32];
    private readonly double[] _ps1 = new double[32];
    private readonly uint[] _sprg = new uint[4];
    private readonly uint[] _gqr = new uint[8];
    private readonly uint[] _ibatU = new uint[4], _ibatL = new uint[4], _dbatU = new uint[4], _dbatL = new uint[4];
    private readonly uint[] _sr = new uint[16];
    private readonly byte[] _lockedCache = new byte[0x4000];
    private int _timeDivider;

    public GekkoCpu(IGekkoBus bus) => _bus = bus;

    public uint Pc { get; set; }

    public uint Msr { get; set; }

    public uint Cr { get; set; }

    public uint Xer { get; set; }

    public uint Lr { get; set; }

    public uint Ctr { get; set; }

    public uint Srr0 { get; set; }

    public uint Srr1 { get; set; }

    public uint Fpscr { get; set; }

    public uint Dec { get; set; }

    public ulong TimeBase { get; set; }

    public uint Hid0 { get; set; }

    public uint Hid1 { get; set; }

    public uint Hid2 { get; set; }

    public uint Dar { get; private set; }

    public uint Dsisr { get; private set; }

    public uint Wpar { get; set; }

    public uint L2cr { get; set; }

    public long Cycles { get; private set; }

    public uint GetGpr(int index) => _gpr[index];

    public void SetGpr(int index, uint value) => _gpr[index] = value;

    public double GetPs0(int index) => _ps0[index];

    public double GetPs1(int index) => _ps1[index];

    public void SetFpr(int index, double ps0, double ps1)
    {
        _ps0[index] = ps0;
        _ps1[index] = ps1;
    }

    public uint GetGqr(int index) => _gqr[index];

    public void SetGqr(int index, uint value) => _gqr[index] = value;

    /// <summary>Sets a BAT pair (0–3) for instruction or data accesses.</summary>
    public void SetBat(bool data, int index, uint upper, uint lower)
    {
        (data ? _dbatU : _ibatU)[index] = upper;
        (data ? _dbatL : _ibatL)[index] = lower;
    }

    private bool Ca
    {
        get => (Xer & 0x20000000) != 0;
        set => Xer = value ? Xer | 0x20000000 : Xer & ~0x20000000u;
    }

    public void Reset()
    {
        Array.Clear(_gpr);
        Array.Clear(_ps0);
        Array.Clear(_ps1);
        Array.Clear(_sprg);
        Array.Clear(_gqr);
        Array.Clear(_ibatU);
        Array.Clear(_ibatL);
        Array.Clear(_dbatU);
        Array.Clear(_dbatL);
        Array.Clear(_sr);
        Msr = MsrIp;
        Pc = 0xFFF00100;
        Cr = Xer = Lr = Ctr = Srr0 = Srr1 = Fpscr = Dec = 0;
        TimeBase = 0;
        Hid0 = Hid1 = Hid2 = Wpar = L2cr = 0;
        Dar = Dsisr = 0;
        _timeDivider = 0;
        Cycles = 0;
    }

    // ---- Address translation ----

    /// <summary>
    /// An effective address to a physical one through the BATs when translation is on (MSR[IR] for fetches, MSR[DR]
    /// for data). Addresses no BAT covers keep their low 30 bits, as the OS's own page tables map them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Translate(uint address, bool data)
    {
        if ((Msr & (data ? MsrDr : MsrIr)) == 0)
        {
            return address;
        }

        var upper = data ? _dbatU : _ibatU;
        var lower = data ? _dbatL : _ibatL;
        for (var i = 0; i < 4; i++)
        {
            var u = upper[i];
            if ((u & 3) == 0)
            {
                continue; // neither supervisor nor user valid
            }

            var mask = ((u >> 2) & 0x7FF) << 17 | 0x1FFFF; // BL: the block's size
            if ((address & ~mask) == (u & 0xFFFE0000 & ~mask))
            {
                return (lower[i] & 0xFFFE0000 & ~mask) | (address & mask);
            }
        }

        return address & 0x3FFFFFFF;
    }

    private bool IsLockedCache(uint address) => (address & 0xFFFFC000) == 0xE0000000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read8(uint address) =>
        IsLockedCache(address) ? _lockedCache[address & 0x3FFF] : _bus.Read8(Translate(address, data: true));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Read16(uint address)
    {
        if (IsLockedCache(address))
        {
            return (ushort)((_lockedCache[address & 0x3FFF] << 8) | _lockedCache[(address + 1) & 0x3FFF]);
        }

        var physical = Translate(address, data: true);
        return (physical & 1) == 0 ? _bus.Read16(physical) : (ushort)((_bus.Read8(physical) << 8) | _bus.Read8(physical + 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(uint address)
    {
        if (IsLockedCache(address))
        {
            return ((uint)Read16(address) << 16) | Read16(address + 2);
        }

        var physical = Translate(address, data: true);
        return (physical & 3) == 0
            ? _bus.Read32(physical)
            : ((uint)_bus.Read8(physical) << 24) | ((uint)_bus.Read8(physical + 1) << 16) | ((uint)_bus.Read8(physical + 2) << 8) | _bus.Read8(physical + 3);
    }

    public ulong Read64(uint address) => ((ulong)Read32(address) << 32) | Read32(address + 4);

    public void Write8(uint address, byte value)
    {
        if (IsLockedCache(address))
        {
            _lockedCache[address & 0x3FFF] = value;
            return;
        }

        _bus.Write8(Translate(address, data: true), value);
    }

    public void Write16(uint address, ushort value)
    {
        if (IsLockedCache(address) || (address & 1) != 0)
        {
            Write8(address, (byte)(value >> 8));
            Write8(address + 1, (byte)value);
            return;
        }

        _bus.Write16(Translate(address, data: true), value);
    }

    public void Write32(uint address, uint value)
    {
        if (IsLockedCache(address) || (address & 3) != 0)
        {
            Write16(address, (ushort)(value >> 16));
            Write16(address + 2, (ushort)value);
            return;
        }

        _bus.Write32(Translate(address, data: true), value);
    }

    public void Write64(uint address, ulong value)
    {
        Write32(address, (uint)(value >> 32));
        Write32(address + 4, (uint)value);
    }

    private uint Fetch(uint address) =>
        IsLockedCache(address) ? ((uint)Read16(address) << 16) | Read16(address + 2) : _bus.Read32(Translate(address, data: false));

    // ---- Running ----

    /// <summary>Runs one instruction, or takes a pending exception. Returns its cycles.</summary>
    public int Step()
    {
        if ((Msr & MsrEe) != 0)
        {
            if (_bus.ExternalInterrupt)
            {
                Exception(VectorExternal, Pc);
                return 1;
            }

            if (_decrementerPending)
            {
                _decrementerPending = false;
                Exception(VectorDecrementer, Pc);
                return 1;
            }
        }

        var op = Fetch(Pc);
        Execute(op);
        Tick(1);
        return 1;
    }

    private bool _decrementerPending;

    private void Tick(int cycles)
    {
        Cycles += cycles;
        _timeDivider += cycles;
        while (_timeDivider >= 12)
        {
            _timeDivider -= 12;
            TimeBase++;
            var before = Dec;
            Dec--;
            if ((before & 0x80000000) == 0 && (Dec & 0x80000000) != 0)
            {
                _decrementerPending = true; // the decrementer passed zero
            }
        }
    }

    /// <summary>Takes an exception: SRR0 the address to return to, SRR1 the MSR, translation and interrupts off.</summary>
    private void Exception(uint vector, uint returnAddress, uint srr1Flags = 0)
    {
        Srr0 = returnAddress;
        Srr1 = (Msr & 0x87C0FFFF) | srr1Flags;
        Msr &= MsrMe | MsrIp;
        Pc = ((Msr & MsrIp) != 0 ? 0xFFF00000 : 0) | vector;
    }

    private void Program(uint reason)
    {
        Exception(VectorProgram, Pc, reason);
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var r in _gpr)
        {
            writer.WriteUInt32(r);
        }

        for (var i = 0; i < 32; i++)
        {
            writer.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(_ps0[i]));
            writer.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(_ps1[i]));
        }

        foreach (var array in (uint[][])[_sprg, _gqr, _ibatU, _ibatL, _dbatU, _dbatL, _sr])
        {
            foreach (var value in array)
            {
                writer.WriteUInt32(value);
            }
        }

        writer.WriteUInt32(Pc);
        writer.WriteUInt32(Msr);
        writer.WriteUInt32(Cr);
        writer.WriteUInt32(Xer);
        writer.WriteUInt32(Lr);
        writer.WriteUInt32(Ctr);
        writer.WriteUInt32(Srr0);
        writer.WriteUInt32(Srr1);
        writer.WriteUInt32(Fpscr);
        writer.WriteUInt32(Dec);
        writer.WriteUInt64(TimeBase);
        writer.WriteUInt32(Hid0);
        writer.WriteUInt32(Hid1);
        writer.WriteUInt32(Hid2);
        writer.WriteUInt32(Dar);
        writer.WriteUInt32(Dsisr);
        writer.WriteUInt32(Wpar);
        writer.WriteUInt32(L2cr);
        writer.WriteInt32(_timeDivider);
        writer.WriteBool(_decrementerPending);
        writer.WriteInt64(Cycles);
        writer.WriteBytes(_lockedCache);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 32; i++)
        {
            _gpr[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < 32; i++)
        {
            _ps0[i] = BitConverter.Int64BitsToDouble((long)reader.ReadUInt64());
            _ps1[i] = BitConverter.Int64BitsToDouble((long)reader.ReadUInt64());
        }

        foreach (var array in (uint[][])[_sprg, _gqr, _ibatU, _ibatL, _dbatU, _dbatL, _sr])
        {
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = reader.ReadUInt32();
            }
        }

        Pc = reader.ReadUInt32();
        Msr = reader.ReadUInt32();
        Cr = reader.ReadUInt32();
        Xer = reader.ReadUInt32();
        Lr = reader.ReadUInt32();
        Ctr = reader.ReadUInt32();
        Srr0 = reader.ReadUInt32();
        Srr1 = reader.ReadUInt32();
        Fpscr = reader.ReadUInt32();
        Dec = reader.ReadUInt32();
        TimeBase = reader.ReadUInt64();
        Hid0 = reader.ReadUInt32();
        Hid1 = reader.ReadUInt32();
        Hid2 = reader.ReadUInt32();
        Dar = reader.ReadUInt32();
        Dsisr = reader.ReadUInt32();
        Wpar = reader.ReadUInt32();
        L2cr = reader.ReadUInt32();
        _timeDivider = Math.Clamp(reader.ReadInt32(), 0, 11);
        _decrementerPending = reader.ReadBool();
        Cycles = reader.ReadInt64();
        reader.ReadBytes(_lockedCache);
        reader.EndChunk();
    }
}
