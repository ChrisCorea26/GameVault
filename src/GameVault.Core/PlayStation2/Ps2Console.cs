using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.Input;
using GameVault.Core.Snes;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// What a PlayStation 2 boots: an ELF (homebrew, test programs) or a disc in the drive.
/// </summary>
/// <remarks>
/// Without a BIOS an ELF runs on GameVault's own EE kernel (<see cref="Ps2Kernel"/>) with the IOP's services answered in
/// C# (<see cref="Ps2IopServices"/>). With the player's BIOS it boots through the BIOS
/// (<see cref="Ps2ConsoleOptions.ElfThroughBios"/>). A disc needs the player's BIOS: the EE and IOP both run it.
/// </remarks>
public sealed class Ps2Boot
{
    private Ps2Boot(byte[]? elf, string path, string[] arguments, IPs2Disc? disc)
    {
        Elf = elf;
        Path = path;
        Arguments = arguments;
        Disc = disc;
    }

    /// <summary>The ELF to run, or null for a disc.</summary>
    public byte[]? Elf { get; }

    /// <summary>The program's name as argv[0] gives it (for example <c>host:test.elf</c>).</summary>
    public string Path { get; }

    /// <summary>argv[1] onwards.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The disc in the drive (also given to an ELF boot, if any).</summary>
    public IPs2Disc? Disc { get; }

    /// <summary>Boots an ELF (on GameVault's kernel, or through the BIOS when one is given); <paramref name="path"/> becomes argv[0].</summary>
    public static Ps2Boot FromElf(byte[] elf, string path = "host:program.elf", params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(elf);
        return new Ps2Boot(elf, path, arguments, null);
    }

    /// <summary>Boots a disc through the player's BIOS.</summary>
    public static Ps2Boot FromDisc(IPs2Disc disc)
    {
        ArgumentNullException.ThrowIfNull(disc);
        return new Ps2Boot(null, "cdrom0:", [], disc);
    }
}

/// <summary>
/// How a <see cref="Ps2Console"/> boots (Phase 17 plan, "Two ways to boot"). The platform sets it from Settings →
/// PlayStation 2.
/// </summary>
public sealed record Ps2ConsoleOptions
{
    /// <summary>ELFs through the BIOS when there is one; discs through the BIOS's start-up screens.</summary>
    public static Ps2ConsoleOptions Default { get; } = new();

    /// <summary>
    /// With the player's BIOS, an ELF boots through it (the default): the EE and the IOP run the BIOS until it starts its
    /// EE loader (EELOAD, at $82000), and the ELF is loaded and started in its place with ExecPS2, as PCSX2's fast boot
    /// does — so IOP modules the program loads run on the BIOS's IOP kernel. False runs ELFs on GameVault's kernel even
    /// when a BIOS is given. Without a BIOS, ELFs always run on GameVault's kernel.
    /// </summary>
    public bool ElfThroughBios { get; init; } = true;

    /// <summary>
    /// "Skip the startup screens" for discs: when the BIOS starts its EE loader, the disc's boot executable (SYSTEM.CNF's
    /// <c>BOOT2</c>) is read from the disc and started at once, instead of the BIOS's start-up screens running first. A
    /// disc without SYSTEM.CNF or BOOT2 boots normally. (An ELF booted through the BIOS is always started this way.)
    /// </summary>
    public bool FastBoot { get; init; }
}

/// <summary>
/// The PlayStation 2 (SPEC §7.11, Phase 17 plan §5): the Emotion Engine (the R5900 with VU0 as its COP2), VU1, the
/// DMAC with the VIFs, the GIF and the GS, the INTC and the EE timers, the SIF to the I/O processor (with its CDVD,
/// SIO2 pads and memory card and the SPU2), and 32 MB of RDRAM. Deterministic: everything runs on the EE's clock.
/// </summary>
/// <remarks>
/// <para>
/// Physical memory map (ps2tek: "EE Physical Memory Map", "EE Map"): RDRAM at $00000000; the EE registers at
/// $10000000 (timers, IPU $10002000, GIF $10003000, VIF0/VIF1 $10003800/$10003C00, the VIF0, VIF1, GIF and IPU FIFOs
/// at $10004000–$10007000, the DMAC $10008000–$1000EFFF, the INTC $1000F000, the SIO $1000F100, the SIF $1000F200,
/// the RDRAM controller $1000F430, D_ENABLER/W $1000F520/$1000F590); the VU memories at $11000000; the GS's privileged
/// registers at $12000000; the IOP's RAM at $1C000000; the BIOS at $1FC00000. Unmapped addresses read 0.
/// </para>
/// <para>
/// Clocks: the EE at 294.912 MHz, the bus (DMAC, VIF, GIF, timers) at half of it, the IOP at an eighth. The EE runs in
/// slices up to the next event (a line's H-blank, a timer, at most <see cref="MaxSlice"/> cycles); then the devices
/// catch up: VU1 (and VU0's micro programs) run for the same time, the DMAC and timers for the bus cycles, the IOP to
/// the same time. A frame is one field: it ends at V-blank's start, when the GS's picture is captured.
/// </para>
/// <para>
/// Without a BIOS, ELFs run on GameVault's kernel (<see cref="Kernel"/>) and the IOP's side of the SIF is answered by
/// <see cref="IopServices"/>; the IOP's CPU doesn't run. With a BIOS both processors start at its reset vector and run
/// it; an ELF (and a disc with <see cref="Ps2ConsoleOptions.FastBoot"/>) is started when the BIOS reaches its EE loader
/// (see <see cref="Ps2ConsoleOptions"/>).
/// </para>
/// </remarks>
public sealed partial class Ps2Console : IEmulatedConsole, IStateful
{
    public const int RdramSize = 32 * 1024 * 1024;
    public const int BiosSize = 4 * 1024 * 1024;

    /// <summary>The longest stretch the EE runs before the devices catch up (EE cycles).</summary>
    public const int MaxSlice = 2048;

    /// <summary>The stretch the EE runs while a DMA transfer is under way (EE cycles).</summary>
    public const int BusySlice = 64;

    /// <summary>The scratchpad's address in <see cref="PeekMemory"/>'s space (after RDRAM), as RetroAchievements sees it.</summary>
    public const int ScratchpadPeekAddress = 0x0200_0000;

    private static readonly StateTag Tag = StateTag.From("PS2 ");
    private static readonly StateTag SnapshotTag = StateTag.From("GVP2");

    private readonly byte[] _rdram = new byte[RdramSize];
    private readonly byte[]? _bios;
    private readonly Ps2Boot _boot;
    private readonly ushort[] _frame = new ushort[Ps2Gs.MaxFrameWidth * Ps2Gs.MaxFrameHeight];
    private int _frameWidth = 640;
    private int _frameHeight = 448;
    private bool _frameDone;

    /// <param name="boot">The ELF or disc to boot.</param>
    /// <param name="bios">The player's 4 MB BIOS, or null: ELFs then run on GameVault's kernel (discs need one).</param>
    /// <param name="card">The memory card in slot 1; a new formatted card when null.</param>
    /// <exception cref="ArgumentException">A disc boot without a BIOS, or a BIOS of the wrong size.</exception>
    /// <exception cref="InvalidDataException">The ELF isn't a MIPS executable.</exception>
    public Ps2Console(Ps2Boot boot, byte[]? bios = null, Ps2MemoryCard? card = null)
        : this(boot, bios, card, Ps2ConsoleOptions.Default)
    {
    }

    /// <param name="boot">The ELF or disc to boot.</param>
    /// <param name="bios">The player's 4 MB BIOS, or null: ELFs then run on GameVault's kernel (discs need one).</param>
    /// <param name="card">The memory card in slot 1; a new formatted card when null.</param>
    /// <param name="options">How to boot: an ELF through the BIOS or on GameVault's kernel, a disc's fast boot.</param>
    /// <exception cref="ArgumentException">A disc boot without a BIOS, or a BIOS of the wrong size.</exception>
    /// <exception cref="InvalidDataException">The ELF isn't a MIPS executable.</exception>
    public Ps2Console(Ps2Boot boot, byte[]? bios, Ps2MemoryCard? card, Ps2ConsoleOptions options)
    {
        ArgumentNullException.ThrowIfNull(boot);
        ArgumentNullException.ThrowIfNull(options);
        if (bios is not null && bios.Length != BiosSize)
        {
            throw new ArgumentException("The PlayStation 2 BIOS is 4 MB.", nameof(bios));
        }

        if (boot.Elf is null && bios is null)
        {
            throw new ArgumentException("A PlayStation 2 disc needs the player's BIOS.", nameof(bios));
        }

        _boot = boot;
        Options = options;
        _bios = boot.Elf is null || options.ElfThroughBios ? bios : null;
        Elf = boot.Elf is { } elf ? Ps2Elf.Parse(elf) : null;

        Intc = new Ps2Intc();
        Timers = new Ps2EeTimers(Intc);
        Sif = new Ps2Sif();
        Gs = new Ps2Gs { InterruptRaised = () => Intc.Raise(Ps2Interrupt.Gs) };
        Gif = new Ps2Gif(Gs);
        Vu0 = new Ps2Vu(0, this);
        Vu1 = new Ps2Vu(1, this);
        Vif0 = new Ps2Vif(0, Vu0, Intc);
        Vif1 = new Ps2Vif(1, Vu1, Intc, Gif);
        Cpu = new R5900Cpu(this, Vu0);
        Dmac = new Ps2Dmac(_rdram, Cpu.Scratchpad, Vif0, Vif1, Gif, Sif, Intc);
        Ipu = new Ps2Ipu(Intc);
        Dmac.Ipu = Ipu;
        Spu2 = new Ps2Spu2 { SampleReady = OnSample };
        Iop = new Ps2Iop(_bios, Sif, Spu2)
        {
            Disc = boot.Disc,
            MemoryCard = card ?? new Ps2MemoryCard(),
            DebugOutput = c => DebugOutput?.Invoke(c),
        };

        if (Elf is not null && _bios is null)
        {
            Kernel = new Ps2Kernel(this);
            IopServices = new Ps2IopServices(this);
        }

        RomHash = boot.Elf is { } bytes ? Fnv(bytes) : HashDisc(boot.Disc!);
        Power();
    }

    public GameSystem System => GameSystem.PlayStation2;

    /// <summary>How this machine boots.</summary>
    public Ps2ConsoleOptions Options { get; }

    /// <summary>The BIOS runs (a disc, or an ELF booted through it): both processors started at its reset vector.</summary>
    public bool BootsThroughBios => _bios is not null;

    public R5900Cpu Cpu { get; }

    public Ps2Vu Vu0 { get; }

    public Ps2Vu Vu1 { get; }

    public Ps2Vif Vif0 { get; }

    public Ps2Vif Vif1 { get; }

    public Ps2Gs Gs { get; }

    public Ps2Gif Gif { get; }

    public Ps2Dmac Dmac { get; }

    public Ps2Intc Intc { get; }

    public Ps2EeTimers Timers { get; }

    public Ps2Sif Sif { get; }

    public Ps2Iop Iop { get; }

    public Ps2Spu2 Spu2 { get; }

    /// <summary>GameVault's EE kernel, when an ELF boots without the BIOS (null otherwise).</summary>
    public Ps2Kernel? Kernel { get; }

    /// <summary>The IOP's services answered in C#, when an ELF boots without the BIOS (null otherwise).</summary>
    public Ps2IopServices? IopServices { get; }

    /// <summary>The program booted, when it is an ELF.</summary>
    public Ps2Elf? Elf { get; }

    /// <summary>The 32 MB of RDRAM.</summary>
    public byte[] Rdram => _rdram;

    /// <summary>The memory card in slot 1, which the platform saves (it isn't battery-backed cartridge RAM).</summary>
    public Ps2MemoryCard MemoryCard => Iop.MemoryCard!;

    /// <summary>
    /// The memory card in slot 2, or null for an empty slot. GameVault's app puts one shared card there, the same for
    /// every game; set it before <see cref="Power"/>.
    /// </summary>
    public Ps2MemoryCard? MemoryCard2
    {
        get => Iop.MemoryCard2;
        set => Iop.MemoryCard2 = value;
    }

    /// <summary>
    /// Text the program prints: the EE's debug UART (KPUTCHAR at $1000F180), the IOP's console output, and — on
    /// GameVault's kernel — the <c>fileio</c> server's writes to stdout and Deci2Call's TTY. Null while unused.
    /// </summary>
    public Action<char>? DebugOutput { get; set; }

    /// <summary>The program ended (Exit, or its last thread exited, on GameVault's kernel): the EE stops.</summary>
    public bool Exited { get; internal set; }

    /// <summary>The program's exit code, when it ended through Exit.</summary>
    public int ExitCode { get; internal set; }

    public ulong RomHash { get; }

    public int ScreenWidth => _frameWidth;

    public int ScreenHeight => _frameHeight;

    /// <summary>Every picture fills a 4:3 screen.</summary>
    public double PixelAspect => 4.0 / 3.0 / ((double)_frameWidth / _frameHeight);

    public ReadOnlySpan<ushort> FrameBuffer => _frame.AsSpan(0, _frameWidth * _frameHeight);

    /// <summary>The frame holds BGR555 colours, the same layout as the SNES's (red in the low bits).</summary>
    public ReadOnlySpan<uint> Palette => SnesPpu.Palette;

    public long FrameCount { get; private set; }

    /// <summary>Audio is stamped in SPU2 samples (48 kHz).</summary>
    public double CpuClockRate => Ps2Spu2.SampleRate;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    /// <summary>The PS2 has no battery-backed cartridge RAM; the memory card is saved through <see cref="MemoryCard"/>.</summary>
    public IBatteryBacked? Battery => null;

    /// <summary>The IPU (the MPEG decoder): its registers at $10002000 and its FIFOs at $10007000.</summary>
    public Ps2Ipu Ipu { get; }

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate) { ApplyNesFilters = false };
        return Audio;
    }

    // ---- Power and reset ----

    public void Power()
    {
        Array.Clear(_rdram);
        Array.Clear(Cpu.Scratchpad);
        Array.Clear(Vu0.MicroMemory);
        Array.Clear(Vu0.DataMemory);
        Array.Clear(Vu1.MicroMemory);
        Array.Clear(Vu1.DataMemory);
        FrameCount = 0;
        Reset();
    }

    /// <summary>
    /// The reset: every device to its power-on state, then the boot — the BIOS from its reset vector, or the ELF on
    /// GameVault's kernel (which sets up the TLB, the DMAC and the GS as the BIOS would leave them).
    /// </summary>
    public void Reset()
    {
        Cpu.Reset();
        Cpu.Cycles = 0;
        Intc.Reset();
        Timers.Reset();
        Sif.Reset();
        Gs.Reset();
        Gif.Reset();
        Vu0.Reset();
        Vu1.Reset();
        Vif0.Reset();
        Vif1.Reset();
        Dmac.Reset();
        Iop.Reset();
        Ipu.Reset();
        Exited = false;
        ExitCode = 0;
        ResetIo();
        ResetScheduler();

        if (Kernel is not null)
        {
            IopServices!.Reset();
            Kernel.Boot(Elf!, _boot.Path, _boot.Arguments);
        }

        ArmBootHook();

        UpdateInterruptLines();
    }

    // ---- Running ----

    /// <summary>
    /// The VUs' and the EE FPU's add and multiply in the host's own float arithmetic instead of bit-exactly
    /// (ps2-full-speed.md §2): several times faster on transform-heavy code, differing from the hardware in the last bit
    /// of some results. Off by default; the host turns it on for normal play.
    /// </summary>
    public bool FastFloat
    {
        get => Cpu.FastFloat;
        set
        {
            Cpu.FastFloat = value;
            Vu0.FastFloat = value;
            Vu1.FastFloat = value;
        }
    }

    public void RunFrame()
    {
        _frameDone = false;
        var guard = Cpu.Cycles + R5900Cpu.ClockRate / 10; // a field never takes longer than a tenth of a second
        while (!_frameDone && Cpu.Cycles < guard)
        {
            RunSlice(guard);
        }

        if (!_frameDone)
        {
            CaptureFrame();
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

    /// <summary>Runs the machine for <paramref name="cycles"/> more EE cycles (tests).</summary>
    public void RunCycles(long cycles)
    {
        var end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            RunSlice(end);
        }
    }

    /// <summary>Runs the EE up to the next event (or <paramref name="limit"/>), then lets the devices catch up.</summary>
    private void RunSlice(long limit)
    {
        var target = Math.Min(limit, NextEvent());
        if (Exited)
        {
            Cpu.Cycles = Math.Max(Cpu.Cycles, target); // the program ended: time still passes for the devices
        }
        else
        {
            Cpu.Run(target);
            if (Cpu.AtBreakpoint)
            {
                EeLoaderReached();
            }
        }

        Sync();
    }

    // ---- Input ----

    /// <summary>The DualShock 2 in <paramref name="port"/> (0 or 1): buttons by position, both sticks, L3 and R3.</summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if ((uint)port < 2)
        {
            Iop.SetControllerState(port, state);
        }
    }

    // ---- Memory for memory maps and achievements ----

    /// <summary>RDRAM by physical address, then the 16 KB scratchpad at <see cref="ScratchpadPeekAddress"/>.</summary>
    public byte PeekMemory(int address) => address switch
    {
        >= 0 and < RdramSize => _rdram[address],
        >= ScratchpadPeekAddress and < ScratchpadPeekAddress + R5900Cpu.ScratchpadSize => Cpu.Scratchpad[address - ScratchpadPeekAddress],
        _ => 0,
    };

    // ---- Audio ----

    private int _samples;
    private float _lastLeft;
    private float _lastRight;

    internal void OnSample(short left, short right)
    {
        if (Audio is not { } audio)
        {
            return;
        }

        var l = left * (0.9f / 32768);
        var r = right * (0.9f / 32768);
        if (l != _lastLeft || r != _lastRight)
        {
            audio.AddDelta(_samples, l - _lastLeft, r - _lastRight);
            _lastLeft = l;
            _lastRight = r;
        }

        _samples++;
    }

    // ---- Video ----

    private void CaptureFrame()
    {
        Gs.RenderFrame(_frame, out _frameWidth, out _frameHeight);
        _frameDone = true;
    }

    // ---- Snapshots ----

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter(0x400_0000);
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

    private static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>Identifies a disc by its first 32 sectors (the volume descriptors) and its size.</summary>
    private static ulong HashDisc(IPs2Disc disc)
    {
        var sector = new byte[IPs2Disc.SectorSize];
        var hash = 14695981039346656037UL ^ (ulong)disc.SectorCount;
        for (var lba = 0; lba < 32; lba++)
        {
            disc.ReadSector(lba, sector);
            foreach (var b in sector)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }
        }

        return hash;
    }
}
