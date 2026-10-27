using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.Z80;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Sega;

/// <summary>
/// The SG-1000 / SC-3000 and the Master System (SPEC §7.11, Phase 11): a Z80 at 3.58 MHz, the VDP, the SN76489
/// and two joypads. Runs a line (228 Z80 cycles) at a time. Deterministic, like every core.
/// </summary>
/// <remarks>
/// The Master System boots cartridges without its BIOS: GameVault starts the cartridge with the state the BIOS
/// leaves (stack at $DFF0, memory control $AB, interrupt mode 1), as documented on SMS Power!. The console
/// behaves like an export (US/European) model, so region checks that read back the TH lines pass.
/// </remarks>
public sealed class SegaConsole : IEmulatedConsole, IZ80Bus
{
    public const int CyclesPerLine = 228;

    private static readonly StateTag Tag = StateTag.From("SEGA");
    private static readonly StateTag SnapshotTag = StateTag.From("GVSG");

    private readonly byte[] _ram;
    private readonly ControllerState[] _pads = new ControllerState[2];
    private int _lineCycles;
    private bool _pausePressed;
    private byte _ioControl = 0xFF;
    private byte _memoryControl;
    private int _frameCycles;
    private float _lastFm;
    private readonly ushort[] _keyRows = new ushort[7];
    private byte _ppiPortC = 0x07;

    /// <param name="fmUnit">Give a Master System the FM Sound Unit (the Japanese model's YM2413).</param>
    /// <param name="keyboard">Make an SG-1000 an SC-3000, with its keyboard on the 8255 PPI.</param>
    public SegaConsole(GameSystem system, SegaCartridge cartridge, bool pal = false, bool fmUnit = false, bool keyboard = false)
    {
        if (system is not (GameSystem.Sg1000 or GameSystem.MasterSystem))
        {
            throw new ArgumentOutOfRangeException(nameof(system), system, "SG-1000 or Master System only.");
        }

        System = system;
        Cartridge = cartridge;
        Pal = pal;
        _ram = new byte[system == GameSystem.MasterSystem ? 0x2000 : 0x0400];
        Vdp = new SegaVdp(system == GameSystem.MasterSystem ? VdpModel.Sms2 : VdpModel.Tms9918, pal);
        Psg = new Sn76489();
        Fm = fmUnit && system == GameSystem.MasterSystem ? new Ym2413() : null;
        HasKeyboard = keyboard && system == GameSystem.Sg1000;
        Cpu = new Z80Cpu(this);
        RomHash = HashRom(cartridge.Rom);
    }

    /// <summary>A console for a ROM file, with the board detected from the ROM (a 512-byte copier header is skipped).</summary>
    /// <param name="fmUnit">The FM Sound Unit: null gives it to Japanese Master System cartridges (by their header).</param>
    /// <param name="keyboard">An SC-3000 (an SG-1000 with its keyboard): for .sc files and BASIC cartridges.</param>
    public static SegaConsole FromBytes(GameSystem system, ReadOnlySpan<byte> file, SegaMapper? mapper = null, bool pal = false, bool? fmUnit = null, bool keyboard = false)
    {
        var rom = (file.Length % 0x4000 == 512 ? file[512..] : file).ToArray();
        return new SegaConsole(system, new SegaCartridge(rom, mapper ?? SegaCartridge.DetectMapper(rom)), pal, fmUnit ?? SegaCartridge.IsJapaneseMasterSystem(rom), keyboard);
    }

    public GameSystem System { get; }

    public bool Pal { get; }

    public Z80Cpu Cpu { get; }

    public SegaVdp Vdp { get; }

    public Sn76489 Psg { get; }

    /// <summary>The FM Sound Unit, on a Japanese Master System; null otherwise.</summary>
    public Ym2413? Fm { get; }

    /// <summary>
    /// True for the SC-3000 (outstanding-work plan §8): its keyboard is a matrix of seven rows of twelve keys on the
    /// 8255 PPI. Port $DE's low three bits pick a row; $DC reads the row's first eight keys and $DD the other four,
    /// active low. Row 7 is the joypads, so games read them as on the SG-1000 (SMS Power! "SC-3000 keyboard").
    /// </summary>
    public bool HasKeyboard { get; }

    /// <summary>
    /// The SC-3000's keys held now: one 12-bit mask per row (bits 0–7 are PA0–PA7, 8–11 PB0–PB3), set while held.
    /// The host sets it every frame from the PC keyboard.
    /// </summary>
    public void SetKeyboardRows(ReadOnlySpan<ushort> rows)
    {
        for (var row = 0; row < _keyRows.Length; row++)
        {
            _keyRows[row] = row < rows.Length ? (ushort)(rows[row] & 0x0FFF) : (ushort)0;
        }
    }

    public SegaCartridge Cartridge { get; }

    public ulong RomHash { get; }

    public ReadOnlySpan<ushort> FrameBuffer => Vdp.FrameBuffer;

    public ReadOnlySpan<uint> Palette => SegaVdp.Palette;

    public long FrameCount => Vdp.FrameCount;

    /// <summary>3.579545 MHz (NTSC) or 3.546893 MHz (PAL).</summary>
    public double CpuClockRate => Pal ? 3546893.0 : 3579545.0;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 2;

    public IBatteryBacked? Battery => Cartridge.HasBattery ? Cartridge : null;

    /// <summary>System RAM (8 KB on the Master System, 1 KB on the SG-1000).</summary>
    public ReadOnlySpan<byte> Ram => _ram;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate);
        Psg.AudioSink = Audio;
        return Audio;
    }

    public void Power()
    {
        Array.Clear(_ram);
        Vdp.Power();
        Psg.Power();
        Fm?.Power();
        Cartridge.Reset();
        Cpu.Power();
        _frameCycles = 0;
        _lastFm = 0;
        _ppiPortC = 0x07;
        Array.Clear(_keyRows);
        _lineCycles = 0;
        _pausePressed = false;
        _ioControl = 0xFF;
        _memoryControl = 0;
        if (System == GameSystem.MasterSystem)
        {
            // What the BIOS leaves behind when it hands over to the cartridge.
            Cpu.SP = 0xDFF0;
            Cpu.InterruptMode = 1;
            _memoryControl = 0xAB;
        }
    }

    /// <summary>The Master System's reset button (the SG-1000 has none, so it restarts the cartridge).</summary>
    public void Reset()
    {
        Cpu.Reset();
        Cartridge.Reset();
        if (System == GameSystem.MasterSystem)
        {
            Cpu.SP = 0xDFF0;
            Cpu.InterruptMode = 1;
        }
    }

    /// <summary>
    /// Port 0 and 1 joypads. B is Button 1, A is Button 2, and Start is the console's Pause button (an NMI on
    /// both consoles).
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if (port is < 0 or > 1)
        {
            return;
        }

        var pause = (state & ControllerState.Start) != 0;
        if (port == 0 && pause && (_pads[0] & ControllerState.Start) == 0)
        {
            _pausePressed = true;
        }

        _pads[port] = state;
    }

    public void RunFrame()
    {
        for (var line = 0; line < Vdp.LinesPerFrame; line++)
        {
            RunLine(line);
        }

        Psg.EndAudioFrame();
        _frameCycles = 0;
    }

    public void RunFrameSilently()
    {
        var sink = Psg.AudioSink;
        Psg.AudioSink = null;
        try
        {
            RunFrame();
        }
        finally
        {
            Psg.AudioSink = sink;
        }
    }

    private void RunLine(int line)
    {
        Vdp.StartLine(line);
        if (_pausePressed)
        {
            _pausePressed = false;
            Cpu.RaiseNmi();
        }

        while (_lineCycles < CyclesPerLine)
        {
            Cpu.IrqLine = Vdp.Irq;
            var cycles = Cpu.Step();
            Psg.Run(cycles);
            if (Fm is { } fm)
            {
                RunFm(fm, cycles);
            }

            _lineCycles += cycles;
        }

        _lineCycles -= CyclesPerLine;
    }

    /// <summary>The FM unit's samples join the PSG's in the same audio frame, as changes stamped with the Z80 cycle.</summary>
    private void RunFm(Ym2413 fm, int cycles)
    {
        for (var i = 0; i < cycles; i++, _frameCycles++)
        {
            if (fm.Clock() && Psg.AudioSink is { } sink && fm.Output != _lastFm)
            {
                sink.AddDelta(_frameCycles, fm.Output - _lastFm);
                _lastFm = fm.Output;
            }
        }
    }

    // ---------------------------------------------------------------- the Z80's view

    public byte Read(ushort address)
    {
        if (address < 0xC000)
        {
            return Cartridge.Read(address);
        }

        return _ram[address & (_ram.Length - 1)];
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0xC000)
        {
            Cartridge.Write(address, value);
            return;
        }

        _ram[address & (_ram.Length - 1)] = value;
        if (address >= 0xFFFC)
        {
            Cartridge.WriteRegister(address, value);
        }
    }

    public byte PeekMemory(int address) => Read((ushort)address);

    /// <summary>
    /// Memory in RetroAchievements' layout (rcheevos consoleinfo.c). Master System: $0000–$1FFF system RAM, then
    /// $2000–$9FFF cartridge RAM. SG-1000: $0000–$03FF RAM, then the expansion RAM some boards have.
    /// </summary>
    public byte ReadAchievementMemory(int address)
    {
        if (System == GameSystem.MasterSystem)
        {
            return address switch
            {
                < 0x2000 => _ram[address],
                < 0xA000 => Cartridge.BatteryRam[address - 0x2000],
                _ => 0,
            };
        }

        return address switch
        {
            < 0x0400 => _ram[address],
            >= 0x2000 and < 0x4000 when Cartridge.ExpansionRamAt == 0x2000 => Cartridge.Read((ushort)address),
            >= 0x4000 and < 0x6000 when Cartridge.ExpansionRamAt == 0x8000 => Cartridge.Read((ushort)(address + 0x4000)),
            _ => 0,
        };
    }

    public byte In(ushort port)
    {
        var low = (byte)port;
        if (Fm is { } fm && low == 0xF2)
        {
            return fm.Control; // the FM unit answers its control port, so games can find it
        }

        if (HasKeyboard && (low & 0xC1) is 0xC0 or 0xC1 && (_ppiPortC & 7) < 7)
        {
            // A keyboard row: PA0–PA7 on $DC, PB0–PB3 on $DD (the other bits read high).
            var keys = _keyRows[_ppiPortC & 7];
            return (low & 1) == 0 ? (byte)~keys : (byte)(~(keys >> 8) | 0xF0);
        }

        switch (low & 0xC1)
        {
            case 0x40:
                return Vdp.VCounter;
            case 0x41:
                return SegaVdp.HCounter(_lineCycles);
            case 0x80:
                return Vdp.ReadData();
            case 0x81:
                return Vdp.ReadControl();
            case 0xC0:
                return PortA();
            case 0xC1:
                return PortB();
            default:
                return 0xFF;
        }
    }

    public void Out(ushort port, byte value)
    {
        var low = (byte)port;
        if (HasKeyboard && (low & 0xC2) == 0xC2)
        {
            if ((low & 1) == 0)
            {
                _ppiPortC = value; // $DE: port C, the row select
            }
            else if ((value & 0x80) == 0)
            {
                // $DF with bit 7 clear sets or clears one bit of port C.
                var bit = (value >> 1) & 7;
                _ppiPortC = (byte)((value & 1) != 0 ? _ppiPortC | (1 << bit) : _ppiPortC & ~(1 << bit));
            }

            return;
        }

        if (Fm is { } fm && low is 0xF0 or 0xF1 or 0xF2)
        {
            switch (low)
            {
                case 0xF0: fm.WriteRegister(value); break;
                case 0xF1: fm.WriteData(value); break;
                default: fm.WriteControl(value); break;
            }

            return;
        }

        switch (low & 0xC1)
        {
            case 0x00:
                _memoryControl = value;
                break;
            case 0x01:
                _ioControl = value;
                break;
            case 0x40:
            case 0x41:
                Psg.Write(value);
                break;
            case 0x80:
                Vdp.WriteData(value);
                break;
            case 0x81:
                Vdp.WriteControl(value);
                break;
        }
    }

    /// <summary>$DC: player 1's directions and buttons, then player 2's up and down (active low).</summary>
    private byte PortA()
    {
        var p1 = Bits(_pads[0]);
        var p2 = Bits(_pads[1]);
        return (byte)~(p1 | (p2 & 0x03) << 6);
    }

    /// <summary>$DD: player 2's left, right and buttons, the reset button (not pressed), and the TH lines.</summary>
    private byte PortB()
    {
        var p2 = Bits(_pads[1]);
        var value = (byte)~((p2 >> 2) & 0x0F) & 0x3F;
        if (System == GameSystem.MasterSystem)
        {
            // Export consoles read back TH levels set as outputs in port $3F (region detection).
            var thA = (_ioControl & 0x02) == 0 ? (_ioControl & 0x20) != 0 : true;
            var thB = (_ioControl & 0x08) == 0 ? (_ioControl & 0x80) != 0 : true;
            value |= (byte)((thA ? 0x40 : 0) | (thB ? 0x80 : 0));
        }
        else
        {
            value |= 0xC0;
        }

        return (byte)value;
    }

    /// <summary>Up, down, left, right, Button 1 (B), Button 2 (A) as bits 0–5, set when pressed.</summary>
    private static int Bits(ControllerState state) =>
        ((state & ControllerState.Up) != 0 ? 0x01 : 0)
        | ((state & ControllerState.Down) != 0 ? 0x02 : 0)
        | ((state & ControllerState.Left) != 0 ? 0x04 : 0)
        | ((state & ControllerState.Right) != 0 ? 0x08 : 0)
        | ((state & ControllerState.B) != 0 ? 0x10 : 0)
        | ((state & ControllerState.A) != 0 ? 0x20 : 0);

    // ---------------------------------------------------------------- state

    public void SaveSnapshot(StateWriter writer)
    {
        writer.BeginChunk(SnapshotTag, 1);
        writer.WriteUInt64(RomHash);
        SaveState(writer);
        writer.EndChunk();
    }

    public byte[] SaveSnapshot()
    {
        var writer = new StateWriter();
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
        Vdp.SaveState(writer);
        Psg.SaveState(writer);
        Cartridge.SaveState(writer);
        writer.WriteBytes(_ram);
        writer.WriteInt32(_lineCycles);
        writer.WriteBool(_pausePressed);
        writer.WriteByte(_ioControl);
        writer.WriteByte(_memoryControl);
        writer.WriteByte((byte)_pads[0]);
        writer.WriteByte((byte)_pads[1]);
        if (Fm is { } fm)
        {
            // Only consoles with the FM unit have this, so other games' states are unchanged.
            fm.SaveState(writer);
            writer.WriteInt32(_frameCycles);
            writer.WriteInt32(BitConverter.SingleToInt32Bits(_lastFm));
        }

        if (HasKeyboard)
        {
            writer.WriteByte(_ppiPortC);
            foreach (var row in _keyRows)
            {
                writer.WriteUInt16(row);
            }
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Vdp.LoadState(reader);
        Psg.LoadState(reader);
        Cartridge.LoadState(reader);
        reader.ReadBytes(_ram);
        _lineCycles = reader.ReadInt32();
        _pausePressed = reader.ReadBool();
        _ioControl = reader.ReadByte();
        _memoryControl = reader.ReadByte();
        _pads[0] = (ControllerState)reader.ReadByte();
        _pads[1] = (ControllerState)reader.ReadByte();
        if (Fm is { } fm)
        {
            fm.LoadState(reader);
            _frameCycles = reader.ReadInt32();
            _lastFm = BitConverter.Int32BitsToSingle(reader.ReadInt32());
        }

        if (HasKeyboard)
        {
            _ppiPortC = reader.ReadByte();
            for (var row = 0; row < _keyRows.Length; row++)
            {
                _keyRows[row] = (ushort)(reader.ReadUInt16() & 0x0FFF);
            }
        }

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
