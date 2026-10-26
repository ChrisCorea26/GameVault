using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Systems;

namespace GameVault.Core.Apple;

/// <summary>Which Apple II.</summary>
public enum AppleModel
{
    /// <summary>Apple II/II+: 6502, 48 KB plus the Language Card, uppercase text.</summary>
    IIPlus,

    /// <summary>Apple IIe (unenhanced): 6502, 128 KB with the 80-column card.</summary>
    IIe,

    /// <summary>Enhanced Apple IIe: 65C02, lowercase and MouseText.</summary>
    IIeEnhanced,
}

/// <summary>The player's own Apple ROMs (GameVault ships none).</summary>
/// <param name="System">12 KB ($D000–$FFFF, II/II+) or 16 KB ($C000–$FFFF, IIe).</param>
/// <param name="DiskBoot">The Disk II controller's 256-byte boot ROM (slot 6), or null for no disk controller.</param>
public sealed record AppleRoms(byte[] System, byte[]? DiskBoot)
{
    public AppleModel DefaultModel => System.Length == 0x4000 ? AppleModel.IIeEnhanced : AppleModel.IIPlus;

    public void Validate()
    {
        if (System.Length is not (0x3000 or 0x4000))
        {
            throw new RomFormatException("The Apple II ROM must be 12 KB (II/II+) or 16 KB (IIe).");
        }

        if (DiskBoot is { Length: not 256 })
        {
            throw new RomFormatException("The Disk II boot ROM must be 256 bytes.");
        }
    }
}

/// <summary>
/// The Apple II, II+ and IIe (SPEC §7.11, Phase 12): the 6502 (65C02 on the enhanced IIe), memory with the
/// Language Card and the IIe's auxiliary bank, the soft switches, the keyboard, the speaker, paddles and buttons,
/// and a Disk II in slot 6. Every CPU cycle passes through the bus, so the disk and speaker see exact timing.
/// Deterministic, like every core.
/// </summary>
/// <remarks>References: "Apple II Reference Manual" (1979); "Apple IIe Technical Reference Manual" (1985), chapters 2–5.</remarks>
public sealed class AppleConsole : IEmulatedConsole, ICpuBus
{
    public const int CyclesPerLine = 65;
    public const int LinesPerFrame = 262;
    public const int CyclesPerFrame = CyclesPerLine * LinesPerFrame;

    private static readonly StateTag Tag = StateTag.From("APL2");
    private static readonly StateTag SnapshotTag = StateTag.From("GVA2");

    private readonly byte[] _rom;
    private readonly byte[]? _diskRom;
    private readonly byte[] _main = new byte[0x10000];
    private readonly byte[] _aux;
    private readonly byte[] _lcMain = new byte[0x4000]; // $D000 bank 1, $D000 bank 2, $E000–$FFFF
    private readonly byte[] _lcAux;
    private readonly ushort[] _frame = new ushort[AppleVideo.Width * AppleVideo.Height];
    private readonly ControllerState[] _pads = new ControllerState[2];

    // Language Card
    private bool _lcReadRam;
    private bool _lcWriteEnable;
    private bool _lcBank2 = true;
    private bool _lcPrewrite;

    // IIe memory switches
    private bool _store80, _ramRead, _ramWrite, _intCxRom, _altZp, _slotC3Rom, _intC8Rom;

    private AppleVideoMode _video;

    // Keyboard, speaker, paddles
    private byte _keyLatch;
    private bool _anyKeyDown;
    private bool _speaker;
    private long _lastSpeakerToggle;
    private bool _speakerSettled = true;
    private long _paddleTrigger = long.MinValue;
    private long _frameStartCycle;
    private float _lastLevel;
    private int _flashFrames;

    public AppleConsole(AppleRoms roms, AppleModel? model = null)
    {
        roms.Validate();
        Model = model ?? roms.DefaultModel;
        if (Model != AppleModel.IIPlus && roms.System.Length != 0x4000)
        {
            throw new RomFormatException("The Apple IIe needs its 16 KB ROM.");
        }

        _rom = roms.System;
        _diskRom = roms.DiskBoot;
        _aux = Model == AppleModel.IIPlus ? [] : new byte[0x10000];
        _lcAux = Model == AppleModel.IIPlus ? [] : new byte[0x4000];
        Cpu = new Cpu6502(this, Model == AppleModel.IIeEnhanced ? CpuVariant.Cmos65C02 : CpuVariant.Nmos6502);
        Disk = new DiskII();
        _video.Text = true;
        _video.Lowercase = Model == AppleModel.IIeEnhanced;
        RomHash = Fnv(_rom) ^ (Fnv(_diskRom ?? []) * 31);
    }

    public AppleModel Model { get; }

    public Cpu6502 Cpu { get; }

    public DiskII Disk { get; }

    public GameSystem System => GameSystem.AppleII;

    /// <summary>Names the machine (its ROMs) and the disk a save state belongs to.</summary>
    public ulong RomHash { get; private set; }

    public int ScreenWidth => AppleVideo.Width;

    public int ScreenHeight => AppleVideo.Height;

    /// <summary>560×384 dots on a 4:3 screen.</summary>
    public double PixelAspect => 512.0 / 560.0;

    public ReadOnlySpan<ushort> FrameBuffer => _frame;

    public ReadOnlySpan<uint> Palette => AppleVideo.Palette;

    public long FrameCount { get; private set; }

    /// <summary>14.31818 MHz ÷ 14: the Apple's 1.023 MHz.</summary>
    public double CpuClockRate => 14318181.8 / 14;

    public BandLimitedResampler? Audio { get; private set; }

    public int ControllerPorts => 1;

    public IBatteryBacked? Battery => null;

    public ReadOnlySpan<byte> MainMemory => _main;

    public ReadOnlySpan<byte> AuxMemory => _aux;

    public AppleVideoMode Video => _video;

    public BandLimitedResampler EnableAudio(int sampleRate = 48000)
    {
        Audio ??= new BandLimitedResampler(CpuClockRate, sampleRate);
        return Audio;
    }

    /// <summary>Puts a disk in drive 1 or 2 (or takes it out with null). The disk is part of the game's identity.</summary>
    public void InsertDisk(int drive, AppleDisk? disk, ulong diskHash = 0)
    {
        Disk.Insert(drive, disk);
        if (drive == 0 && diskHash != 0)
        {
            RomHash = Fnv(_rom) ^ (Fnv(_diskRom ?? []) * 31) ^ (diskHash * 131);
        }
    }

    public void Power()
    {
        Array.Clear(_main);
        Array.Clear(_aux);
        Array.Clear(_lcMain);
        Array.Clear(_lcAux);

        // Real RAM powers up in a pattern; the Monitor relies on the power-up byte at $3F4 not matching.
        for (var i = 0; i < 0xC000; i += 4)
        {
            _main[i + 2] = _main[i + 3] = 0xFF;
        }

        ResetSwitches();
        Disk.Reset();
        Cpu.Power();
        FrameCount = 0;
        _frameStartCycle = Cpu.CycleCount;
    }

    /// <summary>Ctrl-Reset: the 6502 resets; on the IIe the memory switches go back to their defaults.</summary>
    public void Reset()
    {
        if (Model != AppleModel.IIPlus)
        {
            ResetSwitches();
        }

        Disk.Reset();
        Cpu.Reset();
    }

    private void ResetSwitches()
    {
        _lcReadRam = false;
        _lcWriteEnable = true;
        _lcBank2 = true;
        _lcPrewrite = false;
        _store80 = _ramRead = _ramWrite = _intCxRom = _altZp = _slotC3Rom = _intC8Rom = false;
        _video = new AppleVideoMode { Text = true, Lowercase = Model == AppleModel.IIeEnhanced };
    }

    /// <summary>
    /// A key typed on the Apple's keyboard: ASCII with the high bit clear (Return is 13, the arrows 8 and 21).
    /// The key stays in the latch until the program reads it and clears the strobe.
    /// </summary>
    public void TypeKey(byte ascii)
    {
        if (Model == AppleModel.IIPlus && ascii is >= 0x60 and < 0x7F)
        {
            ascii -= 0x20; // no lowercase on the II+
        }

        _keyLatch = (byte)(ascii | 0x80);
    }

    /// <summary>Whether any key is held (the IIe reads it at $C010).</summary>
    public void SetKeyHeld(bool held) => _anyKeyDown = held;

    /// <summary>
    /// The joystick from a pad: the directions move the stick to its ends, A is button 0 and B is button 1.
    /// </summary>
    public void SetControllerState(int port, ControllerState state)
    {
        if (port is >= 0 and < 2)
        {
            _pads[port] = state;
        }
    }

    public void RunFrame() => RunFrame(audio: true);

    public void RunFrameSilently() => RunFrame(audio: false);

    private bool _audioOn = true;

    private void RunFrame(bool audio)
    {
        _audioOn = audio;
        var end = _frameStartCycle + CyclesPerFrame;
        while (Cpu.CycleCount < end)
        {
            Cpu.Step();
        }

        Disk.Advance(Cpu.CycleCount);
        SettleSpeaker(end);
        if (_audioOn && Audio is { } sink)
        {
            sink.EndFrame((int)(Cpu.CycleCount - _frameStartCycle));
        }

        _frameStartCycle = Cpu.CycleCount;
        if (++_flashFrames >= 16)
        {
            _flashFrames = 0;
        }

        AppleVideo.Render(_frame, _main, _aux, _video, _flashFrames >= 8);
        FrameCount++;
        _audioOn = true;
    }

    // ---------------------------------------------------------------- the 6502's bus (one call per cycle)

    public byte Read(ushort address)
    {
        if (address < 0xC000)
        {
            return RamFor(address, write: false)[address];
        }

        if (address < 0xC100)
        {
            return ReadIo(address);
        }

        if (address < 0xD000)
        {
            return ReadSlotRom(address);
        }

        if (_lcReadRam)
        {
            return LanguageCard()[LcOffset(address)];
        }

        return _rom[address - (0x10000 - _rom.Length)];
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0xC000)
        {
            RamFor(address, write: true)[address] = value;
            return;
        }

        if (address < 0xC100)
        {
            WriteIo(address, value);
            return;
        }

        if (address >= 0xD000 && _lcWriteEnable)
        {
            LanguageCard()[LcOffset(address)] = value;
        }
    }

    public byte PeekMemory(int address) => address switch
    {
        < 0xC000 => RamFor((ushort)address, write: false)[address],
        >= 0xD000 when _lcReadRam => LanguageCard()[LcOffset((ushort)address)],
        >= 0xD000 => _rom[address - (0x10000 - _rom.Length)],
        _ => 0,
    };

    private byte[] RamFor(ushort address, bool write)
    {
        if (_aux.Length == 0)
        {
            return _main;
        }

        if (address < 0x200)
        {
            return _altZp ? _aux : _main;
        }

        if (_store80 && ((address is >= 0x400 and < 0x800) || (_video.HiRes && address is >= 0x2000 and < 0x4000)))
        {
            return _video.Page2 ? _aux : _main;
        }

        return (write ? _ramWrite : _ramRead) ? _aux : _main;
    }

    private byte[] LanguageCard() => _altZp && _lcAux.Length > 0 ? _lcAux : _lcMain;

    /// <summary>$D000–$DFFF is bank 1 (offset 0) or bank 2 (offset $1000); $E000–$FFFF follows at $2000.</summary>
    private int LcOffset(ushort address) => address < 0xE000
        ? (address - 0xD000) + (_lcBank2 ? 0x1000 : 0)
        : address - 0xE000 + 0x2000;

    private byte ReadSlotRom(ushort address)
    {
        if (_rom.Length == 0x4000)
        {
            // IIe: internal ROM for $C100–$CFFF when INTCXROM is on; $C300 is the 80-column firmware unless
            // SLOTC3ROM; touching it maps the internal $C800 space until $CFFF is accessed.
            if (address == 0xCFFF)
            {
                _intC8Rom = false;
            }

            var internalRom = _intCxRom
                || (address is >= 0xC300 and < 0xC400 && !_slotC3Rom)
                || (address >= 0xC800 && _intC8Rom);
            if (address is >= 0xC300 and < 0xC400 && !_slotC3Rom)
            {
                _intC8Rom = true;
            }

            if (internalRom)
            {
                return _rom[address - 0xC000];
            }
        }

        if (address is >= 0xC600 and < 0xC700 && _diskRom is not null)
        {
            return _diskRom[address - 0xC600];
        }

        return 0;
    }

    // ---------------------------------------------------------------- soft switches

    private byte ReadIo(ushort address)
    {
        var low = address & 0xFF;
        switch (low)
        {
            case < 0x10:
                return _keyLatch;
            case 0x10:
                _keyLatch &= 0x7F;
                return (byte)(_anyKeyDown ? 0x80 : 0);
            case >= 0x11 and < 0x20 when _aux.Length > 0:
                return (byte)((Status(low) ? 0x80 : 0) | (_keyLatch & 0x7F));
            case >= 0x30 and < 0x40:
                ToggleSpeaker();
                return 0;
            case >= 0x50 and < 0x60:
                VideoSwitch(low);
                return 0;
            case 0x61:
                return Button((_pads[0] & ControllerState.A) != 0);
            case 0x62:
                return Button((_pads[0] & ControllerState.B) != 0);
            case 0x63:
                return Button((_pads[1] & ControllerState.A) != 0);
            case >= 0x64 and < 0x68:
                return (byte)(Cpu.CycleCount < _paddleTrigger + PaddleValue(low - 0x64) * 11 ? 0x80 : 0);
            case 0x70:
                _paddleTrigger = Cpu.CycleCount;
                return 0;
            case >= 0x80 and < 0x90:
                LanguageCardSwitch(low, read: true);
                return 0;
            case >= 0xE0 and < 0xF0:
                return Disk.Access(low & 0x0F, write: false, 0, Cpu.CycleCount);
            default:
                return 0;
        }
    }

    private void WriteIo(ushort address, byte value)
    {
        var low = address & 0xFF;
        switch (low)
        {
            case < 0x10 when _aux.Length > 0:
                IIeSwitch(low);
                break;
            case 0x10:
                _keyLatch &= 0x7F;
                break;
            case >= 0x30 and < 0x40:
                ToggleSpeaker();
                break;
            case >= 0x50 and < 0x60:
                VideoSwitch(low);
                break;
            case 0x70:
                _paddleTrigger = Cpu.CycleCount;
                break;
            case >= 0x80 and < 0x90:
                LanguageCardSwitch(low, read: false);
                break;
            case >= 0xE0 and < 0xF0:
                Disk.Access(low & 0x0F, write: true, value, Cpu.CycleCount);
                break;
        }
    }

    private static byte Button(bool pressed) => (byte)(pressed ? 0x80 : 0);

    /// <summary>The stick from the pad: 0 at one end, 255 at the other, 128 centred.</summary>
    private int PaddleValue(int paddle)
    {
        var pad = _pads[paddle >> 1];
        return (paddle & 1) == 0
            ? (pad & ControllerState.Left) != 0 ? 0 : (pad & ControllerState.Right) != 0 ? 255 : 128
            : (pad & ControllerState.Up) != 0 ? 0 : (pad & ControllerState.Down) != 0 ? 255 : 128;
    }

    private void IIeSwitch(int low)
    {
        var on = (low & 1) != 0;
        switch (low >> 1)
        {
            case 0: _store80 = on; break;
            case 1: _ramRead = on; break;
            case 2: _ramWrite = on; break;
            case 3: _intCxRom = on; break;
            case 4: _altZp = on; break;
            case 5: _slotC3Rom = on; break;
            case 6: _video.Col80 = on; break;
            case 7: _video.AltCharset = on; break;
        }

        _video.Store80 = _store80;
    }

    private bool Status(int low) => low switch
    {
        0x11 => _lcBank2,
        0x12 => _lcReadRam,
        0x13 => _ramRead,
        0x14 => _ramWrite,
        0x15 => _intCxRom,
        0x16 => _altZp,
        0x17 => _slotC3Rom,
        0x18 => _store80,
        0x19 => (Cpu.CycleCount - _frameStartCycle) < 192 * CyclesPerLine, // high while drawing, low in vertical blank
        0x1A => _video.Text,
        0x1B => _video.Mixed,
        0x1C => _video.Page2,
        0x1D => _video.HiRes,
        0x1E => _video.AltCharset,
        _ => _video.Col80,
    };

    private void VideoSwitch(int low)
    {
        var on = (low & 1) != 0;
        switch (low)
        {
            case 0x50 or 0x51: _video.Text = on; break;
            case 0x52 or 0x53: _video.Mixed = on; break;
            case 0x54 or 0x55: _video.Page2 = on; break;
            case 0x56 or 0x57: _video.HiRes = on; break;
            case 0x5E: _video.DoubleHiRes = true; break;  // annunciator 3 off: double hi-res on (IIe)
            case 0x5F: _video.DoubleHiRes = false; break;
        }
    }

    /// <summary>
    /// $C080–$C08F: bit 3 picks bank 1 or 2 at $D000; bits 0–1 pick read RAM/ROM and write; writing needs two reads
    /// of an odd address in a row.
    /// </summary>
    private void LanguageCardSwitch(int low, bool read)
    {
        _lcBank2 = (low & 0x08) == 0;
        var mode = low & 3;
        _lcReadRam = mode is 0 or 3;
        if ((low & 1) != 0)
        {
            if (read && _lcPrewrite)
            {
                _lcWriteEnable = true;
            }

            _lcPrewrite = read;
        }
        else
        {
            _lcWriteEnable = false;
            _lcPrewrite = false;
        }
    }

    // ---------------------------------------------------------------- speaker

    private void ToggleSpeaker()
    {
        _speaker = !_speaker;
        _lastSpeakerToggle = Cpu.CycleCount;
        _speakerSettled = false;
        SetLevel(_speaker ? 0.25f : -0.25f);
    }

    /// <summary>A speaker left alone drifts back to rest, so a stopped tone doesn't leave a DC offset.</summary>
    private void SettleSpeaker(long now)
    {
        if (!_speakerSettled && now - _lastSpeakerToggle > 4096)
        {
            _speakerSettled = true;
            SetLevel(0);
        }
    }

    private void SetLevel(float level)
    {
        if (_audioOn && Audio is { } sink && level != _lastLevel)
        {
            sink.AddDelta((int)(Cpu.CycleCount - _frameStartCycle), level - _lastLevel);
        }

        _lastLevel = level;
    }

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
        Disk.SaveState(writer);
        writer.WriteBytes(_main);
        writer.WriteBytes(_aux);
        writer.WriteBytes(_lcMain);
        writer.WriteBytes(_lcAux);
        bool[] switches = [_lcReadRam, _lcWriteEnable, _lcBank2, _lcPrewrite, _store80, _ramRead, _ramWrite, _intCxRom, _altZp, _slotC3Rom, _intC8Rom,
            _video.Text, _video.Mixed, _video.Page2, _video.HiRes, _video.Col80, _video.AltCharset, _video.DoubleHiRes, _speaker, _anyKeyDown];
        foreach (var on in switches)
        {
            writer.WriteBool(on);
        }

        writer.WriteByte(_keyLatch);
        writer.WriteInt64(_paddleTrigger);
        writer.WriteInt64(_frameStartCycle);
        writer.WriteInt64(FrameCount);
        writer.WriteInt32(_flashFrames);
        writer.WriteByte((byte)_pads[0]);
        writer.WriteByte((byte)_pads[1]);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Disk.LoadState(reader);
        reader.ReadBytes(_main);
        reader.ReadBytes(_aux);
        reader.ReadBytes(_lcMain);
        reader.ReadBytes(_lcAux);
        _lcReadRam = reader.ReadBool();
        _lcWriteEnable = reader.ReadBool();
        _lcBank2 = reader.ReadBool();
        _lcPrewrite = reader.ReadBool();
        _store80 = reader.ReadBool();
        _ramRead = reader.ReadBool();
        _ramWrite = reader.ReadBool();
        _intCxRom = reader.ReadBool();
        _altZp = reader.ReadBool();
        _slotC3Rom = reader.ReadBool();
        _intC8Rom = reader.ReadBool();
        _video.Text = reader.ReadBool();
        _video.Mixed = reader.ReadBool();
        _video.Page2 = reader.ReadBool();
        _video.HiRes = reader.ReadBool();
        _video.Col80 = reader.ReadBool();
        _video.AltCharset = reader.ReadBool();
        _video.DoubleHiRes = reader.ReadBool();
        _video.Store80 = _store80;
        _speaker = reader.ReadBool();
        _anyKeyDown = reader.ReadBool();
        _keyLatch = reader.ReadByte();
        _paddleTrigger = reader.ReadInt64();
        _frameStartCycle = reader.ReadInt64();
        FrameCount = reader.ReadInt64();
        _flashFrames = reader.ReadInt32();
        _pads[0] = (ControllerState)reader.ReadByte();
        _pads[1] = (ControllerState)reader.ReadByte();
        reader.EndChunk();
    }

    /// <summary>Identifies a disk image inside save states (not a security hash).</summary>
    public static ulong HashDisk(ReadOnlySpan<byte> image) => Fnv(image);

    /// <summary>Memory in RetroAchievements' layout (rcheevos consoleinfo.c): main RAM, then auxiliary RAM at $10000.</summary>
    public byte ReadAchievementMemory(int address) => address switch
    {
        < 0x10000 => _main[address],
        < 0x20000 when _aux.Length > 0 => _aux[address - 0x10000],
        _ => 0,
    };

    private static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}
