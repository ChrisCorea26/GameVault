using System.Buffers.Binary;
using GameVault.Core.Cpu.R3000;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>Interrupt sources in I_STAT / I_MASK (psx-spx: "Interrupts").</summary>
[Flags]
public enum PsInterrupt : uint
{
    VBlank = 1 << 0,
    Gpu = 1 << 1,
    CdRom = 1 << 2,
    Dma = 1 << 3,
    Timer0 = 1 << 4,
    Timer1 = 1 << 5,
    Timer2 = 1 << 6,
    Controller = 1 << 7,
    Sio = 1 << 8,
    Spu = 1 << 9,
    Lightpen = 1 << 10,
}

/// <summary>
/// The Sony PlayStation (SPEC §7.11, Phase 15): the R3000A with 2 MB of RAM, the 512 KB BIOS, the 1 KB scratchpad,
/// the interrupt controller and the GPU. Deterministic, like every core.
/// </summary>
/// <remarks>
/// Physical memory map (psx-spx: "Memory Map"): RAM at $00000000 (2 MB, mirrored to 8 MB), expansion 1 at $1F000000,
/// the scratchpad at $1F800000, I/O at $1F801000, expansion 2 at $1F802000 and the BIOS at $1FC00000.
/// Access times are approximate for now: RAM reads cost 4 cycles, BIOS reads 20 and I/O 2; writes are buffered.
/// </remarks>
public sealed class PlayStationConsole : IR3000Bus, IStateful
{
    public const int RamSize = 2 * 1024 * 1024;
    public const int BiosSize = 512 * 1024;
    public const double CpuClock = 33868800.0;

    private const int RamReadCycles = 4;
    private const int BiosReadCycles = 20;
    private const int IoReadCycles = 2;

    private static readonly StateTag Tag = StateTag.From("PSX ");

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _bios = new byte[BiosSize];
    private readonly byte[] _scratchpad = new byte[1024];

    // I/O registers that don't have a device behind them yet keep what was written, so they read back.
    private readonly byte[] _io = new byte[0x1000];

    private uint _interruptStatus;
    private uint _interruptMask;

    public PlayStationConsole(ReadOnlySpan<byte> bios = default)
    {
        bios[..Math.Min(bios.Length, BiosSize)].CopyTo(_bios);
        HasBios = !bios.IsEmpty;
        Cpu = new R3000Cpu(this);
        Gpu = new PsGpu();
    }

    public R3000Cpu Cpu { get; }

    public PsGpu Gpu { get; }

    /// <summary>False when no BIOS was given: only bare-metal PS-EXE programs can run.</summary>
    public bool HasBios { get; }

    public Span<byte> Ram => _ram;

    /// <summary>Characters the program writes to the debug port at $1F802023 (DUART channel A) or $1F802080.</summary>
    public Action<char>? DebugOutput { get; set; }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_scratchpad);
        Array.Clear(_io);
        _interruptStatus = _interruptMask = 0;
        Gpu.Power();
        Cpu.Reset();
        Cpu.Cycles = 0;
    }

    /// <summary>
    /// Copies a PS-EXE into RAM and starts it with its entry point, global pointer and stack, as the BIOS does
    /// after booting a disc.
    /// </summary>
    public void LoadExe(PsExe exe)
    {
        var start = (int)(exe.LoadAddress & (RamSize - 1));
        var length = Math.Min(exe.Code.Length, RamSize - start);
        exe.Code.AsSpan(0, length).CopyTo(_ram.AsSpan(start));
        Cpu.SetRegister(28, exe.Gp);
        Cpu.SetRegister(29, exe.Stack);
        Cpu.SetRegister(30, exe.Stack);
        Cpu.Jump(exe.Pc);
    }

    /// <summary>Runs the CPU until <paramref name="cycles"/> more have passed.</summary>
    public void RunCycles(long cycles)
    {
        var end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            Cpu.Step();
        }
    }

    /// <summary>Raises an interrupt in I_STAT.</summary>
    public void RaiseInterrupt(PsInterrupt source)
    {
        _interruptStatus |= (uint)source;
        UpdateInterruptLine();
    }

    private void UpdateInterruptLine() => Cpu.InterruptLine = (_interruptStatus & _interruptMask) != 0;

    // ---- Bus ----

    public uint Fetch(uint address) => Read32(address);

    public uint Read32(uint address)
    {
        if (address < 0x00800000)
        {
            Cpu.Cycles += RamReadCycles;
            return BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan((int)(address & (RamSize - 4))));
        }

        if (address - 0x1F800000 < 0x400)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FC)));
        }

        if (address - 0x1FC00000 < BiosSize)
        {
            Cpu.Cycles += BiosReadCycles;
            return BinaryPrimitives.ReadUInt32LittleEndian(_bios.AsSpan((int)(address & (BiosSize - 4))));
        }

        if (address - 0x1F801000 < 0x1000)
        {
            Cpu.Cycles += IoReadCycles;
            return ReadIo(address & ~3u);
        }

        if (address == 0xFFFE0130)
        {
            return Cpu.CacheControl;
        }

        return 0xFFFFFFFF; // expansion regions and unmapped space read as open bus
    }

    public ushort Read16(uint address)
    {
        if (address < 0x00800000)
        {
            Cpu.Cycles += RamReadCycles;
            return BinaryPrimitives.ReadUInt16LittleEndian(_ram.AsSpan((int)(address & (RamSize - 2))));
        }

        if (address - 0x1F800000 < 0x400)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FE)));
        }

        return (ushort)(Read32(address & ~3u) >> (int)((address & 2) * 8));
    }

    public byte Read8(uint address)
    {
        if (address < 0x00800000)
        {
            Cpu.Cycles += RamReadCycles;
            return _ram[address & (RamSize - 1)];
        }

        if (address - 0x1F800000 < 0x400)
        {
            return _scratchpad[address & 0x3FF];
        }

        return (byte)(Read32(address & ~3u) >> (int)((address & 3) * 8));
    }

    public void Write32(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan((int)(address & (RamSize - 4))), value);
        }
        else if (address - 0x1F800000 < 0x400)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FC)), value);
        }
        else if (address - 0x1F801000 < 0x2000)
        {
            WriteIo(address & ~3u, value, 4);
        }
    }

    public void Write16(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_ram.AsSpan((int)(address & (RamSize - 2))), (ushort)value);
        }
        else if (address - 0x1F800000 < 0x400)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_scratchpad.AsSpan((int)(address & 0x3FE)), (ushort)value);
        }
        else if (address - 0x1F801000 < 0x2000)
        {
            WriteIo(address, value, 2);
        }
    }

    public void Write8(uint address, uint value)
    {
        if (address < 0x00800000)
        {
            _ram[address & (RamSize - 1)] = (byte)value;
        }
        else if (address - 0x1F800000 < 0x400)
        {
            _scratchpad[address & 0x3FF] = (byte)value;
        }
        else if (address - 0x1F801000 < 0x2000)
        {
            WriteIo(address, value, 1);
        }
    }

    // ---- I/O ----

    private uint ReadIo(uint address)
    {
        switch (address)
        {
            case 0x1F801070:
                return _interruptStatus;
            case 0x1F801074:
                return _interruptMask;
            case 0x1F801810:
                return Gpu.ReadData();
            case 0x1F801814:
                return Gpu.Status;
            default:
                return BinaryPrimitives.ReadUInt32LittleEndian(_io.AsSpan((int)(address - 0x1F801000)));
        }
    }

    /// <summary>
    /// An I/O write of <paramref name="size"/> bytes, with the CPU register's whole value. 32-bit devices (the GPU,
    /// DMA and the interrupt controller) take that whole value whatever the store's width
    /// (ps1-tests cpu/io-access-bitwidth: a byte store of $12345678 sets DMAC_CTRL to $12345678).
    /// </summary>
    private void WriteIo(uint address, uint value, int size)
    {
        var aligned = address & ~3u;
        var word = value;
        switch (aligned)
        {
            case 0x1F801070:
                // Writing 0 to a bit acknowledges it.
                _interruptStatus &= word;
                UpdateInterruptLine();
                return;
            case 0x1F801074:
                _interruptMask = word & 0x7FF;
                UpdateInterruptLine();
                return;
            case 0x1F801810:
                Gpu.WriteGp0(word);
                return;
            case 0x1F801814:
                Gpu.WriteGp1(word);
                return;
        }

        switch (address)
        {
            case 0x1F802023: // DUART channel A data (the debug serial port psx-spx lists)
            case 0x1F802080: // PCSX-style debug output
                DebugOutput?.Invoke((char)(value & 0xFF));
                return;
        }

        if (address >= 0x1F802000)
        {
            return; // expansion 2: nothing else is attached
        }

        var offset = (int)(address - 0x1F801000);
        switch (size)
        {
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(_io.AsSpan(offset), value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(_io.AsSpan(offset), (ushort)value);
                break;
            default:
                _io[offset] = (byte)value;
                break;
        }
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_scratchpad);
        writer.WriteBytes(_io);
        writer.WriteUInt32(_interruptStatus);
        writer.WriteUInt32(_interruptMask);
        Cpu.SaveState(writer);
        Gpu.SaveState(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        reader.ReadBytes(_scratchpad);
        reader.ReadBytes(_io);
        _interruptStatus = reader.ReadUInt32();
        _interruptMask = reader.ReadUInt32();
        Cpu.LoadState(reader);
        Gpu.LoadState(reader);
        reader.EndChunk();
    }
}
