using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.M68000;
using GameVault.Core.Cpu.Z80;
using GameVault.Core.Input;
using GameVault.Core.Sega;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Genesis;

/// <summary>
/// The Sega Genesis / Mega Drive (SPEC §7.11, Phase 13): a 68000 at 7.67 MHz, a Z80 at 3.58 MHz for sound, the VDP,
/// the YM2612 and the SN76489, with two 3- or 6-button pads. It is a model 1 without TMSS, so no Sega code is
/// needed. Deterministic, like every core.
/// </summary>
/// <remarks>
/// Everything runs from the 53.69 MHz (PAL 53.20 MHz) master clock: the 68000 and the YM2612 on master ÷ 7, the Z80
/// and the PSG on master ÷ 15, and 3,420 master clocks per line. The Z80 catches up with the 68000 after each 68000
/// instruction. References: Charles MacDonald, "Sega Genesis hardware notes"; Sega Retro; Plutiedev ("Controllers",
/// "Z80 and the 68000").
/// </remarks>
public sealed class GenesisConsole : IEmulatedConsole, IM68000Bus, IZ80Bus
{
    public const double NtscMasterClock = 53693175.0;
    public const double PalMasterClock = 53203424.0;

    private const int ActiveMasterClocks = 2560; // the part of a line before H-blank
    private const int Z80InterruptClocks = 2560;

    private static readonly StateTag Tag = StateTag.From("GENS");
    private static readonly StateTag SnapshotTag = StateTag.From("GVMD");

    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _z80Ram = new byte[0x2000];
    private readonly ControllerState[] _pads = new ControllerState[2];
    private readonly Pad[] _ports = [new(), new()];
    private readonly ScaledSink _psgSink;
    private readonly long[] _lineStart = new long[1];

    private long _master;      // the 68000's position in master clocks within the frame
    private long _z80Master;   // the Z80's
    private long _ymMaster;    // where the YM2612's last sample was taken
    private int _z80Bank;
    private bool _z80BusRequested;
    private bool _z80Reset = true;
    private int _cpuDebt;
    private float _lastFmLeft;
    private float _lastFmRight;

    public GenesisConsole(GenesisCartridge cartridge, GenesisRegion? region = null, bool sixButton = true)
    {
        Cartridge = cartridge;
        Region = region ?? cartridge.Region;
        Pal = Region == GenesisRegion.Europe;
        SixButton = sixButton;
        Cpu = new M68000Cpu(this);
        Z80 = new Z80Cpu(this);
        Vdp = new GenesisVdp(Pal) { DmaRead = DmaRead };
        Fm = new Ym2612();
        Psg = new Sn76489();
        _psgSink = new ScaledSink(15, 0.5f);
        RomHash = HashRom(cartridge.Rom);
    }

    public GameSystem System => GameSystem.Genesis;

    public GenesisRegion Region { get; }

    public bool Pal { get; }

    /// <summary>The 6-button pad protocol (the default); some older games need the 3-button pad.</summary>
    public bool SixButton { get; }

    public M68000Cpu Cpu { get; }

    public Z80Cpu Z80 { get; }

    public GenesisVdp Vdp { get; }

    public Ym2612 Fm { get; }

    public Sn76489 Psg { get; }

    public GenesisCartridge Cartridge { get; }

    public ulong RomHash { get; }

    public int ScreenWidth => Vdp.FrameWidth;

    public int ScreenHeight => Vdp.FrameHeight;

    /// <summary>The picture fills a 4:3 screen whatever the mode: 320×224 pixels are narrower than square, 256×224 wider.</summary>
    public double PixelAspect => 4.0 / 3.0 / ((double)Vdp.FrameWidth / Vdp.FrameHeight);

    public ReadOnlySpan<ushort> FrameBuffer => Vdp.Frame;

    public ReadOnlySpan<uint> Palette => GenesisVdp.Palette;

    public long FrameCount { get; private set; }

    /// <summary>The master clock: audio is stamped in master clocks.</summary>
    public double CpuClockRate => Pal ? PalMasterClock : NtscMasterClock;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    public IBatteryBacked? Battery => Cartridge.HasBattery ? Cartridge : null;

    /// <summary>The 64 KB of 68000 work RAM ($FF0000–$FFFFFF).</summary>
    public ReadOnlySpan<byte> WorkRam => _ram;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate) { ApplyNesFilters = false };
        _psgSink.Target = Audio;
        Psg.AudioSink = _psgSink;
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Array.Clear(_z80Ram);
        Vdp.Power();
        Fm.Reset();
        Psg.Power();
        Cartridge.Reset();
        Z80.Power();
        foreach (var port in _ports)
        {
            port.Reset();
        }

        _master = _z80Master = _ymMaster = 0;
        _z80Bank = 0;
        _z80BusRequested = false;
        _z80Reset = true;
        _cpuDebt = 0;
        _lastFmLeft = 0;
        _lastFmRight = 0;
        FrameCount = 0;
        Cpu.Reset();
    }

    /// <summary>The console's reset button: the 68000 and the Z80 restart; memory and the VDP keep their contents.</summary>
    public void Reset()
    {
        Cartridge.Reset();
        Fm.Reset();
        Z80.Reset();
        _z80Reset = true;
        _z80BusRequested = false;
        Cpu.Reset();
    }

    /// <summary>
    /// The pad's A is the controller's B, its B is A, and Mode is Select, so the keyboard keeps Z, X, C for A, B, C
    /// (see <see cref="ControllerState"/>).
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if (port is 0 or 1)
        {
            _pads[port] = state;
        }
    }

    public void RunFrame()
    {
        var lines = Vdp.LinesPerFrame;
        for (var line = 0; line < lines; line++)
        {
            RunLine(line);
        }

        var frameClocks = (long)lines * GenesisVdp.MasterClocksPerLine;
        RunPsgTo(frameClocks);
        _master -= frameClocks;
        _z80Master -= frameClocks;
        _ymMaster -= frameClocks;
        Psg.EndAudioFrame();
        Audio?.EndFrame((int)frameClocks);
        FrameCount++;
    }

    public void RunFrameSilently()
    {
        var audio = Audio;
        Audio = null;
        _psgSink.Target = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Audio = audio;
            _psgSink.Target = audio;
        }
    }

    private void RunLine(int line)
    {
        var start = (long)line * GenesisVdp.MasterClocksPerLine;
        _lineStart[0] = start;
        Vdp.BeginLine(line);
        RunTo(start + ActiveMasterClocks, line);
        Vdp.BeginHBlank();
        RunTo(start + GenesisVdp.MasterClocksPerLine, line);
    }

    private void RunTo(long target, int line)
    {
        while (_master < target)
        {
            var z80Interrupt = Vdp.Z80Interrupt && _master - (long)line * GenesisVdp.MasterClocksPerLine < Z80InterruptClocks;
            Z80.IrqLine = z80Interrupt;
            Cpu.InterruptLevel = Vdp.InterruptLevel;
            int cycles;
            if (_cpuDebt > 0)
            {
                cycles = Math.Min(_cpuDebt, 64);
                _cpuDebt -= cycles;
            }
            else
            {
                cycles = Cpu.Step();
                if (Vdp.DmaStallCycles > 0)
                {
                    _cpuDebt += Vdp.DmaStallCycles;
                    Vdp.DmaStallCycles = 0;
                }
            }

            _master += cycles * 7L;
            RunYmTo(_master);
            RunZ80To(_master);
        }
    }

    private void RunZ80To(long target)
    {
        while (_z80Master < target)
        {
            int cycles;
            if (_z80Reset || _z80BusRequested)
            {
                cycles = (int)Math.Max(1, (target - _z80Master + 14) / 15);
            }
            else
            {
                cycles = Z80.Step();
            }

            Psg.Run(cycles);
            _z80Master += cycles * 15L;
        }
    }

    private void RunPsgTo(long target)
    {
        if (_z80Master < target)
        {
            var cycles = (int)((target - _z80Master + 14) / 15);
            Psg.Run(cycles);
            _z80Master += cycles * 15L;
        }
    }

    /// <summary>The YM2612 runs on the 68000's clock and makes a sample every 144 of its clocks (1,008 master clocks).</summary>
    private void RunYmTo(long target)
    {
        while (_ymMaster + Fm.ClocksToNextSample * 7L <= target)
        {
            var clocks = Fm.ClocksToNextSample;
            Fm.Clock(clocks);
            _ymMaster += clocks * 7L;
            if (Audio is { } audio)
            {
                // Each channel is at most ±8192; panning (register $B4) sends a channel to the left, right or both.
                var left = Fm.Left * (0.16f / 8192);
                var right = Fm.Right * (0.16f / 8192);
                if (left != _lastFmLeft || right != _lastFmRight)
                {
                    audio.AddDelta((int)Math.Max(0, _ymMaster), left - _lastFmLeft, right - _lastFmRight);
                    _lastFmLeft = left;
                    _lastFmRight = right;
                }
            }
        }
    }

    // ---- The 68000's view ----

    public byte ReadByte(uint address)
    {
        switch (address >> 16)
        {
            case < 0x40:
                return Cartridge.ReadByte(address);
            case 0xA0:
                return Z80AreaRead(address);
            case 0xA1:
                return IoReadByte(address);
            case >= 0xC0 and < 0xE0:
            {
                var word = VdpRead(address & ~1u);
                return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
            }

            case >= 0xE0:
                return _ram[address & 0xFFFF];
            default:
                return 0;
        }
    }

    public ushort ReadWord(uint address)
    {
        switch (address >> 16)
        {
            case < 0x40:
                return Cartridge.ReadWord(address);
            case 0xA0:
            {
                // Word reads of the Z80's space return the byte in both halves.
                var value = Z80AreaRead(address);
                return (ushort)(value << 8 | value);
            }

            case 0xA1:
                if ((address & 0xFFFF) == 0x1100)
                {
                    // Z80 BUSREQ: bit 0 of the upper byte is 0 once the 68000 has the bus.
                    return (ushort)(_z80BusRequested && !_z80Reset ? 0x0000 : 0x0100);
                }

                var io = IoReadByte(address | 1);
                return (ushort)(io << 8 | io);
            case >= 0xC0 and < 0xE0:
                return VdpRead(address);
            case >= 0xE0:
                return (ushort)(_ram[address & 0xFFFF] << 8 | _ram[(address + 1) & 0xFFFF]);
            default:
                return 0;
        }
    }

    public void WriteByte(uint address, byte value)
    {
        switch (address >> 16)
        {
            case < 0x40:
                Cartridge.WriteByte(address, value);
                break;
            case 0xA0:
                Z80AreaWrite(address, value);
                break;
            case 0xA1:
                IoWrite(address, value);
                break;
            case >= 0xC0 and < 0xE0:
                if ((address & 0x1F) is 0x11 or 0x13 or 0x15 or 0x17)
                {
                    Psg.Write(value);
                }
                else
                {
                    VdpWrite(address & ~1u, (ushort)(value << 8 | value));
                }

                break;
            case >= 0xE0:
                _ram[address & 0xFFFF] = value;
                break;
        }
    }

    public void WriteWord(uint address, ushort value)
    {
        switch (address >> 16)
        {
            case < 0x40:
                break;
            case 0xA0:
                Z80AreaWrite(address, (byte)(value >> 8));
                break;
            case 0xA1:
                IoWrite(address, (byte)(value >> 8));
                if ((address & 0xFFFF) >= 0x0000 && (address & 0xFFFF) < 0x0020)
                {
                    IoWrite(address | 1, (byte)value);
                }

                break;
            case >= 0xC0 and < 0xE0:
                if ((address & 0x1F) is 0x10 or 0x12 or 0x14 or 0x16)
                {
                    Psg.Write((byte)value);
                }
                else
                {
                    VdpWrite(address, value);
                }

                break;
            case >= 0xE0:
                _ram[address & 0xFFFF] = (byte)(value >> 8);
                _ram[(address + 1) & 0xFFFF] = (byte)value;
                break;
        }
    }

    public int AcknowledgeInterrupt(int level)
    {
        Vdp.Acknowledge(level);
        return -1; // autovectored
    }

    private double LineFraction() =>
        Math.Clamp((_master - _lineStart[0]) / (double)GenesisVdp.MasterClocksPerLine, 0, 0.999);

    private ushort VdpRead(uint address) => (address & 0x1F) switch
    {
        < 0x04 => Vdp.ReadData(),
        < 0x08 => Vdp.ReadStatus(LineFraction()),
        < 0x10 => Vdp.HvCounter(LineFraction()),
        _ => 0xFFFF,
    };

    private void VdpWrite(uint address, ushort value)
    {
        switch (address & 0x1F)
        {
            case < 0x04:
                Vdp.WriteData(value);
                break;
            case < 0x08:
                Vdp.WriteControl(value);
                break;
        }
    }

    private ushort DmaRead(uint address) => (address >> 16) switch
    {
        < 0x40 => Cartridge.ReadWord(address),
        >= 0xE0 => (ushort)(_ram[address & 0xFFFF] << 8 | _ram[(address + 1) & 0xFFFF]),
        _ => 0,
    };

    // ---- The Z80's space as the 68000 sees it ($A00000–$A0FFFF) ----

    private byte Z80AreaRead(uint address)
    {
        var offset = (ushort)(address & 0x7FFF);
        if (!_z80BusRequested && !_z80Reset)
        {
            return 0xFF; // the 68000 doesn't have the bus
        }

        return offset < 0x4000 ? _z80Ram[offset & 0x1FFF] : offset < 0x6000 ? Fm.ReadStatus() : (byte)0xFF;
    }

    private void Z80AreaWrite(uint address, byte value)
    {
        var offset = (ushort)(address & 0x7FFF);
        if (offset < 0x4000)
        {
            _z80Ram[offset & 0x1FFF] = value;
        }
        else if (offset < 0x6000)
        {
            Fm.Write(offset & 3, value);
        }
        else if (offset < 0x6100)
        {
            WriteBank(value);
        }
        else if (offset is >= 0x7F11 and <= 0x7F17 && (offset & 1) != 0)
        {
            Psg.Write(value);
        }
    }

    // ---- I/O ($A10000–$A1FFFF) ----

    private byte IoReadByte(uint address)
    {
        var offset = address & 0xFFFF;
        switch (offset)
        {
            case 0x0001:
                // Overseas (not Japan), PAL, and no expansion unit attached (bit 5); version 0 (no TMSS).
                return (byte)((Region == GenesisRegion.Japan ? 0 : 0x80) | (Pal ? 0x40 : 0) | 0x20);
            case 0x0003:
                return _ports[0].Read(_pads[0], SixButton, _master);
            case 0x0005:
                return _ports[1].Read(_pads[1], SixButton, _master);
            case 0x0007:
                return 0x7F;
            case 0x0009:
                return _ports[0].Control;
            case 0x000B:
                return _ports[1].Control;
            case 0x1100:
                return (byte)(_z80BusRequested && !_z80Reset ? 0x00 : 0x01);
            default:
                return 0;
        }
    }

    private void IoWrite(uint address, byte value)
    {
        var offset = address & 0xFFFF;
        switch (offset)
        {
            case 0x0003:
                _ports[0].WriteData(value, _master);
                break;
            case 0x0005:
                _ports[1].WriteData(value, _master);
                break;
            case 0x0009:
                _ports[0].Control = value;
                break;
            case 0x000B:
                _ports[1].Control = value;
                break;
            case 0x1100:
                _z80BusRequested = (value & 1) != 0;
                break;
            case 0x1200:
            {
                var reset = (value & 1) == 0;
                if (reset && !_z80Reset)
                {
                    Z80.Reset();
                    Fm.Reset();
                }

                _z80Reset = reset;
                break;
            }

            case >= 0x30F0 and <= 0x30FF:
                if ((offset & 1) != 0)
                {
                    Cartridge.WriteControl(offset, value);
                }

                break;
        }
    }

    // ---- The Z80's view ----

    public byte Read(ushort address)
    {
        if (address < 0x4000)
        {
            return _z80Ram[address & 0x1FFF];
        }

        if (address < 0x6000)
        {
            return Fm.ReadStatus();
        }

        if (address >= 0x8000)
        {
            // The 32 KB window onto the 68000's space, at the bank register's address.
            var target = (uint)(_z80Bank << 15) | (uint)(address & 0x7FFF);
            return ReadByte(target);
        }

        return 0xFF;
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x4000)
        {
            _z80Ram[address & 0x1FFF] = value;
        }
        else if (address < 0x6000)
        {
            Fm.Write(address & 3, value);
        }
        else if (address < 0x6100)
        {
            WriteBank(value);
        }
        else if (address is >= 0x7F10 and < 0x7F18 && (address & 1) != 0)
        {
            Psg.Write(value);
        }
        else if (address >= 0x8000)
        {
            var target = (uint)(_z80Bank << 15) | (uint)(address & 0x7FFF);
            WriteByte(target, value);
        }
    }

    public byte In(ushort port) => 0xFF;

    public void Out(ushort port, byte value)
    {
    }

    /// <summary>The bank register takes one bit per write, filling address bits 15–23 from the top.</summary>
    private void WriteBank(byte value) => _z80Bank = ((_z80Bank >> 1) | ((value & 1) << 8)) & 0x1FF;

    // ---- Memory maps and achievements ----

    /// <summary>Work RAM at 0–$FFFF (the 68000's $FF0000–$FFFFFF), then cartridge RAM.</summary>
    public byte PeekMemory(int address)
    {
        if (address is >= 0 and < 0x10000)
        {
            return _ram[address];
        }

        var sram = Cartridge.BatteryRam;
        var index = address - 0x10000;
        return index >= 0 && index < sram.Length ? sram[index] : (byte)0;
    }

    /// <summary>
    /// RetroAchievements' layout (console 1): work RAM with each 16-bit word's bytes swapped, as the reference core
    /// exposes it, then cartridge RAM at $10000.
    /// </summary>
    public byte ReadAchievementMemory(int address) =>
        address is >= 0 and < 0x10000 ? _ram[address ^ 1] : PeekMemory(address);

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
        var writer = new StateWriter(0x40000);
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
        Z80.SaveState(writer);
        Vdp.SaveState(writer);
        Fm.SaveState(writer);
        Psg.SaveState(writer);
        Cartridge.SaveState(writer);
        writer.WriteBytes(_ram);
        writer.WriteBytes(_z80Ram);
        foreach (var port in _ports)
        {
            port.Save(writer);
        }

        writer.WriteUInt16((ushort)_pads[0]);
        writer.WriteUInt16((ushort)_pads[1]);
        writer.WriteInt64(_master);
        writer.WriteInt64(_z80Master);
        writer.WriteInt64(_ymMaster);
        writer.WriteInt32(_z80Bank);
        writer.WriteBool(_z80BusRequested);
        writer.WriteBool(_z80Reset);
        writer.WriteInt32(_cpuDebt);
        writer.WriteInt64(FrameCount);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Z80.LoadState(reader);
        Vdp.LoadState(reader);
        Fm.LoadState(reader);
        Psg.LoadState(reader);
        Cartridge.LoadState(reader);
        reader.ReadBytes(_ram);
        reader.ReadBytes(_z80Ram);
        foreach (var port in _ports)
        {
            port.Load(reader);
        }

        _pads[0] = (ControllerState)reader.ReadUInt16();
        _pads[1] = (ControllerState)reader.ReadUInt16();
        _master = reader.ReadInt64();
        _z80Master = reader.ReadInt64();
        _ymMaster = reader.ReadInt64();
        _z80Bank = reader.ReadInt32();
        _z80BusRequested = reader.ReadBool();
        _z80Reset = reader.ReadBool();
        _cpuDebt = reader.ReadInt32();
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

    /// <summary>
    /// A controller port: the data and control registers and the pad behind them. Writing TH (bit 6) switches
    /// between the two halves of the 3-button pad; the 6-button pad counts TH changes and shows X, Y, Z and Mode on
    /// the fourth high, until 1.5 ms pass without a change (Plutiedev, "Controllers").
    /// </summary>
    private sealed class Pad
    {
        private const long TimeoutMaster = 80000; // about 1.5 ms

        private byte _data = 0x40;
        private int _count;
        private long _lastChange;

        public byte Control { get; set; }

        public void Reset()
        {
            _data = 0x40;
            _count = 0;
            _lastChange = 0;
            Control = 0;
        }

        public void WriteData(byte value, long now)
        {
            var thBefore = Th;
            _data = value;
            if ((Control & 0x40) != 0 && Th != thBefore)
            {
                if (now - _lastChange > TimeoutMaster || now < _lastChange)
                {
                    _count = 0;
                }

                _count = (_count + 1) & 7;
                _lastChange = now;
            }
        }

        private bool Th => (Control & 0x40) == 0 || (_data & 0x40) != 0;

        public byte Read(ControllerState pad, bool sixButton, long now)
        {
            if (sixButton && (now - _lastChange > TimeoutMaster || now < _lastChange))
            {
                _count = 0;
            }

            bool Pressed(ControllerState button) => (pad & button) != 0;
            int Bit(bool pressed, int bit) => pressed ? 0 : 1 << bit;

            int lines;
            var phase = sixButton ? _count : _count & 1;
            if (Th)
            {
                lines = phase == 6 && sixButton
                    // C, B, Mode, X, Y, Z
                    ? Bit(Pressed(ControllerState.C), 5) | Bit(Pressed(ControllerState.A), 4) | Bit(Pressed(ControllerState.Select), 3)
                      | Bit(Pressed(ControllerState.X), 2) | Bit(Pressed(ControllerState.Y), 1) | Bit(Pressed(ControllerState.Z), 0)
                    // C, B, Right, Left, Down, Up
                    : Bit(Pressed(ControllerState.C), 5) | Bit(Pressed(ControllerState.A), 4) | Bit(Pressed(ControllerState.Right), 3)
                      | Bit(Pressed(ControllerState.Left), 2) | Bit(Pressed(ControllerState.Down), 1) | Bit(Pressed(ControllerState.Up), 0);
                lines |= 0x40;
            }
            else
            {
                // Start, A, then 0, 0, Down, Up (all four low on the third low phase, all high on the fourth).
                lines = Bit(Pressed(ControllerState.Start), 5) | Bit(Pressed(ControllerState.B), 4);
                if (sixButton && phase == 5)
                {
                    // 0000: a 6-button pad.
                }
                else if (sixButton && phase == 7)
                {
                    lines |= 0x0F;
                }
                else
                {
                    lines |= Bit(Pressed(ControllerState.Down), 1) | Bit(Pressed(ControllerState.Up), 0);
                }
            }

            // Output bits read back what was written; inputs read the pad.
            return (byte)((_data & Control & 0x7F) | (lines & ~Control & 0x7F) | (_data & 0x80));
        }

        public void Save(StateWriter writer)
        {
            writer.WriteByte(_data);
            writer.WriteByte(Control);
            writer.WriteInt32(_count);
            writer.WriteInt64(_lastChange);
        }

        public void Load(StateReader reader)
        {
            _data = reader.ReadByte();
            Control = reader.ReadByte();
            _count = reader.ReadInt32();
            _lastChange = reader.ReadInt64();
        }
    }

    /// <summary>Passes the PSG's deltas (stamped in Z80 cycles) to the shared resampler in master clocks, at a lower level.</summary>
    private sealed class ScaledSink(int clockScale, float gain) : IAudioSink
    {
        public BandLimitedResampler? Target { get; set; }

        public void AddDelta(int clock, float delta) => Target?.AddDelta(clock * clockScale, delta * gain);

        public void EndFrame(int clocks)
        {
            // The console ends the shared frame itself.
        }
    }
}
