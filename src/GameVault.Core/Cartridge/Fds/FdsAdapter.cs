using GameVault.Core.Audio;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Fds;

/// <summary>
/// The Famicom Disk System RAM adapter (NESdev Wiki: "Family Computer Disk System"), iNES mapper 20:
/// <list type="bullet">
/// <item>32 KB of PRG-RAM at $6000-$DFFF and the player's own 8 KB BIOS at $E000-$FFFF;</item>
/// <item>8 KB of CHR-RAM and mirroring from $4025 bit 3;</item>
/// <item>a 16-bit CPU-cycle timer IRQ ($4020-$4022);</item>
/// <item>the disk drive, one byte every <see cref="CyclesPerByte"/> CPU cycles, with transfer IRQs;</item>
/// <item>the wavetable sound channel (<see cref="FdsAudio"/>).</item>
/// </list>
/// Drive timing where the wiki is silent (spin-up delay, bytes per transfer) follows Mesen's long-tested
/// implementation.
/// </summary>
public sealed class FdsAdapter : MapperBase
{
    /// <summary>About 96.4 kbit/s: 8 bits every ~149 CPU cycles.</summary>
    public const int CyclesPerByte = 149;

    /// <summary>The head's return to the start of the disk after the motor turns on.</summary>
    private const int SpinUpCycles = 50000;

    /// <summary>A side switch keeps the drive empty for half a second, which games and the BIOS expect.</summary>
    public const int SwitchDelayCycles = 900_000;

    private const int NoDisk = -1;

    private readonly byte[][] _tracks;
    private readonly FdsAudio _audio = new();

    // $4020-$4023
    private int _timerReload;
    private int _timerCounter;
    private bool _timerRepeat;
    private bool _timerEnabled;
    private bool _timerIrq;
    private bool _diskRegisters;
    private bool _soundRegisters;

    // $4024-$4025 and the drive
    private byte _writeData;
    private byte _readData;
    private bool _motorOn;
    private bool _resetTransfer;
    private bool _readMode = true;
    private bool _crcControl;
    private bool _transferEnabled;
    private bool _diskIrqEnabled;
    private bool _diskIrq;
    private bool _transferComplete;
    private bool _gapEnded;
    private bool _scanning;
    private bool _endOfHead = true;
    private int _crc;
    private int _delay;
    private int _position;
    private int _side;
    private int _pendingSide = NoDisk;
    private int _switchDelay;
    private byte _externalOut;

    public FdsAdapter(INesRom biosRom, FdsImage image)
        : base(biosRom, "FDS ")
    {
        _tracks = image.Sides.Select(side => FdsDisk.BuildTrack(side)).ToArray();
        _side = 0;
        Mirroring = Mirroring.Horizontal;
        SetPrg8K(3, 0);
    }

    /// <summary>The synthetic cartridge description for an FDS console: the BIOS as PRG, 32 KB RAM, CHR-RAM.</summary>
    public static INesRom BiosRom(ReadOnlySpan<byte> bios)
    {
        if (bios.Length != 0x2000)
        {
            throw new RomFormatException("The Disk System BIOS (disksys.rom) must be exactly 8 KB.");
        }

        return new INesRom
        {
            PrgRom = bios.ToArray(),
            ChrRom = [],
            Mapper = 20,
            Mirroring = Mirroring.Horizontal,
            PrgRamSize = 0x8000,
            ChrRamSize = 0x2000,
        };
    }

    public override bool HasCycleHooks => true;

    public override bool IrqPending => _timerIrq || _diskIrq;

    public override IExpansionAudio ExpansionAudio => _audio;

    /// <summary>Disk sides in the image (side A of disk 1, side B of disk 1, side A of disk 2, ...).</summary>
    public int SideCount => _tracks.Length;

    /// <summary>The side in the drive, or null while it is empty (ejected or between sides).</summary>
    public int? InsertedSide => _side == NoDisk ? null : _side;

    /// <summary>The drive's tracks, including anything the game wrote. Used for the separate disk save.</summary>
    public IReadOnlyList<byte[]> Tracks => _tracks;

    /// <summary>True once the game has written to the disk.</summary>
    public bool IsDiskModified { get; private set; }

    /// <summary>The head's byte position on the current track (for tests and the debugger).</summary>
    public int HeadPosition => _position;

    public void Eject()
    {
        _side = NoDisk;
        _pendingSide = NoDisk;
        _switchDelay = 0;
    }

    /// <summary>Ejects the current side and inserts <paramref name="side"/> after <see cref="SwitchDelayCycles"/>.</summary>
    public void InsertSide(int side)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(side);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(side, _tracks.Length);
        _side = NoDisk;
        _pendingSide = side;
        _switchDelay = SwitchDelayCycles;
    }

    /// <summary>Restores disk writes from a save made with <see cref="FdsDiskPatch.Create"/>.</summary>
    public void ApplyDiskPatch(ReadOnlySpan<byte> patch)
    {
        FdsDiskPatch.Apply(patch, _tracks);
        IsDiskModified = patch.Length > 4;
    }

    // ------------------------------------------------------------------ CPU bus

    public override byte CpuRead(ushort address, byte openBus)
    {
        if (address >= 0xE000)
        {
            return ReadPrgRom(address);
        }

        if (address >= 0x6000)
        {
            return PrgRam[address - 0x6000];
        }

        if (address is >= 0x4040 and <= 0x4097)
        {
            return _soundRegisters ? _audio.Read(address, openBus) : openBus;
        }

        if (!_diskRegisters)
        {
            return openBus;
        }

        switch (address)
        {
            case 0x4030:
            {
                // Bit 7 is the byte transfer flag (NESdev Wiki); bit 1 mirrors it, as Mesen reports it there too.
                var value = (byte)(openBus & 0x24);
                value |= (byte)(_timerIrq ? 0x01 : 0);
                value |= (byte)(_transferComplete ? 0x82 : 0);
                value |= (byte)(Mirroring == Mirroring.Horizontal ? 0x08 : 0);
                value |= (byte)(_crcControl && _crc != 0 ? 0x10 : 0);
                value |= (byte)(_endOfHead ? 0x40 : 0);
                _transferComplete = false;
                _timerIrq = false;
                _diskIrq = false;
                return value;
            }

            case 0x4031:
                _transferComplete = false;
                _diskIrq = false;
                return _readData;
            case 0x4032:
            {
                // Bit 0: no disk; bit 1: not ready; bit 2: write-protected (or no disk).
                var value = (byte)(openBus & 0xF8);
                if (_side == NoDisk)
                {
                    value |= 0x07;
                }
                else if (!_scanning)
                {
                    value |= 0x02;
                }

                return value;
            }

            case 0x4033:
                return (byte)(0x80 | (_externalOut & 0x7F)); // bit 7: the drive's batteries are good
            default:
                return openBus;
        }
    }

    public override void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0xE000)
        {
            PrgRam[address - 0x6000] = value;
            return;
        }

        if (address is >= 0x4040 and <= 0x4097)
        {
            if (_soundRegisters)
            {
                _audio.Write(address, value);
            }

            return;
        }

        switch (address)
        {
            case 0x4020:
                _timerReload = (_timerReload & 0xFF00) | value;
                return;
            case 0x4021:
                _timerReload = (_timerReload & 0x00FF) | (value << 8);
                return;
            case 0x4023:
                _diskRegisters = (value & 1) != 0;
                _soundRegisters = (value & 2) != 0;
                if (!_diskRegisters)
                {
                    _timerEnabled = false;
                    _timerIrq = false;
                    _diskIrq = false;
                }

                return;
        }

        if (!_diskRegisters)
        {
            return;
        }

        switch (address)
        {
            case 0x4022:
                _timerRepeat = (value & 1) != 0;
                _timerEnabled = (value & 2) != 0;
                if (_timerEnabled)
                {
                    _timerCounter = _timerReload;
                }
                else
                {
                    _timerIrq = false;
                }

                break;
            case 0x4024:
                _writeData = value;
                _transferComplete = false;
                _diskIrq = false;
                break;
            case 0x4025:
                _motorOn = (value & 0x01) != 0;
                _resetTransfer = (value & 0x02) != 0;
                _readMode = (value & 0x04) != 0;
                Mirroring = (value & 0x08) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                _crcControl = (value & 0x10) != 0;
                _transferEnabled = (value & 0x40) != 0;
                _diskIrqEnabled = (value & 0x80) != 0;
                _diskIrq = false;
                break;
            case 0x4026:
                _externalOut = value;
                break;
        }
    }

    protected override void WriteRegister(ushort address, byte value)
    {
    }

    // ------------------------------------------------------------------ per cycle

    public override void OnCpuCycle()
    {
        if (_timerEnabled && _diskRegisters)
        {
            if (_timerCounter == 0)
            {
                _timerIrq = true;
                _timerCounter = _timerReload;
                if (!_timerRepeat)
                {
                    _timerEnabled = false;
                }
            }
            else
            {
                _timerCounter--;
            }
        }

        if (_switchDelay > 0 && --_switchDelay == 0)
        {
            _side = _pendingSide;
            _pendingSide = NoDisk;
        }

        ClockDrive();
    }

    private void ClockDrive()
    {
        if (_side == NoDisk || !_motorOn)
        {
            _endOfHead = true;
            _scanning = false;
            return;
        }

        if (_resetTransfer && !_scanning)
        {
            return;
        }

        if (_endOfHead)
        {
            _delay = SpinUpCycles;
            _endOfHead = false;
            _position = 0;
            _gapEnded = false;
            return;
        }

        if (_delay > 0)
        {
            _delay--;
            return;
        }

        _scanning = true;
        var track = _tracks[_side];
        var needIrq = _diskIrqEnabled;
        if (_readMode)
        {
            var data = track[_position];
            if (!_transferEnabled)
            {
                _gapEnded = false;
            }
            else if (data != 0 && !_gapEnded)
            {
                // The $80 that ends a gap starts the block and its CRC, but raises no IRQ.
                _gapEnded = true;
                needIrq = false;
                _crc = 0;
            }

            if (_gapEnded)
            {
                // CRC-16/KERMIT over the $80, the block and its stored CRC comes to 0 for a good block.
                _crc = FdsDisk.Update(_crc, data);
                _transferComplete = true;
                _readData = data;
                _diskIrq |= needIrq;
            }
        }
        else
        {
            var data = (byte)0;
            if (!_crcControl)
            {
                _transferComplete = true;
                data = _writeData;
                _diskIrq |= needIrq;
            }

            if (!_transferEnabled)
            {
                data = 0;
                _crc = 0;
            }
            else if (!_crcControl)
            {
                _crc = FdsDisk.Update(_crc, data);
            }
            else
            {
                // The reflected CRC goes out low byte first.
                data = (byte)_crc;
                _crc >>= 8;
            }

            track[_position] = data;
            IsDiskModified = true;
            _gapEnded = false;
        }

        _position++;
        if (_position >= track.Length)
        {
            _motorOn = false;
            _endOfHead = true;
        }
        else
        {
            _delay = CyclesPerByte - 1;
        }
    }

    // ------------------------------------------------------------------ state

    protected override void SaveRegisters(StateWriter writer)
    {
        foreach (var track in _tracks)
        {
            writer.WriteBytes(track);
        }

        _audio.Save(writer);
        writer.WriteInt32(_timerReload);
        writer.WriteInt32(_timerCounter);
        writer.WriteBool(_timerRepeat);
        writer.WriteBool(_timerEnabled);
        writer.WriteBool(_timerIrq);
        writer.WriteBool(_diskRegisters);
        writer.WriteBool(_soundRegisters);
        writer.WriteByte(_writeData);
        writer.WriteByte(_readData);
        writer.WriteBool(_motorOn);
        writer.WriteBool(_resetTransfer);
        writer.WriteBool(_readMode);
        writer.WriteBool(_crcControl);
        writer.WriteBool(_transferEnabled);
        writer.WriteBool(_diskIrqEnabled);
        writer.WriteBool(_diskIrq);
        writer.WriteBool(_transferComplete);
        writer.WriteBool(_gapEnded);
        writer.WriteBool(_scanning);
        writer.WriteBool(_endOfHead);
        writer.WriteInt32(_crc);
        writer.WriteInt32(_delay);
        writer.WriteInt32(_position);
        writer.WriteInt32(_side);
        writer.WriteInt32(_pendingSide);
        writer.WriteInt32(_switchDelay);
        writer.WriteByte(_externalOut);
        writer.WriteBool(IsDiskModified);
        writer.WriteByte((byte)Mirroring);
    }

    protected override void LoadRegisters(StateReader reader)
    {
        foreach (var track in _tracks)
        {
            reader.ReadBytes(track);
        }

        _audio.Load(reader);
        _timerReload = reader.ReadInt32();
        _timerCounter = reader.ReadInt32();
        _timerRepeat = reader.ReadBool();
        _timerEnabled = reader.ReadBool();
        _timerIrq = reader.ReadBool();
        _diskRegisters = reader.ReadBool();
        _soundRegisters = reader.ReadBool();
        _writeData = reader.ReadByte();
        _readData = reader.ReadByte();
        _motorOn = reader.ReadBool();
        _resetTransfer = reader.ReadBool();
        _readMode = reader.ReadBool();
        _crcControl = reader.ReadBool();
        _transferEnabled = reader.ReadBool();
        _diskIrqEnabled = reader.ReadBool();
        _diskIrq = reader.ReadBool();
        _transferComplete = reader.ReadBool();
        _gapEnded = reader.ReadBool();
        _scanning = reader.ReadBool();
        _endOfHead = reader.ReadBool();
        _crc = reader.ReadInt32();
        _delay = reader.ReadInt32();
        _position = reader.ReadInt32();
        _side = reader.ReadInt32();
        _pendingSide = reader.ReadInt32();
        _switchDelay = reader.ReadInt32();
        _externalOut = reader.ReadByte();
        IsDiskModified = reader.ReadBool();
        Mirroring = (Mirroring)reader.ReadByte();
    }
}
