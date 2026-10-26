using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GameVault.Core.State;

namespace GameVault.Core.Cpu.R5900;

/// <summary>The EE's exception codes, as Cause.ExcCode holds them (ps2tek: "EE COP0 Exception Handling").</summary>
public enum R5900Exception
{
    Interrupt = 0x00,
    TlbModified = 0x01,
    TlbLoad = 0x02,
    TlbStore = 0x03,
    AddressErrorLoad = 0x04,
    AddressErrorStore = 0x05,
    BusErrorFetch = 0x06,
    BusErrorData = 0x07,
    Syscall = 0x08,
    Breakpoint = 0x09,
    ReservedInstruction = 0x0A,
    CoprocessorUnusable = 0x0B,
    Overflow = 0x0C,
    Trap = 0x0D,
}

/// <summary>
/// The PlayStation 2's Emotion Engine core, the R5900 (SPEC §7.11, Phase 17): MIPS III with 128-bit GPRs and the
/// R5900's own instructions (the MMI SIMD set, the second multiply pipeline, LQ/SQ, the SA register), COP0 with the
/// 48-entry TLB and the scratchpad, the single-precision FPU and COP2 (VU0 in macro mode) through
/// <see cref="IR5900Cop2"/>. One <see cref="Step"/> runs an instruction and charges one cycle at 294.912 MHz;
/// multiplies and divides stall a later HI/LO read for their latency.
/// </summary>
/// <remarks>
/// References: ps2tek ("EE Instruction Decoding", "EE COP0 …", "EE COP1 …"); the EE Core User's Manual as ps2tek and
/// the emulators describe it; ps2autotests' hardware results for the corner cases (the tests derive from them).
/// <para>
/// Timing (approximate, single-issue): every instruction takes one cycle. MULT/MADD and their pipeline-1 forms make
/// HI/LO (or HI1/LO1) ready 4 cycles later, DIV/DIVU 37, the parallel multiplies 4 and PDIVW/PDIVBW 37; MFHI/MFLO and
/// the other HI/LO readers stall until then. The FPU's DIV/SQRT/RSQRT stall a following FPU op similarly (7/7/13).
/// There is no cache model and no dual issue.
/// </para>
/// </remarks>
public sealed partial class R5900Cpu : IStateful
{
    /// <summary>Where the EE starts after reset (the BIOS, uncached).</summary>
    public const uint ResetVector = 0xBFC0_0000;

    /// <summary>The EE's clock.</summary>
    public const long ClockRate = 294_912_000;

    /// <summary>The scratchpad's size: 16 KB, reached only through a TLB entry with the S bit.</summary>
    public const int ScratchpadSize = 16 * 1024;

    private static readonly StateTag Tag = StateTag.From("R59C");

    private readonly IR5900Bus _bus;
    private readonly IR5900Cop2? _cop2;
    private readonly byte[] _rdram;
    private readonly uint _rdramSize;
    private readonly byte[]? _bios;

    /// <summary>The GPRs: register <c>r</c>'s low 64 bits at <c>2r</c>, its high 64 bits at <c>2r + 1</c>.</summary>
    private readonly ulong[] _gpr = new ulong[64];

    // HI/LO are 128 bits wide: the low halves are HI/LO, the high halves HI1/LO1 (pipeline 1).
    private ulong _hi;
    private ulong _lo;
    private ulong _hi1;
    private ulong _lo1;
    private uint _sa;

    private uint _pc;
    private uint _nextPc;
    private uint _currentPc;
    private bool _nextIsDelaySlot;
    private bool _inDelaySlot;

    // When HI/LO and HI1/LO1 hold their results (the multiply/divide units' latency).
    private long _hiLoReady;
    private long _hiLo1Ready;

    /// <summary>Where the current <see cref="Run"/> slice ends; an idle loop skips ahead to it.</summary>
    private long _sliceEnd;

    public R5900Cpu(IR5900Bus bus, IR5900Cop2? cop2 = null)
    {
        _bus = bus;
        _cop2 = cop2;
        _rdram = bus.Rdram;
        _rdramSize = (uint)_rdram.Length;
        _bios = bus.Bios;
        _fetchMemory = _rdram;
        Reset();
    }

    /// <summary>EE cycles since power-on (294.912 MHz).</summary>
    public long Cycles;

    /// <summary>Set (by a device or the hook) to end <see cref="Run"/> after the current instruction.</summary>
    public bool StopRequested;

    /// <summary>
    /// Called just before an exception is taken, with <see cref="CurrentPc"/> the faulting instruction (or, for an
    /// interrupt, the next one) and <see cref="Pc"/> where execution would continue without it. Returning true skips
    /// the exception: execution continues at <see cref="Pc"/>, which the hook may have changed (with
    /// <see cref="Jump"/>), as may the registers. GameVault's high-level kernel hooks SYSCALL here.
    /// </summary>
    public Func<R5900Exception, bool>? ExceptionHook;

    /// <summary>The 16 KB scratchpad (SPR), which TLB entries with the S bit map. The DMAC's SPR channels use it too.</summary>
    public byte[] Scratchpad { get; } = new byte[ScratchpadSize];

    /// <summary>The next instruction to execute.</summary>
    public uint Pc => _pc;

    /// <summary>The instruction being executed (during <see cref="ExceptionHook"/>, the one that raised it).</summary>
    public uint CurrentPc => _currentPc;

    /// <summary>The instruction being executed is in a branch delay slot.</summary>
    public bool InDelaySlot => _inDelaySlot;

    public ulong Hi
    {
        get => _hi;
        set => _hi = value;
    }

    public ulong Lo
    {
        get => _lo;
        set => _lo = value;
    }

    public ulong Hi1
    {
        get => _hi1;
        set => _hi1 = value;
    }

    public ulong Lo1
    {
        get => _lo1;
        set => _lo1 = value;
    }

    /// <summary>The shift amount register (QFSRV): a byte count, 0–15. MTSAB/MTSAH set it, MFSA/MTSA move it.</summary>
    public uint Sa
    {
        get => _sa;
        set => _sa = value & 0xF;
    }

    public UInt128 GetGpr(int index) => new(_gpr[index * 2 + 1], _gpr[index * 2]);

    public void SetGpr(int index, UInt128 value)
    {
        if (index == 0)
        {
            return;
        }

        _gpr[index * 2] = (ulong)value;
        _gpr[index * 2 + 1] = (ulong)(value >> 64);
    }

    /// <summary>The low 64 bits of a GPR (what the MIPS III instructions see).</summary>
    public ulong GetGpr64(int index) => _gpr[index * 2];

    /// <summary>Sets the low 64 bits of a GPR, leaving the high 64 bits as they are (as the MIPS III instructions do).</summary>
    public void SetGpr64(int index, ulong value)
    {
        if (index != 0)
        {
            _gpr[index * 2] = value;
        }
    }

    /// <summary>Sets the program counter (the kernel, the ELF loader, tests).</summary>
    public void Jump(uint address)
    {
        _pc = address;
        _nextPc = address + 4;
        _nextIsDelaySlot = false;
    }

    public void Reset()
    {
        Array.Clear(_gpr);
        _hi = _lo = _hi1 = _lo1 = 0;
        _sa = 0;
        _hiLoReady = _hiLo1Ready = 0;
        _fpuReady = 0;
        Array.Clear(_fpr);
        _fpuAcc = 0;
        _fcr31 = Fcr31Reset;
        ResetCop0();
        Jump(ResetVector);
        _currentPc = ResetVector;
        _inDelaySlot = false;
    }

    /// <summary>Runs one instruction, or takes a pending interrupt.</summary>
    public void Step()
    {
        if (_interruptPending)
        {
            _currentPc = _pc;
            _inDelaySlot = _nextIsDelaySlot;
            Cycles++;
            TakeInterrupt();
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
    }

    /// <summary>
    /// Runs until <see cref="Cycles"/> reaches <paramref name="untilCycle"/> or <see cref="StopRequested"/> is set,
    /// checking interrupts (INT0, INT1, the timer) between instructions.
    /// </summary>
    public void Run(long untilCycle)
    {
        StopRequested = false;
        while (Cycles < untilCycle && !StopRequested)
        {
            if (Cycles >= _compareCycle)
            {
                CompareReached();
            }

            _sliceEnd = Math.Min(untilCycle, _compareCycle);
            while (Cycles < _sliceEnd && !StopRequested)
            {
                if (_interruptPending)
                {
                    _currentPc = _pc;
                    _inDelaySlot = _nextIsDelaySlot;
                    Cycles++;
                    TakeInterrupt();
                    continue;
                }

                _currentPc = _pc;
                _inDelaySlot = _nextIsDelaySlot;
                _nextIsDelaySlot = false;
                Cycles++;
                if (!Fetch(_pc, out var instruction))
                {
                    continue;
                }

                _pc = _nextPc;
                _nextPc += 4;
                Execute(instruction);
            }
        }

        if (Cycles >= _compareCycle)
        {
            CompareReached();
        }

        _sliceEnd = 0; // outside Run, an idle loop doesn't skip ahead
    }

    // ---- Address translation ----

    // The flattened TLB: one entry per 4 KB virtual page of the TLB-mapped segments. An entry is the physical page
    // (or the scratchpad offset) with these flags; 0 means "no valid mapping", which the slow path resolves into a
    // refill or invalid exception. Rebuilt from the TLB whenever it or the ASID changes.
    private const uint PageValid = 1;
    private const uint PageDirty = 2;
    private const uint PageScratch = 4;

    private readonly uint[] _pageTable = new uint[1 << 20];

    /// <summary>The page (virtual) the fetch cache holds, and where it is in <see cref="_fetchMemory"/> (RDRAM or the BIOS).</summary>
    private uint _fetchPage = 1;
    private uint _fetchEntry;
    private byte[] _fetchMemory;

    private const uint BiosBase = 0x1FC0_0000;

    private enum Access
    {
        Load,
        Store,
        Fetch,
    }

    /// <summary>
    /// Translates a virtual address. Returns the physical address, or with <paramref name="scratch"/> set an offset into
    /// the scratchpad; false when it raised an address error or TLB exception.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Translate(uint address, Access access, out uint physical, out bool scratch)
    {
        if ((int)address < 0)
        {
            if (address < 0xC000_0000 && _kernelMode)
            {
                physical = address & 0x1FFF_FFFF; // KSEG0/KSEG1
                scratch = false;
                return true;
            }
        }
        else
        {
            var entry = _pageTable[address >> 12];
            var need = access == Access.Store ? PageValid | PageDirty : PageValid;
            if ((entry & need) == need)
            {
                physical = (entry & 0xFFFF_F000) | (address & 0xFFF);
                scratch = (entry & PageScratch) != 0;
                return true;
            }
        }

        return TranslateSlow(address, access, out physical, out scratch);
    }

    private bool TranslateSlow(uint address, Access access, out uint physical, out bool scratch)
    {
        physical = 0;
        scratch = false;
        if (address >= 0x8000_0000)
        {
            var allowed = _kernelMode || (_supervisorMode && address is >= 0xC000_0000 and < 0xE000_0000);
            if (!allowed)
            {
                AddressError(address, access == Access.Store ? R5900Exception.AddressErrorStore : R5900Exception.AddressErrorLoad);
                return false;
            }

            if (address < 0xC000_0000)
            {
                physical = address & 0x1FFF_FFFF;
                return true;
            }

            var entry = _pageTable[address >> 12];
            var need = access == Access.Store ? PageValid | PageDirty : PageValid;
            if ((entry & need) == need)
            {
                physical = (entry & 0xFFFF_F000) | (address & 0xFFF);
                scratch = (entry & PageScratch) != 0;
                return true;
            }
        }

        // No fast mapping: find out why (refill, invalid or modified) from the TLB itself.
        TlbFault(address, access);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fetch(uint address, out uint instruction)
    {
        var page = address & 0xFFFF_F000;
        if (page == _fetchPage)
        {
            var offset = (_fetchEntry & 0xFFFF_F000) | (address & 0xFFF);
            instruction = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_fetchMemory), (nint)offset));
            return true;
        }

        return FetchSlow(address, out instruction);
    }

    private bool FetchSlow(uint address, out uint instruction)
    {
        instruction = 0;
        if ((address & 3) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Fetch, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            instruction = MemoryMarshal.Read<uint>(Scratchpad.AsSpan((int)(physical & 0x3FFC)));
            return true;
        }

        if (physical < _rdramSize)
        {
            // Cache the page so the next fetches skip translation.
            _fetchPage = address & 0xFFFF_F000;
            _fetchEntry = physical & 0xFFFF_F000;
            _fetchMemory = _rdram;
            instruction = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_rdram), (nint)physical));
            return true;
        }

        if (_bios is { } bios && physical - BiosBase < (uint)bios.Length)
        {
            _fetchPage = address & 0xFFFF_F000;
            _fetchEntry = (physical - BiosBase) & 0xFFFF_F000;
            _fetchMemory = bios;
            instruction = MemoryMarshal.Read<uint>(bios.AsSpan((int)(physical - BiosBase)));
            return true;
        }

        instruction = _bus.Read32(physical);
        return true;
    }

    /// <summary>Forgets the fetch page (the TLB, the mode or the ASID changed).</summary>
    private void InvalidateFetch() => _fetchPage = 1;

    // ---- Loads and stores. Each returns false when it raised an exception, so the instruction stops there. ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte RdramAt(uint physical) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_rdram), (nint)physical);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte ScratchAt(uint offset) =>
        ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Scratchpad), (nint)(offset & (ScratchpadSize - 1)));

    private bool Load8(uint address, out byte value)
    {
        value = 0;
        if (!Translate(address, Access.Load, out var physical, out var scratch))
        {
            return false;
        }

        value = scratch ? ScratchAt(physical) : physical < _rdramSize ? RdramAt(physical) : _bus.Read8(physical);
        return true;
    }

    private bool Load16(uint address, out ushort value)
    {
        value = 0;
        if ((address & 1) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical, out var scratch))
        {
            return false;
        }

        value = scratch ? Unsafe.ReadUnaligned<ushort>(ref ScratchAt(physical))
            : physical < _rdramSize ? Unsafe.ReadUnaligned<ushort>(ref RdramAt(physical)) : _bus.Read16(physical);
        return true;
    }

    private bool Load32(uint address, out uint value)
    {
        value = 0;
        if ((address & 3) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical, out var scratch))
        {
            return false;
        }

        value = scratch ? Unsafe.ReadUnaligned<uint>(ref ScratchAt(physical))
            : physical < _rdramSize ? Unsafe.ReadUnaligned<uint>(ref RdramAt(physical)) : _bus.Read32(physical);
        return true;
    }

    private bool Load64(uint address, out ulong value)
    {
        value = 0;
        if ((address & 7) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorLoad);
            return false;
        }

        if (!Translate(address, Access.Load, out var physical, out var scratch))
        {
            return false;
        }

        value = scratch ? Unsafe.ReadUnaligned<ulong>(ref ScratchAt(physical))
            : physical < _rdramSize ? Unsafe.ReadUnaligned<ulong>(ref RdramAt(physical)) : _bus.Read64(physical);
        return true;
    }

    /// <summary>A quadword load: the address's low four bits are ignored (LQ, LQC2).</summary>
    private bool Load128(uint address, out ulong low, out ulong high)
    {
        low = high = 0;
        address &= ~15u;
        if (!Translate(address, Access.Load, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            low = Unsafe.ReadUnaligned<ulong>(ref ScratchAt(physical));
            high = Unsafe.ReadUnaligned<ulong>(ref ScratchAt(physical + 8));
        }
        else if (physical < _rdramSize)
        {
            low = Unsafe.ReadUnaligned<ulong>(ref RdramAt(physical));
            high = Unsafe.ReadUnaligned<ulong>(ref RdramAt(physical + 8));
        }
        else
        {
            var value = _bus.Read128(physical);
            low = (ulong)value;
            high = (ulong)(value >> 64);
        }

        return true;
    }

    private bool Store8(uint address, byte value)
    {
        if (!Translate(address, Access.Store, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            ScratchAt(physical) = value;
        }
        else if (physical < _rdramSize)
        {
            RdramAt(physical) = value;
        }
        else
        {
            _bus.Write8(physical, value);
        }

        return true;
    }

    private bool Store16(uint address, ushort value)
    {
        if ((address & 1) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            Unsafe.WriteUnaligned(ref ScratchAt(physical), value);
        }
        else if (physical < _rdramSize)
        {
            Unsafe.WriteUnaligned(ref RdramAt(physical), value);
        }
        else
        {
            _bus.Write16(physical, value);
        }

        return true;
    }

    private bool Store32(uint address, uint value)
    {
        if ((address & 3) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            Unsafe.WriteUnaligned(ref ScratchAt(physical), value);
        }
        else if (physical < _rdramSize)
        {
            Unsafe.WriteUnaligned(ref RdramAt(physical), value);
        }
        else
        {
            _bus.Write32(physical, value);
        }

        return true;
    }

    private bool Store64(uint address, ulong value)
    {
        if ((address & 7) != 0)
        {
            AddressError(address, R5900Exception.AddressErrorStore);
            return false;
        }

        if (!Translate(address, Access.Store, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            Unsafe.WriteUnaligned(ref ScratchAt(physical), value);
        }
        else if (physical < _rdramSize)
        {
            Unsafe.WriteUnaligned(ref RdramAt(physical), value);
        }
        else
        {
            _bus.Write64(physical, value);
        }

        return true;
    }

    private bool Store128(uint address, ulong low, ulong high)
    {
        address &= ~15u;
        if (!Translate(address, Access.Store, out var physical, out var scratch))
        {
            return false;
        }

        if (scratch)
        {
            Unsafe.WriteUnaligned(ref ScratchAt(physical), low);
            Unsafe.WriteUnaligned(ref ScratchAt(physical + 8), high);
        }
        else if (physical < _rdramSize)
        {
            Unsafe.WriteUnaligned(ref RdramAt(physical), low);
            Unsafe.WriteUnaligned(ref RdramAt(physical + 8), high);
        }
        else
        {
            _bus.Write128(physical, new UInt128(high, low));
        }

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

        writer.WriteUInt64(_hi);
        writer.WriteUInt64(_lo);
        writer.WriteUInt64(_hi1);
        writer.WriteUInt64(_lo1);
        writer.WriteUInt32(_sa);
        writer.WriteUInt32(_pc);
        writer.WriteUInt32(_nextPc);
        writer.WriteUInt32(_currentPc);
        writer.WriteBool(_nextIsDelaySlot);
        writer.WriteBool(_inDelaySlot);
        writer.WriteInt64(Cycles);
        writer.WriteInt64(_hiLoReady);
        writer.WriteInt64(_hiLo1Ready);
        writer.WriteBool(Int0);
        writer.WriteBool(Int1);
        writer.WriteBool(Cpcond0);
        writer.WriteBytes(Scratchpad);
        SaveCop0(writer);
        SaveFpu(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _gpr.Length; i++)
        {
            _gpr[i] = reader.ReadUInt64();
        }

        _hi = reader.ReadUInt64();
        _lo = reader.ReadUInt64();
        _hi1 = reader.ReadUInt64();
        _lo1 = reader.ReadUInt64();
        _sa = reader.ReadUInt32();
        _pc = reader.ReadUInt32();
        _nextPc = reader.ReadUInt32();
        _currentPc = reader.ReadUInt32();
        _nextIsDelaySlot = reader.ReadBool();
        _inDelaySlot = reader.ReadBool();
        Cycles = reader.ReadInt64();
        _hiLoReady = reader.ReadInt64();
        _hiLo1Ready = reader.ReadInt64();
        _int0 = reader.ReadBool();
        _int1 = reader.ReadBool();
        Cpcond0 = reader.ReadBool();
        reader.ReadBytes(Scratchpad);
        LoadCop0(reader);
        LoadFpu(reader);
        reader.EndChunk();
    }
}
