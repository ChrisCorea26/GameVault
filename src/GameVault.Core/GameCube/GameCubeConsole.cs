using System.Buffers.Binary;
using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.Gekko;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.GameCube;

/// <summary>
/// The Nintendo GameCube (SPEC §7.11, phase 18, milestone 1): the Gekko, 24 MB of main RAM, the processor interface's
/// interrupts, the video interface's frame buffer, the serial interface's controllers, EXI's RTC and SRAM, and the DSP
/// interface's boot handshake and ARAM. Programs are DOL or ELF executables started the way the boot ROM leaves the
/// machine; the GPU (the command processor's FIFO is accepted and ignored), discs and the DSP's sound come later.
/// </summary>
/// <remarks>
/// Physical map: RAM at 0; the command processor $0C000000, pixel engine $0C001000, VI $0C002000, PI $0C003000, memory
/// interface $0C004000, DSP $0C005000, DVD $0C006000, SI $0C006400, EXI $0C006800, AI $0C006C00, the GX FIFO at
/// $0C008000. Reference: YAGCD.
/// </remarks>
public sealed class GameCubeConsole : IEmulatedConsole, IGekkoBus
{
    public const int RamSize = 24 * 1024 * 1024;

    // Processor interface interrupt causes.
    private const uint CauseDi = 1 << 2, CauseSi = 1 << 3, CauseExi = 1 << 4, CauseDsp = 1 << 6, CauseVi = 1 << 8;

    private static readonly StateTag Tag = StateTag.From("GC  ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVGC");
    private static readonly uint[] Rgb555 = BuildPalette();

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _program;
    private readonly ushort[] _frame = new ushort[GcVideoInterface.Width * 576];
    private readonly uint[] _argb = new uint[GcVideoInterface.Width * 576];
    private uint _interruptCause;
    private uint _interruptMask;
    private readonly uint[] _piRegs = new uint[0x40];
    private readonly uint[] _aiRegs = new uint[8];
    private readonly ushort[] _memoryInterface = new ushort[0x40];
    private readonly ushort[] _commandProcessor = new ushort[0x40];
    private readonly ushort[] _pixelEngine = new ushort[0x40];
    private long _fifoBytes;

    /// <param name="program">A DOL or ELF executable.</param>
    /// <exception cref="RomFormatException">Neither a DOL nor a PowerPC ELF.</exception>
    public GameCubeConsole(byte[] program)
    {
        if (!GcExecutable.IsDol(program) && !GcExecutable.IsElf(program))
        {
            throw new RomFormatException("That isn't a GameCube program (a .dol or a PowerPC .elf).");
        }

        _program = program;
        Cpu = new GekkoCpu(this);
        Exi.Ram = _ram;
        Dsp.Ram = _ram;
        RomHash = HashRom(program);
    }

    public GekkoCpu Cpu { get; }

    public GcVideoInterface Video { get; } = new();

    public GcSerialInterface Serial { get; } = new();

    public GcExternalInterface Exi { get; } = new();

    public GcDspInterface Dsp { get; } = new();

    public Span<byte> Ram => _ram;

    public GameSystem System => GameSystem.GameCube;

    public ulong RomHash { get; }

    public int ScreenWidth => GcVideoInterface.Width;

    public int ScreenHeight => Video.Height;

    public double PixelAspect => 4.0 / 3.0 / (GcVideoInterface.Width / (double)Video.Height);

    public ReadOnlySpan<ushort> FrameBuffer => _frame.AsSpan(0, GcVideoInterface.Width * Video.Height);

    public ReadOnlySpan<uint> TrueColorFrame => _argb.AsSpan(0, GcVideoInterface.Width * Video.Height);

    public ReadOnlySpan<uint> Palette => Rgb555;

    public long FrameCount { get; private set; }

    public double CpuClockRate => GekkoCpu.Clock;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 4;

    public IBatteryBacked? Battery => null;

    /// <summary>The CPU's cycles in a video field.</summary>
    private long CyclesPerField => (long)(GekkoCpu.Clock / (Video.Pal ? 50.0 : 59.94));

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate) { ApplyNesFilters = false };
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_frame);
        Array.Clear(_argb);
        Array.Clear(_piRegs);
        Array.Clear(_aiRegs);
        Array.Clear(_memoryInterface);
        Array.Clear(_commandProcessor);
        Array.Clear(_pixelEngine);
        _interruptCause = _interruptMask = 0;
        _fifoBytes = 0;
        Video.Reset();
        Serial.Reset();
        Exi.Reset();
        Dsp.Power();
        FrameCount = 0;
        Cpu.Reset();
        Boot();
    }

    public void Reset() => Power();

    /// <summary>
    /// The boot ROM's work, done here (phase-18.md): the program loaded, the OS globals at $80000000, the BATs mapping
    /// the cached and uncached mirrors, translation and the FPU on, and the stack at the top of the arena.
    /// </summary>
    private void Boot()
    {
        var entry = GcExecutable.Load(_program, _ram);
        void Global(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_ram.AsSpan(offset), value);
        Global(0x20, 0x0D15EA5E); // boot magic
        Global(0x24, 1); // the boot ROM's version
        Global(0x28, RamSize); // physical memory
        Global(0x2C, 0x00000003); // console type: a retail GameCube
        Global(0x34, 0x81800000); // the arena's top
        Global(0xCC, 0); // video mode: NTSC
        Global(0xF0, RamSize); // simulated memory
        Global(0xF8, 162_000_000); // the bus clock
        Global(0xFC, 486_000_000); // the CPU clock
        Cpu.SetBat(data: false, 0, 0x80001FFF, 0x00000002);
        Cpu.SetBat(data: true, 0, 0x80001FFF, 0x00000002);
        Cpu.SetBat(data: true, 1, 0xC0001FFF, 0x0000002A);
        Cpu.Msr = 0x00002032; // FP, IR, DR, RI; vectors at 0
        Cpu.SetGpr(1, 0x816FFFF0);
        Cpu.Dec = 0xFFFFFFFF; // already past zero: no decrementer exception until the OS sets it
        Cpu.Pc = entry;
    }

    public void RunFrame()
    {
        var lines = Video.LinesPerField;
        var perLine = CyclesPerField / lines;
        for (var line = 0; line < lines; line++)
        {
            Video.BeginLine(line);
            UpdateInterrupts();
            var target = Cpu.Cycles + perLine;
            while (Cpu.Cycles < target)
            {
                Cpu.Step();
            }
        }

        Video.Render(_ram);
        Video.Picture.CopyTo(_argb);
        for (var i = 0; i < Video.Picture.Length; i++)
        {
            var c = _argb[i];
            _frame[i] = (ushort)(((c >> 19) & 0x1F) << 10 | ((c >> 11) & 0x1F) << 5 | ((c >> 3) & 0x1F));
        }

        Audio?.EndFrame((int)CyclesPerField);
        FrameCount++;
    }

    public void RunFrameSilently() => RunFrame();

    /// <summary>
    /// A controller on the PlayStation 2 layout: the left stick (or the D-pad's directions) the control stick, the right
    /// stick the C-stick; South A, West B, East X, North Y; R1 Z; L2 and R2 the triggers, pressed fully; Start.
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if ((uint)port >= 4)
        {
            return;
        }

        var pad = GcPadState.Centred;
        void Map(ControllerState button, ushort bit)
        {
            if ((state & button) != 0)
            {
                pad.Buttons |= bit;
            }
        }

        Map(ControllerState.B, GcPadState.A);
        Map(ControllerState.Y, GcPadState.B);
        Map(ControllerState.A, GcPadState.X);
        Map(ControllerState.X, GcPadState.Y);
        Map(ControllerState.Start, GcPadState.Start);
        Map(ControllerState.R, GcPadState.Z);
        Map(ControllerState.L2, GcPadState.L);
        Map(ControllerState.R2, GcPadState.R);
        Map(ControllerState.Up, GcPadState.Up);
        Map(ControllerState.Down, GcPadState.Down);
        Map(ControllerState.Left, GcPadState.Left);
        Map(ControllerState.Right, GcPadState.Right);
        pad.TriggerL = (state & ControllerState.L2) != 0 ? (byte)0xFF : (byte)0;
        pad.TriggerR = (state & ControllerState.R2) != 0 ? (byte)0xFF : (byte)0;
        pad.StickX = (byte)(0x80 + state.StickX());
        pad.StickY = (byte)(0x80 + state.StickY());
        pad.CStickX = (byte)(0x80 + state.RightStickX());
        pad.CStickY = (byte)(0x80 + state.RightStickY());
        Serial.Pads[port] = port == 0 || state != ControllerState.None ? pad : Serial.Pads[port];
    }

    // ---- The processor interface ----

    private void UpdateInterrupts()
    {
        var cause = _interruptCause & ~(CauseVi | CauseSi | CauseExi | CauseDsp);
        if (Video.Interrupt)
        {
            cause |= CauseVi;
        }

        if (Serial.Interrupt)
        {
            cause |= CauseSi;
        }

        if (Exi.Interrupt)
        {
            cause |= CauseExi;
        }

        if (Dsp.Interrupt)
        {
            cause |= CauseDsp;
        }

        _interruptCause = cause;
    }

    /// <summary>The causes are brought up to date on every register write and at every line, so this only reads them.</summary>
    public bool ExternalInterrupt => (_interruptCause & _interruptMask) != 0;

    // ---- The bus ----

    public byte Read8(uint address)
    {
        if (address < RamSize)
        {
            return _ram[address];
        }

        var word = Read32(address & ~3u);
        return (byte)(word >> (int)(24 - (address & 3) * 8));
    }

    public ushort Read16(uint address)
    {
        if (address < RamSize)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(_ram.AsSpan((int)address));
        }

        if ((address & 0xFFFFF000) == 0x0C002000)
        {
            return Video.Read16((int)(address & 0xFFF));
        }

        if ((address & 0xFFFFF000) == 0x0C005000)
        {
            return Dsp.Read16((int)(address & 0xFFF));
        }

        var a = address & 0xFFF;
        return (address & 0xFFFFF000) switch
        {
            0x0C000000 => _commandProcessor[(a >> 1) & 0x3F],
            0x0C001000 => _pixelEngine[(a >> 1) & 0x3F],
            0x0C004000 => _memoryInterface[(a >> 1) & 0x3F],
            _ => (address & 2) == 0 ? (ushort)(Read32(address) >> 16) : (ushort)Read32(address & ~3u),
        };
    }

    public uint Read32(uint address)
    {
        if (address < RamSize)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(_ram.AsSpan((int)address));
        }

        switch (address & 0xFFFFFC00)
        {
            case 0x0C003000:
                return (address & 0xFF) switch
                {
                    0x00 => InterruptCauseRegister,
                    0x04 => _interruptMask,
                    0x2C => 0x246500B1, // the Flipper's revision
                    _ => _piRegs[(address & 0xFF) >> 2],
                };
            case 0x0C006400:
                return Serial.Read32((int)(address & 0xFF));
            case 0x0C006800:
                return Exi.Read32((int)(address & 0xFF));
            case 0x0C006C00:
                return _aiRegs[(address >> 2) & 7];
            case 0x0C006000:
                return 0; // the DVD interface: no disc yet
        }

        if ((address & 0xFFFFF000) is 0x0C002000 or 0x0C005000 or 0x0C000000 or 0x0C001000 or 0x0C004000)
        {
            return ((uint)Read16(address) << 16) | Read16(address + 2);
        }

        return 0;
    }

    /// <summary>The cause register reads the reset button's state in bit 16 (not pressed) with the causes.</summary>
    private uint InterruptCauseRegister
    {
        get
        {
            UpdateInterrupts();
            return _interruptCause | 0x00010000;
        }
    }

    public void Write8(uint address, byte value)
    {
        if (address < RamSize)
        {
            _ram[address] = value;
        }
        else if ((address & 0xFFFFF000) == 0x0C008000)
        {
            _fifoBytes++;
        }
    }

    public void Write16(uint address, ushort value)
    {
        if (address < RamSize)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_ram.AsSpan((int)address), value);
            return;
        }

        var a = address & 0xFFF;
        switch (address & 0xFFFFF000)
        {
            case 0x0C002000: Video.Write16((int)a, value); break;
            case 0x0C005000: Dsp.Write16((int)a, value); break;
            case 0x0C000000: _commandProcessor[(a >> 1) & 0x3F] = value; break;
            case 0x0C001000: _pixelEngine[(a >> 1) & 0x3F] = value; break;
            case 0x0C004000: _memoryInterface[(a >> 1) & 0x3F] = value; break;
            case 0x0C008000: _fifoBytes += 2; break;
        }

        UpdateInterrupts();
    }

    public void Write32(uint address, uint value)
    {
        if (address < RamSize)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_ram.AsSpan((int)address), value);
            return;
        }

        switch (address & 0xFFFFFC00)
        {
            case 0x0C003000:
                switch (address & 0xFF)
                {
                    case 0x00:
                        _interruptCause &= ~value; // writing 1 clears a latched cause
                        break;
                    case 0x04:
                        _interruptMask = value;
                        break;
                    default:
                        _piRegs[(address & 0xFF) >> 2] = value;
                        break;
                }

                break;
            case 0x0C006400:
                Serial.Write32((int)(address & 0xFF), value);
                break;
            case 0x0C006800:
                Exi.Write32((int)(address & 0xFF), value);
                break;
            case 0x0C006C00:
                _aiRegs[(address >> 2) & 7] = value;
                break;
            case 0x0C008000:
                _fifoBytes += 4; // GX commands: drawn from milestone 2
                break;
            default:
                if ((address & 0xFFFFF000) is 0x0C002000 or 0x0C005000 or 0x0C000000 or 0x0C001000 or 0x0C004000)
                {
                    Write16(address, (ushort)(value >> 16));
                    Write16(address + 2, (ushort)value);
                }

                break;
        }

        UpdateInterrupts();
    }

    /// <summary>Main RAM from 0 (the program's view at $80000000).</summary>
    public byte PeekMemory(int address) => (uint)address < RamSize ? _ram[address] : (byte)0;

    /// <summary>The bytes the program has written to the GX FIFO (drawn from milestone 2).</summary>
    public long FifoBytes => _fifoBytes;

    // ---- State ----

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter(RamSize + GcDspInterface.AramSize + 0x10000);
        SaveSnapshot(writer);
        return writer.ToArray();
    }

    public void LoadSnapshot(ReadOnlyMemory<byte> data)
    {
        var reader = new StateReader(data);
        reader.BeginChunk(SnapshotTag);
        if (reader.ReadUInt64() != RomHash)
        {
            throw new StateFormatException("This save state belongs to a different game.");
        }

        LoadState(reader);
        reader.EndChunk();
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Video.SaveState(writer);
        Serial.SaveState(writer);
        Exi.SaveState(writer);
        Dsp.SaveState(writer);
        writer.WriteBytes(_ram);
        writer.WriteUInt32(_interruptCause);
        writer.WriteUInt32(_interruptMask);
        foreach (var array in (uint[][])[_piRegs, _aiRegs])
        {
            foreach (var v in array)
            {
                writer.WriteUInt32(v);
            }
        }

        foreach (var array in (ushort[][])[_memoryInterface, _commandProcessor, _pixelEngine])
        {
            foreach (var v in array)
            {
                writer.WriteUInt16(v);
            }
        }

        writer.WriteInt64(_fifoBytes);
        writer.WriteInt64(FrameCount);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Video.LoadState(reader);
        Serial.LoadState(reader);
        Exi.LoadState(reader);
        Dsp.LoadState(reader);
        reader.ReadBytes(_ram);
        _interruptCause = reader.ReadUInt32();
        _interruptMask = reader.ReadUInt32();
        foreach (var array in (uint[][])[_piRegs, _aiRegs])
        {
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = reader.ReadUInt32();
            }
        }

        foreach (var array in (ushort[][])[_memoryInterface, _commandProcessor, _pixelEngine])
        {
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = reader.ReadUInt16();
            }
        }

        _fifoBytes = reader.ReadInt64();
        FrameCount = reader.ReadInt64();
        reader.EndChunk();
        Video.Render(_ram);
        Video.Picture.CopyTo(_argb);
    }

    private static uint[] BuildPalette()
    {
        var palette = new uint[32768];
        for (var c = 0; c < palette.Length; c++)
        {
            uint Expand(int v) => (uint)((v << 3) | (v >> 2));
            palette[c] = 0xFF000000 | (Expand((c >> 10) & 0x1F) << 16) | (Expand((c >> 5) & 0x1F) << 8) | Expand(c & 0x1F);
        }

        return palette;
    }

    private static ulong HashRom(ReadOnlySpan<byte> rom)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in rom)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
