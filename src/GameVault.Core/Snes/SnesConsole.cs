using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.W65816;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Snes;

/// <summary>
/// The Super Nintendo / Super Famicom (SPEC §7.11, Phase 14): the 65C816 on the 21.477 MHz master clock, 128 KB of
/// WRAM, the S-PPU, the sound module, DMA and HDMA, the multiplier and divider, H/V timers and two pads with
/// auto-read. Deterministic, like every core.
/// </summary>
/// <remarks>
/// Each CPU access costs 6, 8 or 12 master clocks by address (FastROM makes banks $80–$FF 6), internal cycles 6, and
/// a line is 1,364 clocks (NTSC 262 lines, PAL 312) with 40 for DRAM refresh. Visible lines are drawn as they start;
/// HDMA runs in each visible line's H-blank. The sound module catches up whenever the CPU touches its ports and at
/// the end of each frame. References: fullsnes; anomie's timing and DMA docs.
/// </remarks>
public sealed class SnesConsole : IEmulatedConsole, IW65816Bus
{
    public const double NtscMasterClock = 21477272.0;
    public const double PalMasterClock = 21281370.0;
    public const int ClocksPerLine = 1364;
    public const double AudioRate = 32040.0;
    private const int HdmaClock = 1104;  // dot 276
    private const int HBlankClock = 1096; // dot 274

    private static readonly StateTag Tag = StateTag.From("SNES");
    private static readonly StateTag SnapshotTag = StateTag.From("GVSF");
    private static readonly byte[][] Patterns =
    [
        [0], [0, 1], [0, 0], [0, 0, 1, 1], [0, 1, 2, 3], [0, 1, 0, 1], [0, 0], [0, 0, 1, 1],
    ];

    private readonly byte[] _wram = new byte[0x20000];
    private readonly byte[] _dma = new byte[0x80];
    private readonly bool[] _hdmaDoTransfer = new bool[8];
    private readonly bool[] _hdmaTerminated = new bool[8];
    private readonly ControllerState[] _pads = new ControllerState[2];
    private readonly ushort[] _joypads = new ushort[4];
    private readonly int[] _serial = new int[2];
    private readonly ISnesCoprocessor? _coprocessor;

    private long _clock;        // master clocks since the frame started
    private long _apuClock;     // master clocks the sound module has caught up to
    private int _line;
    private bool _hdmaDone;
    private bool _irqFiredThisLine;
    private byte _openBus;
    private byte _nmitimen;
    private byte _wrio = 0xFF;
    private byte _wrmpya = 0xFF;
    private ushort _dividend = 0xFFFF;
    private ushort _quotient;
    private ushort _product;
    private ushort _htime = 0x1FF;
    private ushort _vtime = 0x1FF;
    private byte _hdmaEnabled;
    private bool _fastRom;
    private bool _nmiFlag;
    private bool _irqFlag;
    private bool _autoReadBusy;
    private bool _joypadLatch;
    private int _wramAddress;
    private int _samples;
    private float _lastSample;

    public SnesConsole(SnesCartridge cartridge, bool? pal = null)
    {
        Cartridge = cartridge;
        Pal = pal ?? cartridge.Pal;
        Cpu = new W65816Cpu(this);
        Ppu = new SnesPpu(Pal);
        Apu = new SnesApu();
        Apu.Dsp.SampleReady = OnSample;
        RomHash = HashRom(cartridge.Rom);
        _coprocessor = cartridge.Coprocessor;
    }

    public GameSystem System => GameSystem.Snes;

    public bool Pal { get; }

    public W65816Cpu Cpu { get; }

    public SnesPpu Ppu { get; }

    public SnesApu Apu { get; }

    public SnesCartridge Cartridge { get; }

    public ulong RomHash { get; }

    public int ScreenWidth => Ppu.FrameWidth;

    public int ScreenHeight => Ppu.FrameHeight;

    /// <summary>The picture fills a 4:3 screen: 256×224 pixels are a little wider than square, 512-wide ones narrower.</summary>
    public double PixelAspect => 4.0 / 3.0 / ((double)Ppu.FrameWidth / Ppu.FrameHeight);

    public ReadOnlySpan<ushort> FrameBuffer => Ppu.Frame;

    public ReadOnlySpan<uint> Palette => SnesPpu.Palette;

    public long FrameCount { get; private set; }

    /// <summary>Audio is stamped in DSP samples (32,040 Hz).</summary>
    public double CpuClockRate => AudioRate;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    public IBatteryBacked? Battery => Cartridge.HasBattery ? Cartridge : null;

    public ReadOnlySpan<byte> Wram => _wram;

    private int LinesPerFrame => Pal ? 312 : 262;

    private int VBlankLine => Ppu.Overscan ? 240 : 225;

    private double MasterClock => Pal ? PalMasterClock : NtscMasterClock;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(AudioRate, sampleRate) { ApplyNesFilters = false };
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_wram);
        Array.Clear(_dma);
        Ppu.Power();
        Apu.Power();
        _coprocessor?.Power();
        ResetState();
        FrameCount = 0;
    }

    public void Reset()
    {
        Apu.Reset();
        _coprocessor?.Reset();
        ResetState();
    }

    private void ResetState()
    {
        _clock = _apuClock = 0;
        _line = 0;
        _nmitimen = 0;
        _hdmaEnabled = 0;
        _fastRom = false;
        _nmiFlag = _irqFlag = false;
        _wramAddress = 0;
        _samples = 0;
        Array.Clear(_hdmaTerminated);
        Ppu.BeginFrame();
        Cpu.Reset();
    }

    /// <summary>The SNES pad: B, Y, Select, Start, the D-pad, A, X, L and R, from the matching <see cref="ControllerState"/> buttons.</summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if (port is 0 or 1)
        {
            _pads[port] = state;
        }
    }

    /// <summary>The 16 bits a pad shifts out, first bit in bit 15: B Y Select Start Up Down Left Right A X L R 0 0 0 0.</summary>
    private static ushort PadBits(ControllerState s)
    {
        int Bit(ControllerState button, int bit) => (s & button) != 0 ? 1 << bit : 0;
        return (ushort)(Bit(ControllerState.B, 15) | Bit(ControllerState.Y, 14) | Bit(ControllerState.Select, 13) | Bit(ControllerState.Start, 12)
            | Bit(ControllerState.Up, 11) | Bit(ControllerState.Down, 10) | Bit(ControllerState.Left, 9) | Bit(ControllerState.Right, 8)
            | Bit(ControllerState.A, 7) | Bit(ControllerState.X, 6) | Bit(ControllerState.L, 5) | Bit(ControllerState.R, 4));
    }

    // ---- Frames ----

    public void RunFrame()
    {
        Ppu.BeginFrame();
        var lines = LinesPerFrame;
        for (_line = 0; _line < lines; _line++)
        {
            RunLine();
        }

        CatchUpApu();
        if (_coprocessor is { } chip)
        {
            chip.CatchUp(_clock);
            chip.EndFrame((long)lines * ClocksPerLine);
        }

        _clock -= (long)lines * ClocksPerLine;
        _apuClock -= (long)lines * ClocksPerLine;
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

    private void RunLine()
    {
        var start = (long)_line * ClocksPerLine;
        var vblank = VBlankLine;
        _hdmaDone = false;
        _irqFiredThisLine = false;
        if (_line == 0)
        {
            _nmiFlag = false;
            InitHdma();
        }
        else if (_line < vblank)
        {
            Ppu.RenderLine(_line - 1);
        }
        else if (_line == vblank)
        {
            Ppu.FinishFrame();
            Ppu.BeginVBlank();
            _nmiFlag = true;
            if ((_nmitimen & 0x80) != 0)
            {
                Cpu.Nmi = true;
            }

            if ((_nmitimen & 1) != 0)
            {
                _joypads[0] = PadBits(_pads[0]);
                _joypads[1] = PadBits(_pads[1]);
                _autoReadBusy = true;
            }
        }
        else if (_line == vblank + 3)
        {
            _autoReadBusy = false;
        }

        // DRAM refresh steals 40 clocks of every line.
        _clock = Math.Max(_clock, start) + 40;
        var end = start + ClocksPerLine;
        while (_clock < end)
        {
            CheckIrq(start);
            if (_coprocessor is { } chip)
            {
                // The chip runs alongside the CPU; its interrupt (the Super FX's STOP) joins the timer's.
                chip.CatchUp(_clock);
                Cpu.Irq = _irqFlag || chip.Irq;
            }
            else
            {
                Cpu.Irq = _irqFlag;
            }

            Cpu.Step();
            if (!_hdmaDone && _clock - start >= HdmaClock)
            {
                _hdmaDone = true;
                if (_line < vblank)
                {
                    RunHdmaLine();
                }
            }
        }

        if (!_hdmaDone && _line < vblank)
        {
            _hdmaDone = true;
            RunHdmaLine();
        }
    }

    private void CheckIrq(long lineStart)
    {
        var mode = (_nmitimen >> 4) & 3;
        if (mode == 0 || _irqFiredThisLine)
        {
            return;
        }

        var dot = (int)((_clock - lineStart) / 4);
        var fire = mode switch
        {
            1 => dot >= _htime,
            2 => _line == _vtime,
            _ => _line == _vtime && dot >= _htime,
        };
        if (fire)
        {
            _irqFiredThisLine = true;
            _irqFlag = true;
        }
    }

    private void OnSample(short left, short right)
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

    private void CatchUpApu()
    {
        var spcCycles = (int)((_clock - _apuClock) * 1024000 / MasterClock);
        if (spcCycles > 0)
        {
            Apu.Run(spcCycles);
            _apuClock += (long)(spcCycles * MasterClock / 1024000);
        }
    }

    // ---- The CPU's bus ----

    private int Speed(uint address)
    {
        var bank = address >> 16;
        var offset = address & 0xFFFF;
        if ((bank & 0x40) == 0)
        {
            if (offset < 0x2000)
            {
                return 8;
            }

            if (offset < 0x4000)
            {
                return 6;
            }

            if (offset < 0x4200)
            {
                return 12;
            }

            if (offset < 0x6000)
            {
                return 6;
            }

            if (offset < 0x8000)
            {
                return 8;
            }

            return bank >= 0x80 && _fastRom ? 6 : 8;
        }

        return bank >= 0xC0 && _fastRom ? 6 : 8;
    }

    public byte Read(uint address)
    {
        _clock += Speed(address);
        var value = ReadBus(address);
        _openBus = value;
        return value;
    }

    public void Write(uint address, byte value)
    {
        _clock += Speed(address);
        _openBus = value;
        WriteBus(address, value);
    }

    public void Idle() => _clock += 6;

    private byte ReadBus(uint address)
    {
        var bank = address >> 16;
        var offset = (int)(address & 0xFFFF);
        if (bank is 0x7E or 0x7F)
        {
            return _wram[(int)(bank & 1) << 16 | offset];
        }

        if (_coprocessor is { } chip && chip.Claims(address))
        {
            chip.CatchUp(_clock);
            return chip.Read(address, _openBus);
        }

        if ((bank & 0x40) == 0)
        {
            switch (offset)
            {
                case < 0x2000: return _wram[offset];
                case >= 0x2100 and < 0x2140:
                    if (offset == 0x2137)
                    {
                        LatchCounters();
                    }

                    return Ppu.Read(offset, _openBus);
                case >= 0x2140 and < 0x2180:
                    CatchUpApu();
                    return Apu.CpuRead(offset);
                case 0x2180:
                {
                    var value = _wram[_wramAddress];
                    _wramAddress = (_wramAddress + 1) & 0x1FFFF;
                    return value;
                }

                case 0x4016: return (byte)((_openBus & 0xFC) | SerialBit(0));
                case 0x4017: return (byte)((_openBus & 0xE0) | 0x1C | SerialBit(1));
                case >= 0x4200 and < 0x4220: return ReadCpuRegister(offset);
                case >= 0x4300 and < 0x4380: return _dma[offset & 0x7F];
                case < 0x6000: return _openBus;
            }
        }

        return Cartridge.TryRead(address, out var cart) ? cart : _openBus;
    }

    private void WriteBus(uint address, byte value)
    {
        var bank = address >> 16;
        var offset = (int)(address & 0xFFFF);
        if (bank is 0x7E or 0x7F)
        {
            _wram[(int)(bank & 1) << 16 | offset] = value;
            return;
        }

        if (_coprocessor is { } chip && chip.Claims(address))
        {
            chip.CatchUp(_clock);
            chip.Write(address, value);
            return;
        }

        if ((bank & 0x40) == 0)
        {
            switch (offset)
            {
                case < 0x2000:
                    _wram[offset] = value;
                    return;
                case >= 0x2100 and < 0x2200:
                    WriteB((byte)offset, value);
                    return;
                case 0x4016:
                {
                    var latch = (value & 1) != 0;
                    if (latch)
                    {
                        _serial[0] = _serial[1] = 0;
                    }

                    _joypadLatch = latch;
                    return;
                }

                case >= 0x4200 and < 0x4220:
                    WriteCpuRegister(offset, value);
                    return;
                case >= 0x4300 and < 0x4380:
                    _dma[offset & 0x7F] = value;
                    return;
                case < 0x6000:
                    return;
            }
        }

        Cartridge.Write(address, value);
    }

    /// <summary>The B bus ($2100–$21FF): the PPU, the sound ports and the WRAM port.</summary>
    private void WriteB(byte register, byte value)
    {
        switch (register)
        {
            case < 0x40:
                Ppu.Write(register, value);
                break;
            case < 0x80:
                CatchUpApu();
                Apu.CpuWrite(register, value);
                break;
            case 0x80:
                _wram[_wramAddress] = value;
                _wramAddress = (_wramAddress + 1) & 0x1FFFF;
                break;
            case 0x81: _wramAddress = (_wramAddress & 0x1FF00) | value; break;
            case 0x82: _wramAddress = (_wramAddress & 0x100FF) | value << 8; break;
            case 0x83: _wramAddress = (_wramAddress & 0x0FFFF) | (value & 1) << 16; break;
        }
    }

    private byte ReadB(byte register) => register switch
    {
        < 0x40 => Ppu.Read(register, _openBus),
        < 0x80 => Apu.CpuRead(register),
        0x80 => ReadBus(0x2180),
        _ => _openBus,
    };

    private int SerialBit(int port)
    {
        if (_joypadLatch)
        {
            return (PadBits(_pads[port]) >> 15) & 1;
        }

        var bit = _serial[port] < 16 ? (PadBits(_pads[port]) >> (15 - _serial[port])) & 1 : 1;
        _serial[port]++;
        return bit;
    }

    private void LatchCounters()
    {
        var lineStart = (long)_line * ClocksPerLine;
        Ppu.LatchCounters((int)((_clock - lineStart) / 4), _line);
    }

    private byte ReadCpuRegister(int offset)
    {
        switch (offset)
        {
            case 0x4210:
            {
                var value = (byte)((_nmiFlag ? 0x80 : 0) | (_openBus & 0x70) | 0x02);
                _nmiFlag = false;
                return value;
            }

            case 0x4211:
            {
                var value = (byte)((_irqFlag ? 0x80 : 0) | (_openBus & 0x7F));
                _irqFlag = false;
                return value;
            }

            case 0x4212:
            {
                var lineStart = (long)_line * ClocksPerLine;
                var hblank = _clock - lineStart >= HBlankClock || _clock - lineStart < 4;
                return (byte)((_line >= VBlankLine ? 0x80 : 0) | (hblank ? 0x40 : 0) | (_autoReadBusy ? 1 : 0) | (_openBus & 0x3E));
            }

            case 0x4213: return _wrio;
            case 0x4214: return (byte)_quotient;
            case 0x4215: return (byte)(_quotient >> 8);
            case 0x4216: return (byte)_product;
            case 0x4217: return (byte)(_product >> 8);
            case >= 0x4218 and < 0x4220:
            {
                var pad = _joypads[(offset - 0x4218) >> 1];
                return (offset & 1) == 0 ? (byte)pad : (byte)(pad >> 8);
            }

            default: return _openBus;
        }
    }

    private void WriteCpuRegister(int offset, byte value)
    {
        switch (offset)
        {
            case 0x4200:
                // Enabling NMI during the vertical blank, with the flag still up, fires it at once.
                if ((value & 0x80) != 0 && (_nmitimen & 0x80) == 0 && _nmiFlag)
                {
                    Cpu.Nmi = true;
                }

                _nmitimen = value;
                if ((value & 0x30) == 0)
                {
                    _irqFlag = false;
                }

                break;
            case 0x4201:
                if ((_wrio & 0x80) != 0 && (value & 0x80) == 0)
                {
                    LatchCounters();
                }

                _wrio = value;
                break;
            case 0x4202: _wrmpya = value; break;
            case 0x4203:
                _product = (ushort)(_wrmpya * value);
                _quotient = value;
                break;
            case 0x4204: _dividend = (ushort)((_dividend & 0xFF00) | value); break;
            case 0x4205: _dividend = (ushort)((_dividend & 0x00FF) | value << 8); break;
            case 0x4206:
                if (value == 0)
                {
                    _quotient = 0xFFFF;
                    _product = _dividend;
                }
                else
                {
                    _quotient = (ushort)(_dividend / value);
                    _product = (ushort)(_dividend % value);
                }

                break;
            case 0x4207: _htime = (ushort)((_htime & 0x100) | value); break;
            case 0x4208: _htime = (ushort)((_htime & 0xFF) | (value & 1) << 8); break;
            case 0x4209: _vtime = (ushort)((_vtime & 0x100) | value); break;
            case 0x420A: _vtime = (ushort)((_vtime & 0xFF) | (value & 1) << 8); break;
            case 0x420B: RunDma(value); break;
            case 0x420C: _hdmaEnabled = value; break;
            case 0x420D: _fastRom = (value & 1) != 0; break;
        }
    }

    // ---- DMA ----

    private int Channel(int channel, int register) => _dma[channel << 4 | register];

    private uint ChannelA(int channel) => (uint)(Channel(channel, 4) << 16 | Channel(channel, 3) << 8 | Channel(channel, 2));

    /// <summary>General-purpose DMA: each enabled channel moves its byte count, 8 master clocks a byte; the CPU waits.</summary>
    private void RunDma(byte channels)
    {
        _clock += 8;
        for (var channel = 0; channel < 8; channel++)
        {
            if ((channels & (1 << channel)) == 0)
            {
                continue;
            }

            _clock += 8;
            var parameters = Channel(channel, 0);
            var pattern = Patterns[parameters & 7];
            var bAddress = Channel(channel, 1);
            var count = Channel(channel, 5) | Channel(channel, 6) << 8;
            if (count == 0)
            {
                count = 0x10000;
            }

            var bank = Channel(channel, 4);
            var address = Channel(channel, 2) | Channel(channel, 3) << 8;
            var step = (parameters >> 3) & 3;
            var toA = (parameters & 0x80) != 0;
            for (var i = 0; i < count; i++)
            {
                var b = (byte)(bAddress + pattern[i % pattern.Length]);
                var a = (uint)(bank << 16 | address);
                if (toA)
                {
                    WriteBus(a, ReadB(b));
                }
                else
                {
                    WriteB(b, ReadBus(a));
                }

                _clock += 8;
                address = step switch
                {
                    0 => (address + 1) & 0xFFFF,
                    2 => (address - 1) & 0xFFFF,
                    _ => address,
                };
            }

            _dma[channel << 4 | 2] = (byte)address;
            _dma[channel << 4 | 3] = (byte)(address >> 8);
            _dma[channel << 4 | 5] = 0;
            _dma[channel << 4 | 6] = 0;
        }
    }

    // ---- HDMA ----

    private void InitHdma()
    {
        for (var channel = 0; channel < 8; channel++)
        {
            _hdmaTerminated[channel] = true;
            if ((_hdmaEnabled & (1 << channel)) == 0)
            {
                continue;
            }

            // The table address starts at A1T; the first entry loads.
            _dma[channel << 4 | 8] = (byte)Channel(channel, 2);
            _dma[channel << 4 | 9] = (byte)Channel(channel, 3);
            _hdmaTerminated[channel] = false;
            LoadHdmaEntry(channel);
        }
    }

    private byte ReadHdmaTable(int channel)
    {
        var address = Channel(channel, 8) | Channel(channel, 9) << 8;
        var value = ReadBus((uint)(Channel(channel, 4) << 16 | address));
        address = (address + 1) & 0xFFFF;
        _dma[channel << 4 | 8] = (byte)address;
        _dma[channel << 4 | 9] = (byte)(address >> 8);
        _clock += 8;
        return value;
    }

    private void LoadHdmaEntry(int channel)
    {
        var counter = ReadHdmaTable(channel);
        _dma[channel << 4 | 0xA] = counter;
        if ((Channel(channel, 0) & 0x40) != 0)
        {
            _dma[channel << 4 | 5] = ReadHdmaTable(channel);
            _dma[channel << 4 | 6] = ReadHdmaTable(channel);
        }

        _hdmaTerminated[channel] = counter == 0;
        _hdmaDoTransfer[channel] = true;
    }

    private void RunHdmaLine()
    {
        if (_hdmaEnabled == 0)
        {
            return;
        }

        for (var channel = 0; channel < 8; channel++)
        {
            if ((_hdmaEnabled & (1 << channel)) == 0 || _hdmaTerminated[channel])
            {
                continue;
            }

            var parameters = Channel(channel, 0);
            if (_hdmaDoTransfer[channel])
            {
                var pattern = Patterns[parameters & 7];
                var indirect = (parameters & 0x40) != 0;
                foreach (var offset in pattern)
                {
                    byte value;
                    if (indirect)
                    {
                        var address = Channel(channel, 5) | Channel(channel, 6) << 8;
                        value = ReadBus((uint)(Channel(channel, 7) << 16 | address));
                        address = (address + 1) & 0xFFFF;
                        _dma[channel << 4 | 5] = (byte)address;
                        _dma[channel << 4 | 6] = (byte)(address >> 8);
                        _clock += 8;
                    }
                    else
                    {
                        value = ReadHdmaTable(channel);
                    }

                    var b = (byte)(Channel(channel, 1) + offset);
                    if ((parameters & 0x80) != 0)
                    {
                        // B→A HDMA writes into the table (rare).
                        continue;
                    }

                    WriteB(b, value);
                }
            }

            var counter = (byte)(Channel(channel, 0xA) - 1);
            _dma[channel << 4 | 0xA] = counter;
            _hdmaDoTransfer[channel] = (counter & 0x80) != 0;
            if ((counter & 0x7F) == 0)
            {
                LoadHdmaEntry(channel);
            }
        }
    }

    // ---- Memory maps and achievements ----

    /// <summary>WRAM at 0–$1FFFF, then cartridge SRAM (also RetroAchievements' layout for the SNES, console 3).</summary>
    public byte PeekMemory(int address)
    {
        if (address is >= 0 and < 0x20000)
        {
            return _wram[address];
        }

        var sram = Cartridge.BatteryRam;
        var index = address - 0x20000;
        return index >= 0 && index < sram.Length ? sram[index] : (byte)0;
    }

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
        var writer = new StateWriter(0x60000);
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
        Ppu.SaveState(writer);
        Apu.SaveState(writer);
        Cartridge.SaveState(writer);
        writer.WriteBytes(_wram);
        writer.WriteBytes(_dma);
        for (var i = 0; i < 8; i++)
        {
            writer.WriteBool(_hdmaDoTransfer[i]);
            writer.WriteBool(_hdmaTerminated[i]);
        }

        writer.WriteUInt16((ushort)_pads[0]);
        writer.WriteUInt16((ushort)_pads[1]);
        foreach (var pad in _joypads)
        {
            writer.WriteUInt16(pad);
        }

        writer.WriteInt32(_serial[0]);
        writer.WriteInt32(_serial[1]);
        writer.WriteInt64(_clock);
        writer.WriteInt64(_apuClock);
        writer.WriteByte(_openBus);
        writer.WriteByte(_nmitimen);
        writer.WriteByte(_wrio);
        writer.WriteByte(_wrmpya);
        writer.WriteUInt16(_dividend);
        writer.WriteUInt16(_quotient);
        writer.WriteUInt16(_product);
        writer.WriteUInt16(_htime);
        writer.WriteUInt16(_vtime);
        writer.WriteByte(_hdmaEnabled);
        writer.WriteBool(_fastRom);
        writer.WriteBool(_nmiFlag);
        writer.WriteBool(_irqFlag);
        writer.WriteBool(_autoReadBusy);
        writer.WriteBool(_joypadLatch);
        writer.WriteInt32(_wramAddress);
        writer.WriteInt64(FrameCount);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Ppu.LoadState(reader);
        Apu.LoadState(reader);
        Cartridge.LoadState(reader);
        reader.ReadBytes(_wram);
        reader.ReadBytes(_dma);
        for (var i = 0; i < 8; i++)
        {
            _hdmaDoTransfer[i] = reader.ReadBool();
            _hdmaTerminated[i] = reader.ReadBool();
        }

        _pads[0] = (ControllerState)reader.ReadUInt16();
        _pads[1] = (ControllerState)reader.ReadUInt16();
        for (var i = 0; i < 4; i++)
        {
            _joypads[i] = reader.ReadUInt16();
        }

        _serial[0] = reader.ReadInt32();
        _serial[1] = reader.ReadInt32();
        _clock = reader.ReadInt64();
        _apuClock = reader.ReadInt64();
        _openBus = reader.ReadByte();
        _nmitimen = reader.ReadByte();
        _wrio = reader.ReadByte();
        _wrmpya = reader.ReadByte();
        _dividend = reader.ReadUInt16();
        _quotient = reader.ReadUInt16();
        _product = reader.ReadUInt16();
        _htime = reader.ReadUInt16();
        _vtime = reader.ReadUInt16();
        _hdmaEnabled = reader.ReadByte();
        _fastRom = reader.ReadBool();
        _nmiFlag = reader.ReadBool();
        _irqFlag = reader.ReadBool();
        _autoReadBusy = reader.ReadBool();
        _joypadLatch = reader.ReadBool();
        _wramAddress = reader.ReadInt32();
        FrameCount = reader.ReadInt64();
        reader.EndChunk();
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
