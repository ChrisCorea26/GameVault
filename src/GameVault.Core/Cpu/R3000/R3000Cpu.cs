using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R3000;

/// <summary>
/// The R3000A's bus, in physical addresses (the CPU strips the KSEG0/KSEG1 segment bits). The bus charges each
/// access's wait states to <see cref="R3000Cpu.Cycles"/>.
/// </summary>
public interface IR3000Bus
{
    uint Read32(uint address);

    ushort Read16(uint address);

    byte Read8(uint address);

    void Write32(uint address, uint value);

    /// <summary>A halfword store. <paramref name="value"/> is the CPU register's whole value: memory keeps the low 16 bits,
    /// but 32-bit device registers receive all of it (ps1-tests cpu/io-access-bitwidth).</summary>
    void Write16(uint address, uint value);

    /// <summary>A byte store, with the CPU register's whole value as for <see cref="Write16"/>.</summary>
    void Write8(uint address, uint value);

    /// <summary>An instruction fetch that misses (or bypasses) the instruction cache.</summary>
    uint Fetch(uint address);
}

/// <summary>The exception codes in CAUSE (psx-spx: "COP0 - Exception Handling").</summary>
public enum R3000Exception
{
    Interrupt = 0,
    AddressErrorLoad = 4,
    AddressErrorStore = 5,
    BusErrorFetch = 6,
    BusErrorData = 7,
    Syscall = 8,
    Breakpoint = 9,
    ReservedInstruction = 10,
    CoprocessorUnusable = 11,
    Overflow = 12,
}

/// <summary>
/// The PlayStation's MIPS R3000A (SPEC §7.11, Phase 15): the MIPS I instruction set with the branch and load delay
/// slots, COP0 (status, cause, exceptions, cache isolation), the background multiplier/divider and the 4 KB
/// instruction cache, with the GTE as coprocessor 2. One <see cref="Step"/> runs an instruction (or takes an
/// interrupt) and charges one cycle; the bus charges memory wait states.
/// </summary>
/// <remarks>
/// References: psx-spx ("CPU Specifications", "COP0", "CPU Arithmetic" for MULT/DIV timing); the ps1-tests
/// <c>cpu/cop</c> hardware log for which coprocessor accesses raise Coprocessor Unusable.
/// </remarks>
public sealed partial class R3000Cpu : IStateful
{
    public const uint ResetVector = 0xBFC00000;

    /// <summary>COP0 register 15: the R3000A's processor id on the PlayStation.</summary>
    public const uint ProcessorId = 0x00000002;

    // SR bits.
    public const uint SrIsolateCache = 1 << 16;
    public const uint SrBev = 1 << 22;
    public const uint SrCu0 = 1 << 28;
    public const uint SrCu2 = 1 << 30;

    private static readonly StateTag Tag = StateTag.From("R3K ");

    /// <summary>Masks that turn a virtual address into a physical one, by its top three bits (KUSEG, KSEG0, KSEG1, KSEG2).</summary>
    private static readonly uint[] SegmentMask =
    [
        0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, // KUSEG
        0x7FFFFFFF, // KSEG0
        0x1FFFFFFF, // KSEG1
        0xFFFFFFFF, 0xFFFFFFFF, // KSEG2
    ];

    private readonly IR3000Bus _bus;
    private readonly uint[] _gpr = new uint[32];

    // The instruction cache: 256 lines of four words, each tagged with its line's address and a valid bit per word.
    private readonly uint[] _icacheTags = new uint[256];
    private readonly byte[] _icacheValid = new byte[256];
    private readonly uint[] _icacheData = new uint[1024];

    private uint _pc;
    private uint _nextPc;

    /// <summary>The address of the instruction being executed.</summary>
    private uint _currentPc;

    // Set by a branch or jump, so the next instruction knows it is in a delay slot.
    private bool _branch;
    private bool _branchTaken;
    private bool _inDelaySlot;
    private bool _delaySlotTaken;

    // The load delay: a load's value reaches its register after the next instruction.
    private int _loadReg;
    private uint _loadValue;

    // The load that lands at the end of this instruction (cancelled when the instruction writes the same register).
    private int _landingReg;
    private uint _landingValue;

    private long _hiLoReady;

    public R3000Cpu(IR3000Bus bus)
    {
        _bus = bus;
        Gte = new Gte();
    }

    /// <summary>CPU cycles since power-on. The bus adds wait states here.</summary>
    public long Cycles;

    public Gte Gte { get; }

    public uint Pc => _pc;

    public uint Hi { get; set; }

    public uint Lo { get; set; }

    public uint Sr { get; set; }

    public uint Cause { get; set; }

    public uint Epc { get; private set; }

    public uint BadVaddr { get; private set; }

    /// <summary>The hardware interrupt line (CAUSE bit 10), driven by the interrupt controller.</summary>
    public bool InterruptLine { get; set; }

    /// <summary>The cache control register at $FFFE0130 (bit 11 enables the instruction cache).</summary>
    public uint CacheControl { get; set; }

    /// <summary>Called with the exception code whenever an exception is taken (tests and the debugger).</summary>
    public Action<R3000Exception>? ExceptionHook { get; set; }

    public uint GetRegister(int index) => _gpr[index];

    public void SetRegister(int index, uint value)
    {
        if (index != 0)
        {
            _gpr[index] = value;
        }
    }

    /// <summary>Starts executing at <paramref name="address"/> (PS-EXE loading), with no delay slot pending.</summary>
    public void Jump(uint address)
    {
        _pc = address;
        _nextPc = address + 4;
        _branch = _inDelaySlot = false;
        _loadReg = 0;
    }

    public void Reset()
    {
        Array.Clear(_gpr);
        Array.Clear(_icacheTags);
        Array.Clear(_icacheValid);
        Hi = Lo = 0;
        Sr = SrBev;
        Cause = 0;
        Epc = BadVaddr = 0;
        CacheControl = 0;
        _hiLoReady = 0;
        Jump(ResetVector);
    }

    /// <summary>Runs one instruction, or takes a pending interrupt.</summary>
    public void Step()
    {
        _currentPc = _pc;
        _inDelaySlot = _branch;
        _delaySlotTaken = _branchTaken;
        _branch = _branchTaken = false;

        if ((_currentPc & 3) != 0)
        {
            BadVaddr = _currentPc;
            LandPendingLoad();
            Exception(R3000Exception.AddressErrorLoad);
            Cycles++;
            return;
        }

        var instruction = FetchInstruction(_currentPc);

        if (InterruptPending())
        {
            // A GTE command at the interrupted address has already run on the real CPU, so the BIOS's handler
            // skips it (psx-spx: "COP0 - Interrupt Handling"). Run it here to match.
            if ((instruction & 0xFE000000) == 0x4A000000 && (Sr & SrCu2) != 0)
            {
                Gte.Execute(instruction);
            }

            LandPendingLoad();
            Exception(R3000Exception.Interrupt);
            Cycles++;
            return;
        }

        _pc = _nextPc;
        _nextPc += 4;

        _landingReg = _loadReg;
        _landingValue = _loadValue;
        _loadReg = 0;

        Execute(instruction);

        if (_landingReg != 0)
        {
            _gpr[_landingReg] = _landingValue;
        }

        Cycles++;
    }

    private void LandPendingLoad()
    {
        if (_loadReg != 0)
        {
            _gpr[_loadReg] = _loadValue;
            _loadReg = 0;
        }
    }

    private bool InterruptPending()
    {
        var cause = (Cause & ~0x400u) | (InterruptLine ? 0x400u : 0);
        Cause = cause;
        return (Sr & 1) != 0 && (Sr & cause & 0xFF00) != 0;
    }

    // ---- Registers and the load delay ----

    /// <summary>Writes a register now (ALU results); a load landing on the same register is discarded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(int index, uint value)
    {
        if (index == 0)
        {
            return;
        }

        _gpr[index] = value;
        if (_landingReg == index)
        {
            _landingReg = 0;
        }
    }

    /// <summary>Schedules a load's value for after the next instruction; a landing load to the same register is replaced.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetDelayed(int index, uint value)
    {
        if (index == 0)
        {
            return;
        }

        if (_landingReg == index)
        {
            _landingReg = 0;
        }

        _loadReg = index;
        _loadValue = value;
    }

    // ---- Memory ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Physical(uint address) => address & SegmentMask[address >> 29];

    private uint FetchInstruction(uint address)
    {
        // KUSEG and KSEG0 are cached; KSEG1 is not.
        if ((CacheControl & 0x800) == 0 || address >= 0xA0000000)
        {
            return _bus.Fetch(Physical(address));
        }

        var physical = Physical(address);
        var line = (int)((physical >> 4) & 0xFF);
        var tag = physical & 0xFFFFFFF0;
        var word = (int)((physical >> 2) & 3);
        if (_icacheTags[line] != tag)
        {
            _icacheTags[line] = tag;
            _icacheValid[line] = 0;
        }

        if ((_icacheValid[line] & (1 << word)) == 0)
        {
            // A miss refills from this word to the end of the line.
            for (var i = word; i < 4; i++)
            {
                _icacheData[line * 4 + i] = _bus.Fetch(tag | (uint)(i * 4));
            }

            _icacheValid[line] |= (byte)(0xF << word & 0xF);
        }

        return _icacheData[line * 4 + word];
    }

    private uint Load32(uint address) => _bus.Read32(Physical(address));

    private void Store32(uint address, uint value)
    {
        if ((Sr & SrIsolateCache) != 0)
        {
            IsolatedStore(address);
            return;
        }

        if (address == 0xFFFE0130)
        {
            CacheControl = value;
            return;
        }

        _bus.Write32(Physical(address), value);
    }

    private void Store16(uint address, uint value)
    {
        if ((Sr & SrIsolateCache) != 0)
        {
            IsolatedStore(address);
            return;
        }

        _bus.Write16(Physical(address), value);
    }

    private void Store8(uint address, uint value)
    {
        if ((Sr & SrIsolateCache) != 0)
        {
            IsolatedStore(address);
            return;
        }

        _bus.Write8(Physical(address), value);
    }

    /// <summary>
    /// With the cache isolated, stores reach the instruction cache instead of memory. The BIOS uses this to flush
    /// the cache: each store invalidates its line (psx-spx: "Cache Control").
    /// </summary>
    private void IsolatedStore(uint address)
    {
        _icacheValid[(Physical(address) >> 4) & 0xFF] = 0;
    }

    // ---- Exceptions ----

    private void Exception(R3000Exception code, int coprocessor = 0)
    {
        var handler = (Sr & SrBev) != 0 ? 0xBFC00180u : 0x80000080u;

        // Push the interrupt-enable/kernel-mode stack (the low six bits of SR).
        Sr = (Sr & ~0x3Fu) | ((Sr << 2) & 0x3F);

        Cause = (Cause & ~0xB000007Cu) | ((uint)code << 2) | ((uint)coprocessor << 28);
        if (_inDelaySlot)
        {
            Epc = _currentPc - 4;
            Cause |= 0x80000000;
            if (_delaySlotTaken)
            {
                Cause |= 0x40000000;
            }
        }
        else
        {
            Epc = _currentPc;
            Cause &= ~0x40000000u;
        }

        _pc = handler;
        _nextPc = handler + 4;
        _branch = false;
        ExceptionHook?.Invoke(code);
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var r in _gpr)
        {
            writer.WriteUInt32(r);
        }

        writer.WriteUInt32(_pc);
        writer.WriteUInt32(_nextPc);
        writer.WriteUInt32(_currentPc);
        writer.WriteBool(_branch);
        writer.WriteBool(_branchTaken);
        writer.WriteBool(_inDelaySlot);
        writer.WriteBool(_delaySlotTaken);
        writer.WriteInt32(_loadReg);
        writer.WriteUInt32(_loadValue);
        writer.WriteUInt32(Hi);
        writer.WriteUInt32(Lo);
        writer.WriteUInt32(Sr);
        writer.WriteUInt32(Cause);
        writer.WriteUInt32(Epc);
        writer.WriteUInt32(BadVaddr);
        writer.WriteBool(InterruptLine);
        writer.WriteUInt32(CacheControl);
        writer.WriteInt64(Cycles);
        writer.WriteInt64(_hiLoReady);
        foreach (var t in _icacheTags)
        {
            writer.WriteUInt32(t);
        }

        writer.WriteBytes(_icacheValid);
        foreach (var d in _icacheData)
        {
            writer.WriteUInt32(d);
        }

        Gte.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 32; i++)
        {
            _gpr[i] = reader.ReadUInt32();
        }

        _pc = reader.ReadUInt32();
        _nextPc = reader.ReadUInt32();
        _currentPc = reader.ReadUInt32();
        _branch = reader.ReadBool();
        _branchTaken = reader.ReadBool();
        _inDelaySlot = reader.ReadBool();
        _delaySlotTaken = reader.ReadBool();
        _loadReg = reader.ReadInt32() & 31;
        _loadValue = reader.ReadUInt32();
        Hi = reader.ReadUInt32();
        Lo = reader.ReadUInt32();
        Sr = reader.ReadUInt32();
        Cause = reader.ReadUInt32();
        Epc = reader.ReadUInt32();
        BadVaddr = reader.ReadUInt32();
        InterruptLine = reader.ReadBool();
        CacheControl = reader.ReadUInt32();
        Cycles = reader.ReadInt64();
        _hiLoReady = reader.ReadInt64();
        for (var i = 0; i < _icacheTags.Length; i++)
        {
            _icacheTags[i] = reader.ReadUInt32();
        }

        reader.ReadBytes(_icacheValid);
        for (var i = 0; i < _icacheData.Length; i++)
        {
            _icacheData[i] = reader.ReadUInt32();
        }

        Gte.LoadState(reader);
        reader.EndChunk();
    }
}
