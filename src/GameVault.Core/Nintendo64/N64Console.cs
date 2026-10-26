using System.Buffers.Binary;
using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.R4300;
using GameVault.Core.Input;
using GameVault.Core.Snes;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The Nintendo 64 (SPEC §7.11, Phase 16): the VR4300 with 4 or 8 MB of RDRAM; the RCP's signal processor (RSP) and
/// display processor (RDP); the video, audio, peripheral and serial interfaces; and the PIF with the controllers.
/// Deterministic, like every core: everything runs on the CPU clock.
/// </summary>
/// <remarks>
/// Physical memory map (n64brew: "Memory map"): RDRAM at $00000000, its registers at $03F00000, the RSP's DMEM and
/// IMEM at $04000000, then the RCP's registers (SP $04040000, DP $04100000, MI $04300000, VI $04400000,
/// AI $04500000, PI $04600000, RI $04700000, SI $04800000), cartridge SRAM/FlashRAM at $08000000, the ROM at
/// $10000000 and the PIF's RAM at $1FC007C0.
/// The PIF's boot ROM is simulated: the cartridge's boot code (IPL3) is copied to DMEM and started with the registers
/// it expects, so no Nintendo boot code is needed. Devices catch up lazily: the CPU runs to the next event (a video
/// line, a DMA's end, an audio buffer's end, a slice of the RSP), and a device catches up before its registers are
/// touched.
/// </remarks>
public sealed partial class N64Console : IEmulatedConsole, IR4300Bus, IN64RcpHost
{
    public const double CpuClock = 93_750_000.0;
    public const int MaxWidth = 640;
    public const int MaxHeight = 480;

    /// <summary>The VI's clock in each TV system (n64brew: "Video Interface").</summary>
    public const double NtscViClock = 48_681_812.0;
    public const double PalViClock = 49_656_530.0;
    public const double MpalViClock = 48_628_316.0;

    /// <summary>Audio is stamped in units of 64 CPU cycles (1.46 MHz), fine enough for any DAC rate.</summary>
    private const int AudioClockDivider = 64;

    private static readonly StateTag Tag = StateTag.From("N64 ");

    private readonly byte[] _rdram;
    private readonly ushort[] _frame = new ushort[MaxWidth * MaxHeight];
    private int _frameWidth = 320;
    private int _frameHeight = 240;
    private bool _frameDone;

    private long _nextEvent;
    private long _frameStartCycle;
    private long _rspSyncedCycle;

    public N64Console(N64Cartridge cartridge, bool expansionPak = true, N64ControllerPak? controllerPak = null)
    {
        Cartridge = cartridge;
        ExpansionPak = expansionPak;
        _rdram = new byte[expansionPak ? 8 * 1024 * 1024 : 4 * 1024 * 1024];
        Rdp = new N64Rdp(this);
        Rsp = new N64Rsp(this, Rdp);
        Pif = new N64Pif(cartridge, controllerPak ?? new N64ControllerPak());
        Cpu = new R4300Cpu(this);
        ViClock = cartridge.Region switch
        {
            N64Region.Pal => PalViClock,
            N64Region.Mpal => MpalViClock,
            _ => NtscViClock,
        };
        RomHash = HashRom(cartridge.Rom);
    }

    public GameSystem System => GameSystem.Nintendo64;

    public R4300Cpu Cpu { get; }

    public N64Rsp Rsp { get; }

    public N64Rdp Rdp { get; }

    public N64Pif Pif { get; }

    public N64Cartridge Cartridge { get; }

    /// <summary>8 MB of RDRAM instead of 4 (the Expansion Pak).</summary>
    public bool ExpansionPak { get; }

    /// <summary>The VI clock of the cartridge's TV system, which times lines and audio.</summary>
    public double ViClock { get; }

    public byte[] Rdram => _rdram;

    Span<byte> IN64RcpHost.Rdram => _rdram;

    Span<byte> IN64RcpHost.Dmem => Rsp.Dmem;

    public ulong RomHash { get; }

    public int ScreenWidth => _frameWidth;

    public int ScreenHeight => _frameHeight;

    /// <summary>Every picture fills a 4:3 screen.</summary>
    public double PixelAspect => 4.0 / 3.0 / ((double)_frameWidth / _frameHeight);

    public ReadOnlySpan<ushort> FrameBuffer => _frame.AsSpan(0, _frameWidth * _frameHeight);

    /// <summary>The frame holds BGR555 colours, the same layout as the SNES's.</summary>
    public ReadOnlySpan<uint> Palette => SnesPpu.Palette;

    public long FrameCount { get; private set; }

    public double CpuClockRate => CpuClock / AudioClockDivider;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 4;

    public IBatteryBacked? Battery => Cartridge.HasBattery ? Cartridge : null;

    /// <summary>The Controller Pak in the first controller, saved separately from the cartridge.</summary>
    public N64ControllerPak ControllerPak => Pif.Pak!;

    /// <summary>
    /// Text homebrew prints through the IS-Viewer debug port at $13FF0000 (libdragon's debugf). While this is null
    /// the port is absent, as on a real console.
    /// </summary>
    public Action<char>? DebugOutput { get; set; }

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate) { ApplyNesFilters = false };
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_rdram);
        Array.Clear(Rsp.Dmem);
        Array.Clear(Rsp.Imem);
        FrameCount = 0;
        Reset();
    }

    /// <summary>
    /// The reset button, then the PIF's boot (n64brew: "PIF-NUS", "IPL3"): the boot code is copied to DMEM, RDRAM's
    /// interface is left initialized (so IPL3 skips its memory setup), the registers are set as IPL2 leaves them, and
    /// the CPU starts at $A4000040.
    /// </summary>
    public void Reset()
    {
        Cpu.Reset();
        Cpu.Cycles = 0;
        ResetIo();
        Pif.Reset();
        _rspSyncedCycle = 0;
        _frameStartCycle = 0;
        _nextEvent = 0;

        Cartridge.Rom.AsSpan(0, N64Cartridge.BootCodeEnd).CopyTo(Rsp.Dmem);

        // libultra's osMemSize, which a cold boot's IPL3 would have stored (6105 boot code keeps it at $3F0).
        BinaryPrimitives.WriteUInt32BigEndian(_rdram.AsSpan(0x318), (uint)_rdram.Length);
        BinaryPrimitives.WriteUInt32BigEndian(_rdram.AsSpan(0x3F0), (uint)_rdram.Length);

        Cpu.SetRegister(19, 0); // s3: the boot device, the cartridge
        Cpu.SetRegister(20, Cartridge.Region switch { N64Region.Pal => 0UL, N64Region.Mpal => 2UL, _ => 1UL }); // s4: TV type
        Cpu.SetRegister(21, 0); // s5: cold reset
        Cpu.SetRegister(22, N64Cartridge.Seed(Cartridge.Cic)); // s6: the CIC's seed
        Cpu.SetRegister(23, 0); // s7: the PIF's version
        Cpu.SetRegister(11, 0xFFFF_FFFF_A400_0040); // t3
        Cpu.SetRegister(29, 0xFFFF_FFFF_A400_1FF0); // sp
        Cpu.SetRegister(31, 0xFFFF_FFFF_A400_1550); // ra
        Cpu.WriteCop0Register(12, 0x3400_0000); // Status: CU0, CU1, FR
        Cpu.WriteCop0Register(16, 0x0006_E463); // Config
        Cpu.Jump(0xFFFF_FFFF_A400_0040);
        ScheduleVi();
        UpdateNextEvent();
    }

    // ---- Running ----

    public void RunFrame()
    {
        _frameDone = false;
        var guard = Cpu.Cycles + (long)(CpuClock / 10); // a frame never takes longer than a tenth of a second
        while (!_frameDone && Cpu.Cycles < guard)
        {
            Cpu.Run(Math.Min(_nextEvent, guard));
            Sync();
        }

        if (!_frameDone)
        {
            CaptureFrame(); // the VI is stopped: show what's there
        }

        EndAudioFrame();
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

    /// <summary>Runs the CPU for <paramref name="cycles"/> more cycles, with the devices (tests).</summary>
    public void RunCycles(long cycles)
    {
        var end = Cpu.Cycles + cycles;
        while (Cpu.Cycles < end)
        {
            Cpu.Run(Math.Min(end, _nextEvent));
            Sync();
        }
    }

    /// <summary>Brings every device up to the CPU's time, handles the events that are due, and finds the next one.</summary>
    private void Sync()
    {
        var now = Cpu.Cycles;
        CatchUpRsp();
        if (_piBusy && now >= _piDoneCycle)
        {
            PiDmaDone();
        }

        if (_siBusy && now >= _siDoneCycle)
        {
            SiDmaDone();
        }

        CatchUpAudio(now);
        while (now >= _viNextLineCycle)
        {
            ViLine();
        }

        UpdateNextEvent();
    }

    private void UpdateNextEvent()
    {
        var next = _viNextLineCycle;
        if (_piBusy)
        {
            next = Math.Min(next, _piDoneCycle);
        }

        if (_siBusy)
        {
            next = Math.Min(next, _siDoneCycle);
        }

        if (_aiCount > 0)
        {
            next = Math.Min(next, _aiBufferEndCycle);
        }

        if (!Rsp.Halted)
        {
            next = Math.Min(next, Cpu.Cycles + RspSlice);
        }

        _nextEvent = Math.Max(next, Cpu.Cycles + 1);
    }

    /// <summary>How far the CPU runs ahead of a running RSP before it catches up (in CPU cycles).</summary>
    private const int RspSlice = 1500;

    /// <summary>Runs the RSP up to the CPU's time: the RCP clock is two thirds of the CPU's.</summary>
    private void CatchUpRsp()
    {
        var now = Cpu.Cycles;
        if (!Rsp.Halted && now > _rspSyncedCycle)
        {
            var cycles = (int)(now * 2 / 3 - _rspSyncedCycle * 2 / 3);
            if (cycles > 0)
            {
                Rsp.Run(cycles);
            }
        }

        _rspSyncedCycle = now;
    }

    // ---- Interrupts (the MIPS Interface) ----

    private uint _miMode;
    private uint _miInterrupts;
    private uint _miMask;

    public void RaiseInterrupt(N64Interrupt source)
    {
        _miInterrupts |= (uint)source;
        Cpu.InterruptLine = (_miInterrupts & _miMask) != 0;
    }

    public void ClearInterrupt(N64Interrupt source)
    {
        _miInterrupts &= ~(uint)source;
        Cpu.InterruptLine = (_miInterrupts & _miMask) != 0;
    }

    // ---- Video ----

    /// <summary>
    /// Turns the VI's picture into the frame: the framebuffer at VI_ORIGIN, VI_WIDTH pixels a line, over the area
    /// H_VIDEO and V_VIDEO show at the X and Y scales (n64brew: "Video Interface"). 16-bit pixels are RGBA5551;
    /// 32-bit pixels are rounded to 15 bits. The VI's anti-aliasing and divot filters are left for later.
    /// </summary>
    private void CaptureFrame()
    {
        var type = _vi[ViControl] & 3;
        var hStart = (int)(_vi[ViHVideo] >> 16) & 0x3FF;
        var hEnd = (int)_vi[ViHVideo] & 0x3FF;
        var vStart = (int)(_vi[ViVVideo] >> 16) & 0x3FF;
        var vEnd = (int)_vi[ViVVideo] & 0x3FF;
        var xScale = (int)_vi[ViXScale] & 0xFFF;
        var yScale = (int)_vi[ViYScale] & 0xFFF;
        var width = Math.Clamp((hEnd - hStart) * xScale / 1024, 0, MaxWidth);
        var height = Math.Clamp((vEnd - vStart) / 2 * yScale / 1024, 0, MaxHeight);
        if (type < 2 || width < 8 || height < 8)
        {
            _frame.AsSpan(0, _frameWidth * _frameHeight).Clear(); // blanked
            return;
        }

        _frameWidth = width;
        _frameHeight = height;
        var origin = (int)(_vi[ViOrigin] & 0xFF_FFFF);
        var stride = (int)(_vi[ViWidth] & 0xFFF);
        var bytes = type == 3 ? 4 : 2;
        var rdram = _rdram;
        for (var y = 0; y < height; y++)
        {
            var line = _frame.AsSpan(y * width, width);
            var address = origin + y * stride * bytes;
            for (var x = 0; x < width; x++, address += bytes)
            {
                if ((uint)(address + bytes) > (uint)rdram.Length)
                {
                    line[x] = 0;
                    continue;
                }

                if (bytes == 2)
                {
                    var pixel = (rdram[address] << 8) | rdram[address + 1];
                    line[x] = (ushort)(((pixel >> 11) & 0x1F) | (((pixel >> 6) & 0x1F) << 5) | (((pixel >> 1) & 0x1F) << 10));
                }
                else
                {
                    line[x] = (ushort)((rdram[address] >> 3) | ((rdram[address + 1] >> 3) << 5) | ((rdram[address + 2] >> 3) << 10));
                }
            }
        }
    }

    // ---- Input ----

    /// <summary>
    /// The N64 pad by position (docs/design/input.md): A is B (south), B is Y (west), Z is either trigger (L2/R2), C-down
    /// is A (east), C-left is X (north), C-up is C and C-right is Z; L, R, Start and the D-pad are themselves; the stick
    /// is the state's analog stick.
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if ((uint)port >= 4)
        {
            return;
        }

        var buttons = N64Buttons.None;
        if ((state & ControllerState.B) != 0) buttons |= N64Buttons.A;
        if ((state & ControllerState.Y) != 0) buttons |= N64Buttons.B;
        if ((state & (ControllerState.L2 | ControllerState.R2)) != 0) buttons |= N64Buttons.Z;
        if ((state & ControllerState.Start) != 0) buttons |= N64Buttons.Start;
        if ((state & ControllerState.Up) != 0) buttons |= N64Buttons.Up;
        if ((state & ControllerState.Down) != 0) buttons |= N64Buttons.Down;
        if ((state & ControllerState.Left) != 0) buttons |= N64Buttons.Left;
        if ((state & ControllerState.Right) != 0) buttons |= N64Buttons.Right;
        if ((state & ControllerState.L) != 0) buttons |= N64Buttons.L;
        if ((state & ControllerState.R) != 0) buttons |= N64Buttons.R;
        if ((state & ControllerState.A) != 0) buttons |= N64Buttons.CDown;
        if ((state & ControllerState.X) != 0) buttons |= N64Buttons.CLeft;
        if ((state & ControllerState.C) != 0) buttons |= N64Buttons.CUp;
        if ((state & ControllerState.Z) != 0) buttons |= N64Buttons.CRight;
        Pif.SetPad(port, buttons, state.StickX(), state.StickY());
    }

    // ---- Memory for memory maps and achievements ----

    /// <summary>RDRAM, by address (big-endian), for memory maps.</summary>
    public byte PeekMemory(int address) => (uint)address < (uint)_rdram.Length ? _rdram[address] : (byte)0;

    /// <summary>
    /// RDRAM as RetroAchievements addresses it: the bytes of each 32-bit word in little-endian order, as the emulators
    /// its sets were made with keep them.
    /// </summary>
    public byte ReadAchievementMemory(int address) => PeekMemory(address ^ 3);

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_rdram);
        writer.WriteBytes(Rsp.Dmem);
        writer.WriteBytes(Rsp.Imem);
        writer.WriteBytes(Cartridge.Save);
        writer.WriteInt64(_frameStartCycle);
        writer.WriteInt64(_rspSyncedCycle);
        writer.WriteInt64(FrameCount);
        writer.WriteInt32(_frameWidth);
        writer.WriteInt32(_frameHeight);
        writer.WriteUInt32(_miMode);
        writer.WriteUInt32(_miInterrupts);
        writer.WriteUInt32(_miMask);
        SaveIo(writer);
        writer.EndChunk();
        Cpu.SaveState(writer);
        Rsp.SaveState(writer);
        Rdp.SaveState(writer);
        Pif.SaveState(writer);
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_rdram);
        reader.ReadBytes(Rsp.Dmem);
        reader.ReadBytes(Rsp.Imem);
        reader.ReadBytes(Cartridge.Save);
        _frameStartCycle = reader.ReadInt64();
        _rspSyncedCycle = reader.ReadInt64();
        FrameCount = reader.ReadInt64();
        _frameWidth = reader.ReadInt32();
        _frameHeight = reader.ReadInt32();
        _miMode = reader.ReadUInt32();
        _miInterrupts = reader.ReadUInt32();
        _miMask = reader.ReadUInt32();
        LoadIo(reader);
        reader.EndChunk();
        Cpu.LoadState(reader);
        Rsp.LoadState(reader);
        Rdp.LoadState(reader);
        Pif.LoadState(reader);
        Cpu.InterruptLine = (_miInterrupts & _miMask) != 0;
        UpdateNextEvent();
        CaptureFrame();
    }

    private static readonly StateTag SnapshotTag = StateTag.From("GV64");

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter(0x900000);
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

    /// <summary>FNV-1a over the ROM: identifies the game a state belongs to.</summary>
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
