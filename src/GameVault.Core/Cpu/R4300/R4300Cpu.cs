using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R4300;

/// <summary>
/// The VR4300's bus, in physical addresses (the CPU translates virtual addresses itself). RDRAM is read and written
/// directly through <see cref="Rdram"/>; everything above it goes through the methods. Reads and writes of 8 and 16
/// bits get the address of the byte or halfword itself.
/// </summary>
public interface IR4300Bus
{
    /// <summary>RDRAM, big-endian. Physical addresses below its length go straight to it.</summary>
    byte[] Rdram { get; }

    uint Read32(uint address);

    ulong Read64(uint address);

    void Write8(uint address, byte value);

    void Write16(uint address, ushort value);

    void Write32(uint address, uint value);

    void Write64(uint address, ulong value);
}

/// <summary>The exception codes in Cause (n64brew: "COP0", VR4300 manual chapter 6).</summary>
public enum R4300Exception
{
    Interrupt = 0,
    TlbModification = 1,
    TlbLoad = 2,
    TlbStore = 3,
    AddressErrorLoad = 4,
    AddressErrorStore = 5,
    BusErrorFetch = 6,
    BusErrorData = 7,
    Syscall = 8,
    Breakpoint = 9,
    ReservedInstruction = 10,
    CoprocessorUnusable = 11,
    Overflow = 12,
    Trap = 13,
    FloatingPoint = 15,
    Watch = 23,
}

/// <summary>
/// The Nintendo 64's NEC VR4300 (SPEC §7.11, Phase 16): the MIPS III instruction set in 64-bit mode with the
/// branch delay slot and branch-likely, COP0 (exceptions, the Count/Compare timer, the 32-entry TLB) and the FPU as
/// coprocessor 1. One <see cref="Step"/> runs an instruction (or takes an interrupt) and charges one cycle at
/// 93.75 MHz; multiplies and divides add their latencies.
/// </summary>
/// <remarks>
/// References: the NEC VR4300 user's manual; n64brew ("VR4300", "COP0", "MIPS III instructions"); n64-systemtest's
/// notes on the VR4300's quirks (SRA of 64-bit values, the COP0 latch for unused registers).
/// </remarks>
public sealed partial class R4300Cpu : IStateful
{
    public const ulong ResetVector = 0xFFFF_FFFF_BFC0_0000;

    /// <summary>COP0 register 15: the VR4300's implementation and revision (n64brew: "COP0").</summary>
    public const uint ProcessorId = 0x0000_0B22;

    // Status bits.
    public const uint StatusIe = 1 << 0;
    public const uint StatusExl = 1 << 1;
    public const uint StatusErl = 1 << 2;
    public const uint StatusUx = 1 << 5;
    public const uint StatusSx = 1 << 6;
    public const uint StatusKx = 1 << 7;
    public const uint StatusBev = 1 << 22;
    public const uint StatusFr = 1 << 26;
    public const uint StatusCu0 = 1 << 28;
    public const uint StatusCu1 = 1 << 29;

    /// <summary>The Status bits software can write (TS, bit 21, and bit 19 are read-only).</summary>
    private const uint StatusWriteMask = 0xFF57_FFFF;

    // Cause bits.
    private const uint CauseBd = 1u << 31;
    private const uint CauseIp2 = 1 << 10;
    private const uint CauseIp7 = 1 << 15;

    private static readonly StateTag Tag = StateTag.From("VR43");

    private readonly IR4300Bus _bus;
    private readonly byte[] _rdram;
    private readonly ulong[] _gpr = new ulong[32];

    private ulong _pc;
    private ulong _nextPc;

    /// <summary>The address of the instruction being executed.</summary>
    private ulong _currentPc;

    // Set by a branch or jump, so the next instruction knows it is in a delay slot.
    private bool _nextIsDelaySlot;
    private bool _inDelaySlot;

    private bool _llBit;

    public R4300Cpu(IR4300Bus bus)
    {
        _bus = bus;
        _rdram = bus.Rdram;
        Fpu = new R4300Fpu();
        for (var i = 0; i < Tlb.Length; i++)
        {
            Tlb[i] = default;
        }
    }

    /// <summary>CPU cycles since power-on (93.75 MHz).</summary>
    public long Cycles;

    public R4300Fpu Fpu { get; }

    public ulong Pc => _pc;

    public ulong Hi { get; set; }

    public ulong Lo { get; set; }

    /// <summary>The RCP's interrupt line (Cause IP2), driven by the MIPS Interface.</summary>
    public bool InterruptLine
    {
        get => (_cause & CauseIp2) != 0;
        set
        {
            _cause = value ? _cause | CauseIp2 : _cause & ~CauseIp2;
            UpdateInterruptPending();
        }
    }

    /// <summary>When the timer interrupt (Count reaching Compare) fires next, in <see cref="Cycles"/>.</summary>
    public long NextCompareCycle => _compareCycle;

    public ulong GetRegister(int index) => _gpr[index];

    public void SetRegister(int index, ulong value)
    {
        _gpr[index] = value;
        _gpr[0] = 0;
    }

    /// <summary>Sets the program counter (the simulated PIF boot, tests).</summary>
    public void Jump(ulong address)
    {
        _pc = address;
        _nextPc = address + 4;
        _nextIsDelaySlot = false;
    }

    public void Reset()
    {
        Array.Clear(_gpr);
        Hi = Lo = 0;
        _llBit = false;
        ResetCop0();
        Fpu.Fr = false;
        Jump(ResetVector);
    }

    /// <summary>Runs one instruction, or takes a pending interrupt.</summary>
    public void Step()
    {
        if (_interruptPending)
        {
            _currentPc = _pc;
            _inDelaySlot = _nextIsDelaySlot;
            _nextIsDelaySlot = false;
            RaiseException(R4300Exception.Interrupt);
            Cycles++;
            return;
        }

        _currentPc = _pc;
        _inDelaySlot = _nextIsDelaySlot;
        _nextIsDelaySlot = false;
        Cycles++;

        if (!Fetch(_pc, out var instruction))
        {
            return;
        }

        _pc = _nextPc;
        _nextPc += 4;
        Execute(instruction);
        _gpr[0] = 0;
    }

    /// <summary>Runs instructions until <see cref="Cycles"/> reaches <paramref name="untilCycle"/>, checking the timer.</summary>
    public void Run(long untilCycle)
    {
        while (Cycles < untilCycle)
        {
            if (Cycles >= _compareCycle)
            {
                CompareReached();
            }

            var stop = Math.Min(untilCycle, _compareCycle);
            while (Cycles < stop)
            {
                Step();
            }
        }

        if (Cycles >= _compareCycle)
        {
            CompareReached();
        }
    }

    // ---- Fetching and memory ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch(ulong address, out uint instruction)
    {
        if ((address & 0xFFFF_FFFF_C000_0003) == 0xFFFF_FFFF_8000_0000)
        {
            var physical = (uint)address & 0x1FFF_FFFF;
            if (physical < (uint)_rdram.Length)
            {
                instruction = BinaryPrimitives.ReadUInt32BigEndian(_rdram.AsSpan((int)physical));
                return true;
            }

            instruction = _bus.Read32(physical);
            return true;
        }

        return FetchSlow(address, out instruction);
    }

    private bool FetchSlow(ulong address, out uint instruction)
    {
        instruction = 0;
        if ((address & 3) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Fetch, out var physical))
        {
            return false;
        }

        instruction = physical < (uint)_rdram.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(_rdram.AsSpan((int)physical))
            : _bus.Read32(physical);
        return true;
    }

    private enum Access
    {
        Load,
        Store,
        Fetch,
    }

    /// <summary>Turns a virtual address into a physical one, raising the address error or TLB exception if it can't.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Translate(ulong address, Access access, out uint physical)
    {
        // KSEG0 and KSEG1 (32-bit, sign-extended): unmapped, the common case.
        if ((address & 0xFFFF_FFFF_C000_0000) == 0xFFFF_FFFF_8000_0000)
        {
            physical = (uint)address & 0x1FFF_FFFF;
            return true;
        }

        return TranslateSlow(address, access, out physical);
    }

    private bool TranslateSlow(ulong address, Access access, out uint physical)
    {
        physical = 0;
        var kernel = (_status & (StatusExl | StatusErl)) != 0 || ((_status >> 3) & 3) == 0;
        var supervisor = !kernel && ((_status >> 3) & 3) == 1;

        // Which addressing the address is checked under: 64-bit when the mode's KX/SX/UX bit is set.
        var wide = kernel ? (_status & StatusKx) != 0 : supervisor ? (_status & StatusSx) != 0 : (_status & StatusUx) != 0;
        var load = access == Access.Store ? R4300Exception.AddressErrorStore : R4300Exception.AddressErrorLoad;

        if (!wide)
        {
            if ((long)address != (int)address)
            {
                AddressError(address, load);
                return false;
            }

            var low = (uint)address;
            if (low < 0x8000_0000)
            {
                return TlbTranslate(address, access, false, out physical); // useg / suseg / kuseg
            }

            if (!kernel && !(supervisor && low is >= 0xC000_0000 and < 0xE000_0000))
            {
                AddressError(address, load);
                return false;
            }

            if (low < 0xC000_0000)
            {
                physical = low & 0x1FFF_FFFF;
                return true;
            }

            return TlbTranslate(address, access, false, out physical);
        }

        // 64-bit addressing (VR4300 manual 5.2): xkuseg, xksseg, xkphys, xkseg, then the 32-bit compatibility segments.
        var region = address >> 62;
        if (region == 0)
        {
            if (address < 0x0000_0100_0000_0000)
            {
                return TlbTranslate(address, access, true, out physical);
            }
        }
        else if (region == 1)
        {
            if ((kernel || supervisor) && (address & 0x3FFF_FFFF_FFFF_FFFF) < 0x0000_0100_0000_0000)
            {
                return TlbTranslate(address, access, true, out physical);
            }
        }
        else if (region == 2)
        {
            // xkphys: unmapped, with the cache attribute in bits 59–61. Only physical addresses below 2^32 exist.
            if (kernel && (address & 0x07FF_FFFF_0000_0000) == 0)
            {
                physical = (uint)address;
                return true;
            }
        }
        else if (kernel || supervisor)
        {
            if (address >= 0xFFFF_FFFF_8000_0000)
            {
                var low = (uint)address;
                if (low < 0xC000_0000 && kernel)
                {
                    physical = low & 0x1FFF_FFFF;
                    return true;
                }

                if (kernel || low < 0xE000_0000)
                {
                    return TlbTranslate(address, access, true, out physical);
                }
            }
            else if (kernel && address < 0xC000_00FF_8000_0000)
            {
                return TlbTranslate(address, access, true, out physical); // xkseg
            }
        }

        AddressError(address, load);
        return false;
    }

    // Loads and stores. Each returns false when it raised an exception, so the instruction stops there.

    private bool Load8(ulong address, out byte value)
    {
        value = 0;
        if (!Translate(address, Access.Load, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            value = _rdram[physical];
            return true;
        }

        var word = _bus.Read32(physical & ~3u);
        value = (byte)(word >> (int)(24 - ((physical & 3) << 3)));
        return true;
    }

    private bool Load16(ulong address, out ushort value)
    {
        value = 0;
        if ((address & 1) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            value = BinaryPrimitives.ReadUInt16BigEndian(_rdram.AsSpan((int)physical));
            return true;
        }

        var word = _bus.Read32(physical & ~3u);
        value = (ushort)(word >> (int)(16 - ((physical & 2) << 3)));
        return true;
    }

    private bool Load32(ulong address, out uint value)
    {
        value = 0;
        if ((address & 3) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical))
        {
            return false;
        }

        value = physical < (uint)_rdram.Length
            ? BinaryPrimitives.ReadUInt32BigEndian(_rdram.AsSpan((int)physical))
            : _bus.Read32(physical);
        return true;
    }

    private bool Load64(ulong address, out ulong value)
    {
        value = 0;
        if ((address & 7) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical))
        {
            return false;
        }

        value = physical < (uint)_rdram.Length
            ? BinaryPrimitives.ReadUInt64BigEndian(_rdram.AsSpan((int)physical))
            : _bus.Read64(physical);
        return true;
    }

    private bool Store8(ulong address, byte value)
    {
        if (!Translate(address, Access.Store, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            _rdram[physical] = value;
        }
        else
        {
            _bus.Write8(physical, value);
        }

        return true;
    }

    private bool Store16(ulong address, ushort value)
    {
        if ((address & 1) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_rdram.AsSpan((int)physical), value);
        }
        else
        {
            _bus.Write16(physical, value);
        }

        return true;
    }

    private bool Store32(ulong address, uint value)
    {
        if ((address & 3) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_rdram.AsSpan((int)physical), value);
        }
        else
        {
            _bus.Write32(physical, value);
        }

        return true;
    }

    private bool Store64(ulong address, ulong value)
    {
        if ((address & 7) != 0)
        {
            AddressError(address, R4300Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical))
        {
            return false;
        }

        if (physical < (uint)_rdram.Length)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_rdram.AsSpan((int)physical), value);
        }
        else
        {
            _bus.Write64(physical, value);
        }

        return true;
    }

    /// <summary>
    /// The unaligned forms (LWL/LWR/LDL/LDR and the stores) read or write the aligned word or doubleword around the
    /// address, so they translate that and never raise an address error.
    /// </summary>
    private bool LoadAligned64(ulong address, out ulong value)
    {
        value = 0;
        if (!Translate(address & ~7UL, Access.Load, out var physical))
        {
            return false;
        }

        value = physical < (uint)_rdram.Length
            ? BinaryPrimitives.ReadUInt64BigEndian(_rdram.AsSpan((int)physical))
            : _bus.Read64(physical);
        return true;
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var register in _gpr)
        {
            writer.WriteUInt64(register);
        }

        writer.WriteUInt64(_pc);
        writer.WriteUInt64(_nextPc);
        writer.WriteBool(_nextIsDelaySlot);
        writer.WriteUInt64(Hi);
        writer.WriteUInt64(Lo);
        writer.WriteBool(_llBit);
        writer.WriteInt64(Cycles);
        SaveCop0(writer);
        writer.EndChunk();
        Fpu.SaveState(writer);
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _gpr.Length; i++)
        {
            _gpr[i] = reader.ReadUInt64();
        }

        _pc = reader.ReadUInt64();
        _nextPc = reader.ReadUInt64();
        _nextIsDelaySlot = reader.ReadBool();
        Hi = reader.ReadUInt64();
        Lo = reader.ReadUInt64();
        _llBit = reader.ReadBool();
        Cycles = reader.ReadInt64();
        LoadCop0(reader);
        reader.EndChunk();
        Fpu.LoadState(reader);
        Fpu.Fr = (_status & StatusFr) != 0;
    }
}
