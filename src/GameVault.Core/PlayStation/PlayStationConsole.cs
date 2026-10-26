using System.Buffers.Binary;
using System.Text;
using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.R3000;
using GameVault.Core.Input;
using GameVault.Core.Snes;
using GameVault.Core.State;
using GameVault.Core.Systems;

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
/// The Sony PlayStation (SPEC §7.11, Phase 15): the R3000A with 2 MB of RAM, the player's 512 KB BIOS and the
/// 1 KB scratchpad; the GPU, SPU, MDEC, CD-ROM drive, timers, DMA and the controller and memory card port.
/// Deterministic, like every core: everything runs on the CPU clock.
/// </summary>
/// <remarks>
/// Physical memory map (psx-spx: "Memory Map"): RAM at $00000000 (2 MB, mirrored to 8 MB), expansion 1 at $1F000000,
/// the scratchpad at $1F800000, I/O at $1F801000, expansion 2 at $1F802000 and the BIOS at $1FC00000.
/// Access times are approximate: RAM reads cost 4 cycles, BIOS reads 20 and I/O 2; writes are buffered.
/// Devices catch up lazily: the CPU runs to the next event any device has scheduled (a line, a sample, a timer, a CD
/// response), and a device catches up before its registers are touched. Video timing follows the GPU's mode: 3,413
/// GPU clocks a line and 263 lines in NTSC, 3,406 and 314 in PAL, with the GPU clock 11/7 of the CPU's.
/// </remarks>
public sealed partial class PlayStationConsole : IEmulatedConsole, IR3000Bus
{
    public const int RamSize = 2 * 1024 * 1024;
    public const int BiosSize = 512 * 1024;
    public const double CpuClock = 33868800.0;
    public const int MaxWidth = 640;
    public const int MaxHeight = 480;

    /// <summary>Where the BIOS's shell starts, the moment PS-EXE loading and fast boot take over (psx-spx: "BIOS").</summary>
    public const uint ShellEntry = 0x80030000;

    private const int RamReadCycles = 4;
    private const int BiosReadCycles = 20;
    private const int IoReadCycles = 2;
    private const int MaxSyncInterval = 4096;

    private static readonly StateTag Tag = StateTag.From("PSX ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVPS");

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _bios = new byte[BiosSize];
    private readonly byte[] _scratchpad = new byte[1024];
    private readonly ushort[] _frame = new ushort[MaxWidth * MaxHeight];

    // I/O registers without a device behind them (memory control, SIO1, RAM size) keep what was written.
    private readonly byte[] _io = new byte[0x1000];

    private readonly PsExe? _exe;
    private readonly bool _fastBoot;

    private uint _interruptStatus;
    private uint _interruptMask;

    // Scheduling.
    private long _lastSync;
    private long _nextSync;
    private int _spuCycles;
    private bool _bootHook;
    private bool _frameDone;

    // Video timing.
    private int _gpuTickFraction;
    private int _lineTick;
    private int _line;
    private int _dotFraction;
    private bool _inVBlank;
    private bool _gpuIrq;

    // The finished frame.
    private int _frameWidth = 320;
    private int _frameHeight = 240;

    // Audio.
    private int _samples;
    private float _lastSample;

    /// <param name="bios">The player's BIOS (512 KB), or empty for bare-metal programs only.</param>
    /// <param name="disc">The disc in the drive, if any.</param>
    /// <param name="exe">A PS-EXE to run: after the BIOS boots (at the shell) or, without a BIOS, straight away.</param>
    /// <param name="card">The memory card in slot 1; a new formatted card when null.</param>
    /// <param name="fastBoot">Skip the BIOS's intro and start the disc's boot executable directly.</param>
    public PlayStationConsole(
        ReadOnlySpan<byte> bios = default, ICdImage? disc = null, PsExe? exe = null, PsMemoryCard? card = null, bool fastBoot = false)
    {
        bios[..Math.Min(bios.Length, BiosSize)].CopyTo(_bios);
        HasBios = !bios.IsEmpty;
        _exe = exe;
        _fastBoot = fastBoot && disc is not null;
        Cpu = new R3000Cpu(this);
        Gpu = new PsGpu();
        Spu = new PsSpu();
        Mdec = new PsMdec();
        CdRom = new PsCdRom();
        Timers = new PsTimers();
        Sio = new PsSio(card ?? new PsMemoryCard());

        BiosRegion = ReadBiosRegion(_bios);
        CdRom.FallbackRegion = BiosRegion;
        CdRom.InsertDisc(disc);
        CdRom.Interrupt = () => RaiseInterrupt(PsInterrupt.CdRom);
        Spu.CdAudio = CdRom;
        Spu.Irq = () => RaiseInterrupt(PsInterrupt.Spu);
        Spu.SampleReady = OnSample;
        Timers.Interrupt = timer => RaiseInterrupt((PsInterrupt)((uint)PsInterrupt.Timer0 << timer));
        Sio.Interrupt = () => RaiseInterrupt(PsInterrupt.Controller);

        RomHash = disc is not null ? HashDisc(disc) : exe is not null ? Fnv(exe.Code) : 0;
    }

    public GameSystem System => GameSystem.PlayStation;

    public R3000Cpu Cpu { get; }

    public PsGpu Gpu { get; }

    public PsSpu Spu { get; }

    public PsMdec Mdec { get; }

    public PsCdRom CdRom { get; }

    public PsTimers Timers { get; }

    public PsSio Sio { get; }

    /// <summary>False when no BIOS was given: only bare-metal PS-EXE programs can run.</summary>
    public bool HasBios { get; }

    /// <summary>"SCEA", "SCEE" or "SCEI", from the BIOS's version string (America when unknown).</summary>
    public string BiosRegion { get; }

    public Span<byte> Ram => _ram;

    public ulong RomHash { get; }

    public int ScreenWidth => _frameWidth;

    public int ScreenHeight => _frameHeight;

    /// <summary>Every display mode fills a 4:3 screen.</summary>
    public double PixelAspect => 4.0 / 3.0 / ((double)_frameWidth / _frameHeight);

    public ReadOnlySpan<ushort> FrameBuffer => _frame.AsSpan(0, _frameWidth * _frameHeight);

    /// <summary>The frame holds BGR555 colours, the same layout as the SNES's.</summary>
    public ReadOnlySpan<uint> Palette => SnesPpu.Palette;

    public long FrameCount { get; private set; }

    /// <summary>Audio is stamped in SPU samples (44,100 Hz).</summary>
    public double CpuClockRate => PsSpu.SampleRate;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    public IBatteryBacked? Battery => Sio.Card(0);

    /// <summary>Characters the program writes to the debug port at $1F802023 (DUART channel A) or $1F802080.</summary>
    public Action<char>? DebugOutput { get; set; }

    /// <summary>
    /// Characters the program prints through the BIOS (putchar: A0h function 3Ch, B0h function 3Dh, which printf
    /// uses), for tests and homebrew debugging. Watching costs speed, so leave it null in play.
    /// </summary>
    public Action<char>? TtyOutput { get; set; }

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(PsSpu.SampleRate, sampleRate) { ApplyNesFilters = false };
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_scratchpad);
        Array.Clear(_io);
        Gpu.Power();
        Spu.Power();
        FrameCount = 0;
        Reset();
    }

    public void Reset()
    {
        _interruptStatus = _interruptMask = 0;
        Gpu.Reset();
        Mdec.Reset();
        CdRom.Reset();
        Timers.Reset();
        Sio.Reset();
        Array.Clear(_dmaAddress);
        Array.Clear(_dmaBlock);
        Array.Clear(_dmaControl);
        _dmaPriority = 0x07654321;
        _dmaInterrupt = 0;
        Cpu.Reset();
        Cpu.Cycles = 0;
        _lastSync = 0;
        _nextSync = 0;
        _spuCycles = 0;
        _gpuTickFraction = _lineTick = _line = _dotFraction = 0;
        _inVBlank = false;
        _gpuIrq = false;

        if (HasBios)
        {
            _bootHook = _exe is not null || _fastBoot;
        }
        else
        {
            _bootHook = false;
            if (_exe is not null)
            {
                LoadExe(_exe);
            }
        }
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

    /// <summary>Changes the disc in the drive (multi-disc games): the shell opens and closes with the new disc.</summary>
    public void ChangeDisc(ICdImage? disc) => CdRom.InsertDisc(disc);

    // ---- Running ----

    public void RunFrame()
    {
        _frameDone = false;
        var guard = Cpu.Cycles + (long)(CpuClock / 10); // a frame never takes longer than a tenth of a second
        while (!_frameDone && Cpu.Cycles < guard)
        {
            if (_bootHook || TtyOutput is not null)
            {
                RunWatched(_nextSync);
            }
            else
            {
                while (Cpu.Cycles < _nextSync)
                {
                    Cpu.Step();
                }
            }

            Sync();
        }

        if (!_frameDone)
        {
            CaptureFrame(); // video timing stalled (it can't, but never hang the host)
        }

        Audio?.EndFrame(_samples);
        _samples = 0;
        FrameCount++;
    }

    public void RunFrameSilently()
    {
        var audio = Audio;
        Audio = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Audio = audio;
        }
    }

    /// <summary>Runs the CPU for <paramref name="cycles"/> more cycles, with the devices (tests and bare-metal programs).</summary>
    public void RunCycles(long cycles)
    {
        var end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            if (_bootHook || TtyOutput is not null)
            {
                RunWatched(Math.Min(end, _nextSync));
            }
            else
            {
                var stop = Math.Min(end, _nextSync);
                while (Cpu.Cycles < stop)
                {
                    Cpu.Step();
                }
            }

            Sync();
        }
    }

    /// <summary>Steps until <paramref name="stop"/>, watching for the BIOS reaching its shell and for BIOS putchar calls.</summary>
    private void RunWatched(long stop)
    {
        while (Cpu.Cycles < stop)
        {
            var pc = Cpu.Pc;
            if (_bootHook && pc == ShellEntry)
            {
                _bootHook = false;
                if (_exe is not null)
                {
                    LoadExe(_exe);
                }
                else if (_fastBoot && CdRom.Disc is { } disc && PsDisc.BootExecutable(disc) is { } boot && PsExe.IsPsExe(boot))
                {
                    LoadExe(PsExe.Parse(boot));
                }

                continue;
            }

            if (TtyOutput is { } tty)
            {
                var function = Cpu.GetRegister(9);
                var vector = pc & 0x1FFFFFFF;
                if ((vector == 0xA0 && function == 0x3C) || (vector == 0xB0 && function == 0x3D))
                {
                    tty((char)(Cpu.GetRegister(4) & 0xFF));
                }
            }

            Cpu.Step();
        }
    }

    public void SetControllerState(int port, ControllerState state)
    {
        if (port is 0 or 1)
        {
            Sio.SetPad(port, state);
        }
    }

    /// <summary>Raises an interrupt in I_STAT.</summary>
    public void RaiseInterrupt(PsInterrupt source)
    {
        _interruptStatus |= (uint)source;
        UpdateInterruptLine();
    }

    private void UpdateInterruptLine() => Cpu.InterruptLine = (_interruptStatus & _interruptMask) != 0;

    // ---- Scheduling ----

    /// <summary>Brings every device up to the CPU's time, then schedules the next sync.</summary>
    private void Sync()
    {
        var now = Cpu.Cycles;
        var delta = (int)(now - _lastSync);
        if (delta > 0)
        {
            _lastSync = now;
            Timers.AdvanceCycles(delta);
            AdvanceVideo(delta);
            _spuCycles += delta;
            while (_spuCycles >= PsSpu.CyclesPerSample)
            {
                _spuCycles -= PsSpu.CyclesPerSample;
                Spu.Tick();
            }

            CdRom.Advance(now);
            Sio.Advance(now);
            if ((_dmaControl[DmaMdecIn] | _dmaControl[DmaMdecOut]) != 0)
            {
                PollDmaRequests();
            }
        }

        var next = Math.Min(MaxSyncInterval, CyclesToNextLine());
        next = Math.Min(next, Timers.CyclesUntilEvent());
        next = Math.Min(next, PsSpu.CyclesPerSample - _spuCycles);
        next = (int)Math.Min(next, Math.Max(1, CdRom.NextEvent - now));
        next = (int)Math.Min(next, Math.Max(1, Sio.NextEvent - now));
        _nextSync = now + Math.Max(1, next);
    }

    private int TicksPerLine => Gpu.PalMode ? 3406 : 3413;

    private int LinesPerFrame => Gpu.PalMode ? 314 : 263;

    /// <summary>CPU cycles until the GPU's line counter next advances (GPU clocks are 11/7 of the CPU's).</summary>
    private int CyclesToNextLine()
    {
        var remaining = (long)(TicksPerLine - _lineTick) * 7 - _gpuTickFraction;
        return (int)Math.Max(1, (remaining + 10) / 11);
    }

    private void AdvanceVideo(int cycles)
    {
        var total = (long)cycles * 11 + _gpuTickFraction;
        var ticks = (int)(total / 7);
        _gpuTickFraction = (int)(total % 7);

        _dotFraction += ticks;
        var divider = Gpu.DotClockDivider;
        Timers.AdvanceDots(_dotFraction / divider);
        _dotFraction %= divider;

        _lineTick += ticks;
        while (_lineTick >= TicksPerLine)
        {
            _lineTick -= TicksPerLine;
            NextLine();
        }

        if (Gpu.Irq && !_gpuIrq)
        {
            RaiseInterrupt(PsInterrupt.Gpu);
        }

        _gpuIrq = Gpu.Irq;
    }

    private void NextLine()
    {
        Timers.HBlank();
        _line++;
        if (_line >= LinesPerFrame)
        {
            _line = 0;
        }

        var vblankStart = Math.Clamp(Gpu.DisplayLineEnd, 1, LinesPerFrame - 1);
        var vblankEnd = Math.Clamp(Gpu.DisplayLineStart, 0, vblankStart - 1);
        if (_line == vblankStart)
        {
            _inVBlank = true;
            Timers.VBlank(true);
            RaiseInterrupt(PsInterrupt.VBlank);
            CaptureFrame();
            Gpu.OddField = !Gpu.OddField;
        }
        else if (_line == vblankEnd && _inVBlank)
        {
            _inVBlank = false;
            Timers.VBlank(false);
        }

        Gpu.OddLine = !_inVBlank && (Gpu.Interlaced ? Gpu.OddField : (_line & 1) != 0);
    }

    private void CaptureFrame()
    {
        _frameWidth = Gpu.DisplayWidth;
        _frameHeight = Gpu.DisplayHeight;
        Gpu.CopyDisplay(_frame);
        _frameDone = true;
    }

    private void OnSample(int left, int right)
    {
        if (Audio is not { } audio)
        {
            return;
        }

        var level = (left + right) * (0.9f / 65536);
        if (level != _lastSample)
        {
            audio.AddDelta(_samples, level - _lastSample);
            _lastSample = level;
        }

        _samples++;
    }

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
            return ReadIo(address & ~3u, 4);
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

        if (address - 0x1F801000 < 0x1000)
        {
            Cpu.Cycles += IoReadCycles;
            return (ushort)ReadIo(address & ~1u, 2);
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

        if (address - 0x1F801000 < 0x1000)
        {
            Cpu.Cycles += IoReadCycles;
            return (byte)ReadIo(address, 1);
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

    /// <summary>
    /// An I/O read of <paramref name="size"/> bytes at <paramref name="address"/>. Each device sees only the registers
    /// the access covers, so reading a byte of the CD-ROM's response FIFO never pops its neighbours.
    /// </summary>
    private uint ReadIo(uint address, int size)
    {
        switch (address)
        {
            case >= 0x1F801040 and < 0x1F801050:
                Sync();
                return Sio.Read((int)(address & 0xF), Cpu.Cycles);
            case 0x1F801070:
            case 0x1F801072 when size < 4:
                return _interruptStatus >> (int)((address & 2) * 8);
            case 0x1F801074:
            case 0x1F801076 when size < 4:
                return _interruptMask >> (int)((address & 2) * 8);
            case >= 0x1F801080 and < 0x1F801100:
                return ReadDma(address & ~3u) >> (int)((address & 3) * 8);
            case >= 0x1F801100 and < 0x1F801130:
                Sync();
                return Timers.Read((int)((address >> 4) & 3), (int)((address >> 2) & 3));
            case >= 0x1F801800 and < 0x1F801804:
            {
                Sync();
                uint value = 0;
                for (var i = 0; i < size; i++)
                {
                    value |= (uint)CdRom.Read((int)((address + i) & 3)) << (8 * i);
                }

                return value;
            }

            case 0x1F801810:
                return Gpu.ReadData();
            case 0x1F801814:
                return Gpu.Status;
            case 0x1F801820:
                return Mdec.ReadData();
            case 0x1F801824:
                return Mdec.Status;
            case >= 0x1F801C00 and < 0x1F802000:
            {
                Sync();
                var low = Spu.Read16(address - 0x1F801C00 & 0x3FE);
                if (size < 4)
                {
                    return (address & 1) != 0 ? (uint)(low >> 8) : low;
                }

                return low | (uint)Spu.Read16((address + 2 - 0x1F801C00) & 0x3FE) << 16;
            }

            default:
                var offset = (int)(address - 0x1F801000);
                return size switch
                {
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(_io.AsSpan(offset)),
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(_io.AsSpan(offset)),
                    _ => _io[offset],
                };
        }
    }

    /// <summary>
    /// An I/O write of <paramref name="size"/> bytes, with the CPU register's whole value. 32-bit devices (the GPU,
    /// DMA, timers and the interrupt controller) take that whole value whatever the store's width
    /// (ps1-tests cpu/io-access-bitwidth: a byte store of $12345678 sets DMAC_CTRL to $12345678).
    /// </summary>
    private void WriteIo(uint address, uint value, int size)
    {
        var aligned = address & ~3u;
        switch (aligned)
        {
            case 0x1F801070:
                // Writing 0 to a bit acknowledges it.
                _interruptStatus &= value;
                UpdateInterruptLine();
                return;
            case 0x1F801074:
                _interruptMask = value & 0x7FF;
                UpdateInterruptLine();
                return;
            case >= 0x1F801080 and < 0x1F801100:
                WriteDma(aligned, value);
                return;
            case >= 0x1F801100 and < 0x1F801130:
                Sync();
                Timers.Write((int)((address >> 4) & 3), (int)((address >> 2) & 3), (ushort)value);
                Sync();
                return;
            case 0x1F801810:
                WriteGp0(value);
                return;
            case 0x1F801814:
                Gpu.WriteGp1(value);
                return;
            case 0x1F801820:
                Mdec.WriteCommand(value);
                PollDmaRequests();
                return;
            case 0x1F801824:
                Mdec.WriteControl(value);
                PollDmaRequests();
                return;
        }

        switch (address)
        {
            case >= 0x1F801040 and < 0x1F801050:
                Sync();
                Sio.Write((int)(address & 0xF), (ushort)value, Cpu.Cycles);
                Sync();
                return;
            case >= 0x1F801800 and < 0x1F801804:
                Sync();
                for (var i = 0; i < size && address + i < 0x1F801804; i++)
                {
                    CdRom.Write((int)((address + i) & 3), (byte)(value >> (8 * i)));
                }

                Sync();
                return;
            case >= 0x1F801C00 and < 0x1F802000:
                Sync();
                Spu.Write16(address - 0x1F801C00 & 0x3FE, (ushort)value);
                if (size == 4)
                {
                    Spu.Write16((address + 2 - 0x1F801C00) & 0x3FE, (ushort)(value >> 16));
                }

                return;
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

    private void WriteGp0(uint value)
    {
        Gpu.WriteGp0(value);
        if (Gpu.Irq && !_gpuIrq)
        {
            RaiseInterrupt(PsInterrupt.Gpu);
        }

        _gpuIrq = Gpu.Irq;
    }

    // ---- Identification ----

    /// <summary>The BIOS's region, from the letter after its "System ROM Version" string (A, E or J).</summary>
    private static string ReadBiosRegion(ReadOnlySpan<byte> bios)
    {
        var marker = "System ROM Version"u8;
        var at = bios.IndexOf(marker);
        if (at >= 0)
        {
            var end = Math.Min(bios.Length, at + 40);
            for (var i = at + marker.Length; i < end && bios[i] != 0; i++)
            {
                if (bios[i] == ' ' && i + 2 <= end && (i + 2 == end || bios[i + 2] == 0))
                {
                    switch ((char)bios[i + 1])
                    {
                        case 'E':
                            return "SCEE";
                        case 'J':
                            return "SCEI";
                    }
                }
            }
        }

        return "SCEA";
    }

    private static ulong HashDisc(ICdImage disc)
    {
        // The volume descriptor and the boot path identify the game; the track layout separates revisions.
        var sector = new byte[ICdImage.SectorSize];
        disc.ReadSector(disc.Tracks[0].Start + 16, sector);
        var hash = Fnv(sector);
        hash = Fnv(Encoding.ASCII.GetBytes(PsDisc.BootPath(disc) ?? string.Empty), hash);
        foreach (var track in disc.Tracks)
        {
            hash = Fnv(BitConverter.GetBytes(track.Length), hash);
        }

        return hash;
    }

    private static ulong Fnv(ReadOnlySpan<byte> data, ulong hash = 0xCBF29CE484222325)
    {
        foreach (var b in data)
        {
            hash = (hash ^ b) * 0x100000001B3;
        }

        return hash;
    }

    // ---- Memory maps and achievements ----

    /// <summary>RAM at 0–$1FFFFF, then the scratchpad at $200000 (RetroAchievements' layout for the PlayStation, console 12).</summary>
    public byte PeekMemory(int address) => address switch
    {
        >= 0 and < RamSize => _ram[address],
        >= RamSize and < RamSize + 0x400 => _scratchpad[address - RamSize],
        _ => 0,
    };

    public byte ReadAchievementMemory(int address) => PeekMemory(address);

    // ---- Save states ----

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter(0x400000);
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
        writer.BeginChunk(Tag, 2);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_scratchpad);
        writer.WriteBytes(_io);
        writer.WriteUInt32(_interruptStatus);
        writer.WriteUInt32(_interruptMask);
        for (var i = 0; i < 7; i++)
        {
            writer.WriteUInt32(_dmaAddress[i]);
            writer.WriteUInt32(_dmaBlock[i]);
            writer.WriteUInt32(_dmaControl[i]);
        }

        writer.WriteUInt32(_dmaPriority);
        writer.WriteUInt32(_dmaInterrupt);
        writer.WriteInt64(_lastSync);
        writer.WriteInt64(_nextSync);
        writer.WriteInt32(_spuCycles);
        writer.WriteBool(_bootHook);
        writer.WriteInt32(_gpuTickFraction);
        writer.WriteInt32(_lineTick);
        writer.WriteInt32(_line);
        writer.WriteInt32(_dotFraction);
        writer.WriteBool(_inVBlank);
        writer.WriteBool(_gpuIrq);
        writer.WriteBool(Gpu.OddField);
        writer.WriteBool(Gpu.OddLine);
        writer.WriteInt32(_frameWidth);
        writer.WriteInt32(_frameHeight);
        Cpu.SaveState(writer);
        Gpu.SaveState(writer);
        Spu.SaveState(writer);
        Mdec.SaveState(writer);
        CdRom.SaveState(writer);
        Timers.SaveState(writer);
        Sio.SaveState(writer);
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
        for (var i = 0; i < 7; i++)
        {
            _dmaAddress[i] = reader.ReadUInt32();
            _dmaBlock[i] = reader.ReadUInt32();
            _dmaControl[i] = reader.ReadUInt32();
        }

        _dmaPriority = reader.ReadUInt32();
        _dmaInterrupt = reader.ReadUInt32();
        _lastSync = reader.ReadInt64();
        _nextSync = reader.ReadInt64();
        _spuCycles = reader.ReadInt32();
        _bootHook = reader.ReadBool();
        _gpuTickFraction = reader.ReadInt32();
        _lineTick = reader.ReadInt32();
        _line = reader.ReadInt32();
        _dotFraction = reader.ReadInt32();
        _inVBlank = reader.ReadBool();
        _gpuIrq = reader.ReadBool();
        Gpu.OddField = reader.ReadBool();
        Gpu.OddLine = reader.ReadBool();
        _frameWidth = Math.Clamp(reader.ReadInt32(), 1, MaxWidth);
        _frameHeight = Math.Clamp(reader.ReadInt32(), 1, MaxHeight);
        Cpu.LoadState(reader);
        Gpu.LoadState(reader);
        Spu.LoadState(reader);
        Mdec.LoadState(reader);
        CdRom.LoadState(reader);
        Timers.LoadState(reader);
        Sio.LoadState(reader);
        reader.EndChunk();
        UpdateInterruptLine();
    }
}
