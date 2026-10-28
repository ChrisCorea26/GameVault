using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// The PlayStation's CD-ROM controller (SPEC §7.11, Phase 15): the indexed registers at $1F801800, the parameter,
/// response and data FIFOs, the five interrupt types, the drive's commands with their first and second responses,
/// sector reading at single or double speed, XA-ADPCM and CD-DA audio into the SPU, and the shell for changing
/// discs. Deterministic: every delay is a fixed number of CPU cycles.
/// </summary>
/// <remarks>
/// Reference: psx-spx "CDROM Controller I/O Ports", "CDROM Controller Command Summary", "CDROM XA Audio ADPCM
/// Compression". Response delays follow psx-spx's measured averages (about 0xC4E1 cycles for a first response).
/// XA audio is resampled to 44.1 kHz by linear interpolation rather than the hardware's 7-tap filter.
/// </remarks>
public sealed class PsCdRom : IStateful, ICdAudioSource
{
    public const int CyclesPerSecond = 33868800;

    private const int FirstResponseDelay = 0xC4E1;
    private const int InitResponseDelay = 0x13CCE;
    private const int GetIdSecondDelay = 0x4A00;
    private const int PauseSecondDelay = 0x21181;
    private const int SingleSpeedSector = CyclesPerSecond / 75;

    // A response waiting behind an unacknowledged one arrives this long after the acknowledge, not during it: the
    // BIOS's handler acknowledges the controller before I_STAT, so an interrupt raised inside the first write would be
    // cleared by the second and never seen (DuckStation's MINIMUM_INTERRUPT_DELAY, read for facts only).
    private const int InterruptDelay = 1000;
    private const int AudioCapacity = 0x4000; // stereo samples buffered for the SPU (power of two)

    private static readonly StateTag Tag = StateTag.From("PCDR");
    private static readonly int[] XaPositive = [0, 60, 115, 98];
    private static readonly int[] XaNegative = [0, 0, -52, -55];

    private readonly byte[] _parameters = new byte[16];
    private readonly byte[] _response = new byte[16];
    private readonly byte[] _sector = new byte[ICdImage.SectorSize];
    private readonly byte[] _dataBuffer = new byte[ICdImage.SectorSize];
    private readonly byte[] _dataFifo = new byte[ICdImage.SectorSize];
    private readonly byte[] _lastHeader = new byte[8];
    private readonly short[] _audioLeft = new short[AudioCapacity];
    private readonly short[] _audioRight = new short[AudioCapacity];
    private readonly short[] _xaLeft = new short[4032];
    private readonly short[] _xaRight = new short[4032];
    private readonly int[] _xaHistory = new int[4]; // old/older for left and right
    private readonly PendingInterrupt[] _queue = new PendingInterrupt[4];

    private ICdImage? _disc;
    private int _index;
    private int _parameterCount;
    private int _responseCount;
    private int _responseRead;
    private int _dataSize;
    private int _dataRead;
    private bool _dataBufferReady;
    private int _dataBufferSize;
    private byte _interruptEnable;
    private byte _interruptFlags;
    private byte _mode;
    private byte _filterFile;
    private byte _filterChannel;
    private bool _motorOn = true;
    private bool _shellOpen;
    private bool _muted;
    private bool _adpcmMuted;
    private byte[] _volume = [0x80, 0x00, 0x80, 0x00]; // L→L, L→R, R→R, R→L
    private readonly byte[] _pendingVolume = [0x80, 0x00, 0x80, 0x00];

    // Drive state.
    private DriveState _state;
    private int _setloc;
    private bool _setlocPending;
    private int _position;
    private int _playTrack;
    private long _now;
    private long _commandAt = long.MaxValue;
    private byte _pendingCommand;
    private long _secondAt = long.MaxValue;
    private byte _secondCommand;
    private long _sectorAt = long.MaxValue;
    private long _deliverAt = long.MaxValue;
    private long _lastAcknowledge = long.MinValue / 2;
    private int _queueCount;
    private int _reportCounter;

    // Audio for the SPU.
    private int _audioWrite;
    private int _audioRead;
    private int _xaCount;
    private int _xaRate;
    private long _xaPhase;

    private enum DriveState
    {
        Idle,
        Seeking,
        Reading,
        Playing,
    }

    private struct PendingInterrupt
    {
        public byte Type;
        public byte Length;
        public ulong Bytes;
    }

    /// <summary>The CD-ROM's interrupt line (I_STAT bit 2), raised when an enabled interrupt is flagged.</summary>
    public Action? Interrupt { get; set; }

    /// <summary>The region the drive reports in GetID when the disc's licence text doesn't say ("SCEA", "SCEE" or "SCEI").</summary>
    public string FallbackRegion { get; set; } = "SCEA";

    public ICdImage? Disc => _disc;

    /// <summary>Each command the drive carries out and each response it gives, as text (the host's boot log; null while unused).</summary>
    public Action<string>? Trace { get; set; }

    public bool ShellOpen => _shellOpen;

    public void Reset()
    {
        _index = 0;
        _parameterCount = _responseCount = _responseRead = 0;
        _dataSize = _dataRead = 0;
        _dataBufferReady = false;
        _interruptEnable = 0;
        _interruptFlags = 0;
        _mode = 0;
        _filterFile = _filterChannel = 0;
        _motorOn = _disc is not null;
        _muted = false;
        _adpcmMuted = false;
        _volume = [0x80, 0x00, 0x80, 0x00];
        _state = DriveState.Idle;
        _setloc = _position = 150;
        _setlocPending = false;
        _commandAt = _secondAt = _sectorAt = _deliverAt = long.MaxValue;
        _lastAcknowledge = long.MinValue / 2;
        _queueCount = 0;
        _audioWrite = _audioRead = 0;
        _xaCount = 0;
        Array.Clear(_xaHistory);
        Array.Clear(_lastHeader);
    }

    /// <summary>Puts a disc in the drive (or none), as if the shell was opened and closed.</summary>
    public void InsertDisc(ICdImage? disc)
    {
        _disc = disc;
        _state = DriveState.Idle;
        _sectorAt = long.MaxValue;
        _motorOn = disc is not null;
        _shellOpen = true; // reported once by the next Getstat, as after a real disc change
    }

    /// <summary>Opens the shell (the disc stops) until <see cref="InsertDisc"/> closes it.</summary>
    public void OpenShell()
    {
        _disc = null;
        _shellOpen = true;
        _motorOn = false;
        _state = DriveState.Idle;
        _sectorAt = long.MaxValue;
    }

    // ---- Registers ----

    public byte Read(int register)
    {
        switch (register)
        {
            case 0:
            {
                var status = _index;
                status |= _parameterCount == 0 ? 0x08 : 0;
                status |= _parameterCount < 16 ? 0x10 : 0;
                status |= _responseRead < _responseCount ? 0x20 : 0;
                status |= _dataRead < _dataSize ? 0x40 : 0;
                status |= _commandAt != long.MaxValue ? 0x80 : 0;
                return (byte)status;
            }

            case 1:
                if (_responseRead < _responseCount)
                {
                    return _response[_responseRead++];
                }

                return 0;
            case 2:
                return ReadDataByte();
            default:
                return (_index & 1) == 0 ? (byte)(0xE0 | _interruptEnable) : (byte)(0xE0 | _interruptFlags);
        }
    }

    public void Write(int register, byte value)
    {
        if (register == 0)
        {
            _index = value & 3;
            return;
        }

        switch ((register, _index))
        {
            case (1, 0):
                StartCommand(value);
                break;
            case (1, 3):
                _pendingVolume[2] = value; // R→R
                break;
            case (2, 0):
                if (_parameterCount < 16)
                {
                    _parameters[_parameterCount++] = value;
                }

                break;
            case (2, 1):
                _interruptEnable = (byte)(value & 0x1F);
                UpdateInterrupt();
                break;
            case (2, 2):
                _pendingVolume[0] = value; // L→L
                break;
            case (2, 3):
                _pendingVolume[3] = value; // R→L
                break;
            case (3, 0):
                // Request register: bit 7 loads the sector buffer into the data FIFO.
                if ((value & 0x80) != 0)
                {
                    LoadDataFifo();
                }
                else
                {
                    _dataSize = _dataRead = 0;
                }

                break;
            case (3, 1):
                // Acknowledge: writing 1s clears flags; bit 6 clears the parameter FIFO.
                var wasFlagged = _interruptFlags != 0;
                _interruptFlags &= (byte)~(value & 0x1F);
                if ((value & 0x40) != 0)
                {
                    _parameterCount = 0;
                }

                if (_interruptFlags == 0 && wasFlagged)
                {
                    _lastAcknowledge = _now;
                    ScheduleDelivery();
                }

                UpdateInterrupt();
                break;
            case (3, 2):
                _pendingVolume[1] = value; // L→R
                break;
            case (3, 3):
                _adpcmMuted = (value & 1) != 0;
                if ((value & 0x20) != 0)
                {
                    _volume = [.. _pendingVolume];
                }

                break;

            // Index 1 and 2 of register 1 (the sound map) aren't used by games.
        }
    }

    private byte ReadDataByte()
    {
        if (_dataRead >= _dataSize)
        {
            // An empty FIFO repeats its last byte (psx-spx).
            return _dataSize > 0 ? _dataFifo[_dataSize - 1] : (byte)0;
        }

        return _dataFifo[_dataRead++];
    }

    /// <summary>DMA channel 3: four data bytes, little-endian.</summary>
    public uint DmaRead() =>
        ReadDataByte() | (uint)ReadDataByte() << 8 | (uint)ReadDataByte() << 16 | (uint)ReadDataByte() << 24;

    private void LoadDataFifo()
    {
        if (!_dataBufferReady)
        {
            return;
        }

        Array.Copy(_dataBuffer, _dataFifo, _dataBufferSize);
        _dataSize = _dataBufferSize;
        _dataRead = 0;
        _dataBufferReady = false;
    }

    // ---- Time ----

    /// <summary>Advances the drive to <paramref name="now"/> (CPU cycles), firing whatever falls due.</summary>
    public void Advance(long now)
    {
        while (true)
        {
            var next = NextEvent;
            if (next > now)
            {
                _now = now;
                return;
            }

            // Each event runs at its own time, so what it schedules is timed from there.
            _now = next;

            if (next == _commandAt)
            {
                _commandAt = long.MaxValue;
                ExecuteCommand(_pendingCommand);
            }
            else if (next == _secondAt)
            {
                _secondAt = long.MaxValue;
                SecondResponse(_secondCommand);
            }
            else if (next == _deliverAt)
            {
                _deliverAt = long.MaxValue;
                if (_interruptFlags == 0)
                {
                    DeliverQueued();
                }
            }
            else
            {
                _sectorAt = long.MaxValue;
                SectorReady(next);
            }
        }
    }

    /// <summary>Cycles from <paramref name="now"/> until the drive next needs attention.</summary>
    public long NextEvent => Math.Min(Math.Min(_commandAt, _deliverAt), Math.Min(_secondAt, _sectorAt));

    private int SectorCycles => (_mode & 0x80) != 0 ? SingleSpeedSector / 2 : SingleSpeedSector;

    // ---- Commands ----

    private void StartCommand(byte command)
    {
        if (_commandAt != long.MaxValue)
        {
            return; // busy
        }

        _pendingCommand = command;
        var delay = command switch
        {
            0x0A => InitResponseDelay,
            0x01 when _motorOn => 0x4000,
            _ => FirstResponseDelay,
        };
        _commandAt = _now + delay;
    }

    private byte Stat
    {
        get
        {
            var stat = 0;
            stat |= _motorOn ? 0x02 : 0;
            stat |= _shellOpen ? 0x10 : 0;
            stat |= _state switch
            {
                DriveState.Reading => 0x20,
                DriveState.Seeking => 0x40,
                DriveState.Playing => 0x80,
                _ => 0,
            };
            return (byte)stat;
        }
    }

    private void ExecuteCommand(byte command)
    {
        var p = _parameters.AsSpan(0, _parameterCount);
        _parameterCount = 0;
        Trace?.Invoke($"{_now,12} command {command:X2} {Convert.ToHexString(p)}");

        if (_disc is null && command is 0x06 or 0x1B or 0x03 or 0x15 or 0x16 or 0x1E or 0x1A)
        {
            Respond(5, (byte)(Stat | 0x01), 0x80); // no disc
            return;
        }

        switch (command)
        {
            case 0x01: // Getstat
                Respond(3, Stat);
                if (_disc is not null)
                {
                    _shellOpen = false; // the shell-open bit stays until read with the shell closed
                }

                break;
            case 0x02: // Setloc
                if (p.Length >= 3)
                {
                    _setloc = Bcd(p[0]) * 4500 + Bcd(p[1]) * 75 + Bcd(p[2]);
                    _setlocPending = true;
                }

                Respond(3, Stat);
                break;
            case 0x03: // Play
                if (p.Length >= 1 && p[0] != 0 && _disc is not null)
                {
                    var track = Bcd(p[0]);
                    if (track >= 1 && track <= _disc.Tracks.Count)
                    {
                        _setloc = _disc.Tracks[track - 1].Start;
                        _setlocPending = true;
                    }
                }

                Respond(3, Stat);
                StartPlaying();
                break;
            case 0x04: // Forward
            case 0x05: // Backward
                Respond(3, Stat);
                break;
            case 0x06: // ReadN
            case 0x1B: // ReadS
                Respond(3, Stat);
                StartReading();
                break;
            case 0x07: // MotorOn
                _motorOn = true;
                Respond(3, Stat);
                ScheduleSecond(command, FirstResponseDelay);
                break;
            case 0x08: // Stop
                StopDrive();
                Respond(3, Stat);
                _motorOn = false;
                ScheduleSecond(command, FirstResponseDelay * 2);
                break;
            case 0x09: // Pause
                Respond(3, Stat);
                var wasActive = _state != DriveState.Idle;
                StopDrive();
                ScheduleSecond(command, wasActive ? PauseSecondDelay : 0x1DF2);
                break;
            case 0x0A: // Init
                _mode = 0x20;
                StopDrive();
                _motorOn = _disc is not null;
                Respond(3, Stat);
                ScheduleSecond(command, 0x4000);
                break;
            case 0x0B: // Mute
                _muted = true;
                Respond(3, Stat);
                break;
            case 0x0C: // Demute
                _muted = false;
                Respond(3, Stat);
                break;
            case 0x0D: // Setfilter
                if (p.Length >= 2)
                {
                    _filterFile = p[0];
                    _filterChannel = p[1];
                }

                Respond(3, Stat);
                break;
            case 0x0E: // Setmode
                if (p.Length >= 1)
                {
                    _mode = p[0];
                }

                Respond(3, Stat);
                break;
            case 0x0F: // Getparam
                Respond(3, Stat, _mode, 0, _filterFile, _filterChannel);
                break;
            case 0x10: // GetlocL: the last data sector's header and subheader
                Respond(3, _lastHeader[0], _lastHeader[1], _lastHeader[2], _lastHeader[3], _lastHeader[4], _lastHeader[5], _lastHeader[6], _lastHeader[7]);
                break;
            case 0x11: // GetlocP
                GetlocP();
                break;
            case 0x12: // SetSession
                Respond(3, Stat);
                ScheduleSecond(command, FirstResponseDelay);
                break;
            case 0x13: // GetTN
                Respond(3, Stat, 1, ToBcd(_disc?.Tracks.Count ?? 1));
                break;
            case 0x14: // GetTD
                GetTd(p.Length >= 1 ? Bcd(p[0]) : 0);
                break;
            case 0x15: // SeekL
            case 0x16: // SeekP
                Respond(3, Stat);
                StopDrive();
                _state = DriveState.Seeking;
                ScheduleSecond(command, SeekCycles(_setloc));
                break;
            case 0x19: // Test
                Test(p.Length >= 1 ? p[0] : (byte)0);
                break;
            case 0x1A: // GetID
                Respond(3, Stat);
                ScheduleSecond(command, GetIdSecondDelay);
                break;
            case 0x1C: // Reset
                StopDrive();
                Respond(3, Stat);
                break;
            case 0x1E: // ReadTOC
                Respond(3, Stat);
                ScheduleSecond(command, CyclesPerSecond / 2);
                break;
            default:
                Respond(5, (byte)(Stat | 0x01), 0x40); // invalid command
                break;
        }
    }

    private void SecondResponse(byte command)
    {
        switch (command)
        {
            case 0x15:
            case 0x16:
                _position = _setloc;
                _setlocPending = false;
                _state = DriveState.Idle;
                Respond(2, Stat);
                break;
            case 0x1A:
                GetId();
                break;
            default:
                Respond(2, Stat);
                break;
        }
    }

    private void ScheduleSecond(byte command, int delay)
    {
        _secondCommand = command;
        _secondAt = _now + delay;
    }

    private void StopDrive()
    {
        _state = DriveState.Idle;
        _sectorAt = long.MaxValue;
        _secondAt = long.MaxValue;
    }

    /// <summary>A seek takes longer the further the head moves: a base time plus distance, at most about a second.</summary>
    private int SeekCycles(int target)
    {
        var distance = Math.Abs(target - _position);
        return (int)Math.Min(CyclesPerSecond, 20000 + (long)distance * 50);
    }

    private void StartReading()
    {
        var seek = _setlocPending ? SeekCycles(_setloc) : 0;
        if (_setlocPending)
        {
            _position = _setloc;
            _setlocPending = false;
        }

        _state = DriveState.Reading;
        _sectorAt = _now + seek + SectorCycles;
    }

    private void StartPlaying()
    {
        var seek = _setlocPending ? SeekCycles(_setloc) : 0;
        if (_setlocPending)
        {
            _position = _setloc;
            _setlocPending = false;
        }

        _playTrack = TrackAt(_position);
        _reportCounter = 0;
        _state = DriveState.Playing;
        _sectorAt = _now + seek + SectorCycles;
    }

    private void GetId()
    {
        if (_disc is null)
        {
            Respond(5, 0x08, 0x40, 0, 0, 0, 0, 0, 0);
            return;
        }

        if (_disc.Tracks[0].Type == CdTrackType.Audio)
        {
            Respond(5, (byte)(Stat | 0x08), 0x90, 0, 0, 0, 0, 0, 0); // audio disc
            return;
        }

        var region = DiscRegion() ?? FallbackRegion;
        Respond(2, Stat, 0x00, 0x20, 0x00, (byte)region[0], (byte)region[1], (byte)region[2], (byte)region[3]);
    }

    /// <summary>The region from the licence text in sector 4 ("Licensed by Sony Computer Entertainment Amer…").</summary>
    public string? DiscRegion()
    {
        if (_disc is null)
        {
            return null;
        }

        Span<byte> sector = stackalloc byte[ICdImage.SectorSize];
        _disc.ReadSector(_disc.Tracks[0].Start + 4, sector);
        var text = System.Text.Encoding.ASCII.GetString(sector.Slice(24, 80));
        return text.Contains("Amer", StringComparison.Ordinal) ? "SCEA"
            : text.Contains("Europe", StringComparison.Ordinal) ? "SCEE"
            : text.Contains("Inc.", StringComparison.Ordinal) ? "SCEI"
            : null;
    }

    private void Test(byte subcommand)
    {
        switch (subcommand)
        {
            case 0x20: // controller date and version: 1994-09-19, version C0 (the common PU-8 board)
                Respond(3, 0x94, 0x09, 0x19, 0xC0);
                break;
            case 0x04:
            case 0x05:
                Respond(3, Stat, 0, 0);
                break;
            default:
                Respond(5, (byte)(Stat | 0x01), 0x10);
                break;
        }
    }

    private void GetTd(int track)
    {
        if (_disc is null)
        {
            return;
        }

        int address;
        if (track == 0)
        {
            address = _disc.LeadOut;
        }
        else if (track <= _disc.Tracks.Count)
        {
            address = _disc.Tracks[track - 1].Start;
        }
        else
        {
            Respond(5, (byte)(Stat | 0x01), 0x10);
            return;
        }

        Respond(3, Stat, ToBcd(address / 4500), ToBcd(address / 75 % 60));
    }

    private void GetlocP()
    {
        var track = TrackAt(_position);
        var relative = _disc is null ? 0 : _position - _disc.Tracks[Math.Max(0, track - 1)].Start;
        var index = relative < 0 ? 0 : 1;
        relative = Math.Abs(relative);
        Respond(
            3,
            ToBcd(track),
            (byte)index,
            ToBcd(relative / 4500),
            ToBcd(relative / 75 % 60),
            ToBcd(relative % 75),
            ToBcd(_position / 4500),
            ToBcd(_position / 75 % 60),
            ToBcd(_position % 75));
    }

    private int TrackAt(int address)
    {
        if (_disc is null)
        {
            return 1;
        }

        for (var i = _disc.Tracks.Count - 1; i >= 0; i--)
        {
            if (address >= _disc.Tracks[i].Start - _disc.Tracks[i].Pregap)
            {
                return i + 1;
            }
        }

        return 1;
    }

    // ---- Sectors ----

    private void SectorReady(long at)
    {
        if (_disc is null || (_state != DriveState.Reading && _state != DriveState.Playing))
        {
            return;
        }

        _disc.ReadSector(_position, _sector);
        var address = _position++;
        _sectorAt = at + SectorCycles;

        if (_state == DriveState.Playing)
        {
            PlayAudioSector(address);
            return;
        }

        // Data sectors: mode 2 has the subheader at 16 and data at 24; mode 1 has data at 16.
        var mode2 = _sector[15] == 2;
        Array.Copy(_sector, 12, _lastHeader, 0, mode2 ? 8 : 4);
        var submode = mode2 ? _sector[18] : (byte)0;

        if ((_mode & 0x40) != 0 && mode2 && (submode & 0x04) != 0)
        {
            // XA-ADPCM: real-time audio sectors go to the decoder, never to the CPU.
            if ((_mode & 0x08) == 0 || (_sector[16] == _filterFile && _sector[17] == _filterChannel))
            {
                DecodeXaSector();
            }

            return;
        }

        if ((_mode & 0x08) != 0 && mode2 && (submode & 0x04) != 0)
        {
            return; // filtered audio is skipped
        }

        if ((_mode & 0x20) != 0)
        {
            _dataBufferSize = 0x924;
            Array.Copy(_sector, 12, _dataBuffer, 0, 0x924);
        }
        else
        {
            _dataBufferSize = 0x800;
            Array.Copy(_sector, mode2 ? 24 : 16, _dataBuffer, 0, 0x800);
        }

        _dataBufferReady = true;
        Respond(1, Stat);
    }

    private void PlayAudioSector(int address)
    {
        if (_disc is not null && TrackAt(address) != _playTrack && (_mode & 0x02) != 0)
        {
            // Auto-pause at the end of the track: INT4 (data end).
            _state = DriveState.Idle;
            _sectorAt = long.MaxValue;
            Respond(4, Stat);
            return;
        }

        if (address >= (_disc?.LeadOut ?? 0))
        {
            _state = DriveState.Idle;
            _sectorAt = long.MaxValue;
            Respond(4, Stat);
            return;
        }

        if (!_muted)
        {
            for (var i = 0; i < 588; i++)
            {
                var left = (short)(_sector[i * 4] | _sector[i * 4 + 1] << 8);
                var right = (short)(_sector[i * 4 + 2] | _sector[i * 4 + 3] << 8);
                PushAudio(left, right);
            }
        }

        // Report mode: an INT1 with the position every ten sectors.
        if ((_mode & 0x04) != 0 && ++_reportCounter % 10 == 0)
        {
            var relative = address - (_disc?.Tracks[_playTrack - 1].Start ?? 0);
            Respond(1, Stat, ToBcd(_playTrack), 1, ToBcd(relative / 4500), ToBcd(relative / 75 % 60), ToBcd(relative % 75), 0, 0);
        }
    }

    // ---- XA-ADPCM ----

    private void DecodeXaSector()
    {
        var coding = _sector[19];
        var stereo = (coding & 0x01) != 0;
        var eightBit = (coding & 0x10) != 0;
        var sampleRate = (coding & 0x04) != 0 ? 18900 : 37800;
        var units = eightBit ? 4 : 8;
        var count = 0;
        var leftCount = 0;
        var rightCount = 0;

        for (var group = 0; group < 18; group++)
        {
            var g = 24 + group * 128;
            for (var unit = 0; unit < units; unit++)
            {
                var header = _sector[g + 4 + unit];
                var shift = header & 0x0F;
                if (shift > 12)
                {
                    shift = 9;
                }

                var filter = (header >> 4) & 3;
                var channel = stereo ? unit & 1 : 0;
                for (var n = 0; n < 28; n++)
                {
                    int raw;
                    if (eightBit)
                    {
                        raw = (short)(_sector[g + 16 + n * 4 + unit] << 8);
                    }
                    else
                    {
                        var data = _sector[g + 16 + n * 4 + (unit >> 1)];
                        var nibble = (unit & 1) != 0 ? data >> 4 : data & 0x0F;
                        raw = (short)(nibble << 12);
                    }

                    var old = _xaHistory[channel * 2];
                    var older = _xaHistory[channel * 2 + 1];
                    var sample = (raw >> shift) + ((old * XaPositive[filter] + older * XaNegative[filter] + 32) >> 6);
                    sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                    _xaHistory[channel * 2 + 1] = old;
                    _xaHistory[channel * 2] = sample;

                    if (!stereo)
                    {
                        _xaLeft[count] = _xaRight[count] = (short)sample;
                        count++;
                    }
                    else if (channel == 0)
                    {
                        _xaLeft[leftCount++] = (short)sample;
                    }
                    else
                    {
                        _xaRight[rightCount++] = (short)sample;
                    }
                }
            }
        }

        _xaCount = stereo ? Math.Min(leftCount, rightCount) : count;
        _xaRate = sampleRate;
        if (_muted || _adpcmMuted)
        {
            return;
        }

        // Resample to 44.1 kHz by linear interpolation, carrying the phase between sectors.
        var step = ((long)_xaRate << 16) / 44100;
        while (_xaPhase >> 16 < _xaCount - 1)
        {
            var i = (int)(_xaPhase >> 16);
            var fraction = (int)(_xaPhase & 0xFFFF);
            var left = _xaLeft[i] + ((_xaLeft[i + 1] - _xaLeft[i]) * fraction >> 16);
            var right = _xaRight[i] + ((_xaRight[i + 1] - _xaRight[i]) * fraction >> 16);
            PushAudio((short)left, (short)right);
            _xaPhase += step;
        }

        _xaPhase -= (long)(_xaCount - 1) << 16;
    }

    // ---- Audio to the SPU ----

    private void PushAudio(short left, short right)
    {
        if (_audioWrite - _audioRead >= AudioCapacity)
        {
            return; // the SPU isn't keeping up: drop
        }

        _audioLeft[_audioWrite & (AudioCapacity - 1)] = left;
        _audioRight[_audioWrite & (AudioCapacity - 1)] = right;
        _audioWrite++;
    }

    /// <summary>One 44.1 kHz sample for the SPU, through the CD volume matrix (0x80 = 100%).</summary>
    public void ReadSample(out short left, out short right)
    {
        if (_audioRead == _audioWrite)
        {
            left = right = 0;
            return;
        }

        int l = _audioLeft[_audioRead & (AudioCapacity - 1)];
        int r = _audioRight[_audioRead & (AudioCapacity - 1)];
        _audioRead++;
        left = (short)Math.Clamp((l * _volume[0] + r * _volume[3]) >> 7, short.MinValue, short.MaxValue);
        right = (short)Math.Clamp((r * _volume[2] + l * _volume[1]) >> 7, short.MinValue, short.MaxValue);
    }

    // ---- Interrupts ----

    private void Respond(int type, params ReadOnlySpan<byte> bytes)
    {
        var pending = new PendingInterrupt { Type = (byte)type, Length = (byte)Math.Min(bytes.Length, 8) };
        for (var i = 0; i < pending.Length; i++)
        {
            pending.Bytes |= (ulong)bytes[i] << (8 * i);
        }

        if (type == 1 && (_interruptFlags == 1 || QueueHolds(1)))
        {
            return; // a sector's INT1 while the last one waits: the buffer simply holds the newer sector
        }

        if (_interruptFlags == 0 && _queueCount == 0 && _now - _lastAcknowledge >= InterruptDelay)
        {
            Deliver(pending);
        }
        else if (_queueCount < _queue.Length)
        {
            _queue[_queueCount++] = pending;
            ScheduleDelivery();
        }
    }

    private bool QueueHolds(byte type)
    {
        for (var i = 0; i < _queueCount; i++)
        {
            if (_queue[i].Type == type)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>With the flags clear and a response waiting, delivers it once the delay after the last acknowledge has passed.</summary>
    private void ScheduleDelivery()
    {
        if (_interruptFlags == 0 && _queueCount > 0 && _deliverAt == long.MaxValue)
        {
            _deliverAt = Math.Max(_now, _lastAcknowledge + InterruptDelay);
        }
    }

    private void DeliverQueued()
    {
        if (_queueCount == 0)
        {
            return;
        }

        var next = _queue[0];
        Array.Copy(_queue, 1, _queue, 0, --_queueCount);
        Deliver(next);
    }

    private void Deliver(PendingInterrupt pending)
    {
        _responseCount = pending.Length;
        _responseRead = 0;
        for (var i = 0; i < pending.Length; i++)
        {
            _response[i] = (byte)(pending.Bytes >> (8 * i));
        }

        _interruptFlags = pending.Type;
        Trace?.Invoke($"{_now,12} INT{pending.Type} {Convert.ToHexString(_response.AsSpan(0, _responseCount))}");
        UpdateInterrupt();
    }

    private void UpdateInterrupt()
    {
        if ((_interruptFlags & _interruptEnable & 0x1F) != 0)
        {
            Interrupt?.Invoke();
        }
    }

    private static int Bcd(byte value) => (value >> 4) * 10 + (value & 0x0F);

    private static byte ToBcd(int value) => (byte)((value / 10 % 10) << 4 | value % 10);

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 2);
        writer.WriteBytes(_parameters);
        writer.WriteBytes(_response);
        writer.WriteBytes(_sector);
        writer.WriteBytes(_dataBuffer);
        writer.WriteBytes(_dataFifo);
        writer.WriteBytes(_lastHeader);
        writer.WriteInt32(_index);
        writer.WriteInt32(_parameterCount);
        writer.WriteInt32(_responseCount);
        writer.WriteInt32(_responseRead);
        writer.WriteInt32(_dataSize);
        writer.WriteInt32(_dataRead);
        writer.WriteBool(_dataBufferReady);
        writer.WriteInt32(_dataBufferSize);
        writer.WriteByte(_interruptEnable);
        writer.WriteByte(_interruptFlags);
        writer.WriteByte(_mode);
        writer.WriteByte(_filterFile);
        writer.WriteByte(_filterChannel);
        writer.WriteBool(_motorOn);
        writer.WriteBool(_shellOpen);
        writer.WriteBool(_muted);
        writer.WriteBool(_adpcmMuted);
        writer.WriteBytes(_volume);
        writer.WriteBytes(_pendingVolume);
        writer.WriteInt32((int)_state);
        writer.WriteInt32(_setloc);
        writer.WriteBool(_setlocPending);
        writer.WriteInt32(_position);
        writer.WriteInt32(_playTrack);
        writer.WriteInt64(_now);
        writer.WriteInt64(_commandAt);
        writer.WriteByte(_pendingCommand);
        writer.WriteInt64(_secondAt);
        writer.WriteByte(_secondCommand);
        writer.WriteInt64(_sectorAt);
        writer.WriteInt32(_queueCount);
        foreach (var pending in _queue)
        {
            writer.WriteByte(pending.Type);
            writer.WriteByte(pending.Length);
            writer.WriteUInt64(pending.Bytes);
        }

        writer.WriteInt32(_reportCounter);
        writer.WriteInt32(_audioWrite - _audioRead);
        for (var i = _audioRead; i != _audioWrite; i++)
        {
            writer.WriteUInt16((ushort)_audioLeft[i & (AudioCapacity - 1)]);
            writer.WriteUInt16((ushort)_audioRight[i & (AudioCapacity - 1)]);
        }

        foreach (var h in _xaHistory)
        {
            writer.WriteInt32(h);
        }

        writer.WriteInt64(_xaPhase);
        writer.WriteInt64(_deliverAt);
        writer.WriteInt64(_lastAcknowledge);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        var version = reader.BeginChunk(Tag);
        reader.ReadBytes(_parameters);
        reader.ReadBytes(_response);
        reader.ReadBytes(_sector);
        reader.ReadBytes(_dataBuffer);
        reader.ReadBytes(_dataFifo);
        reader.ReadBytes(_lastHeader);
        _index = reader.ReadInt32() & 3;
        _parameterCount = Math.Clamp(reader.ReadInt32(), 0, 16);
        _responseCount = Math.Clamp(reader.ReadInt32(), 0, 16);
        _responseRead = reader.ReadInt32();
        _dataSize = Math.Clamp(reader.ReadInt32(), 0, _dataFifo.Length);
        _dataRead = reader.ReadInt32();
        _dataBufferReady = reader.ReadBool();
        _dataBufferSize = Math.Clamp(reader.ReadInt32(), 0, _dataBuffer.Length);
        _interruptEnable = reader.ReadByte();
        _interruptFlags = reader.ReadByte();
        _mode = reader.ReadByte();
        _filterFile = reader.ReadByte();
        _filterChannel = reader.ReadByte();
        _motorOn = reader.ReadBool();
        _shellOpen = reader.ReadBool();
        _muted = reader.ReadBool();
        _adpcmMuted = reader.ReadBool();
        _volume = new byte[4];
        reader.ReadBytes(_volume);
        reader.ReadBytes(_pendingVolume);
        _state = (DriveState)reader.ReadInt32();
        _setloc = reader.ReadInt32();
        _setlocPending = reader.ReadBool();
        _position = reader.ReadInt32();
        _playTrack = reader.ReadInt32();
        _now = reader.ReadInt64();
        _commandAt = reader.ReadInt64();
        _pendingCommand = reader.ReadByte();
        _secondAt = reader.ReadInt64();
        _secondCommand = reader.ReadByte();
        _sectorAt = reader.ReadInt64();
        _queueCount = Math.Clamp(reader.ReadInt32(), 0, _queue.Length);
        for (var i = 0; i < _queue.Length; i++)
        {
            _queue[i].Type = reader.ReadByte();
            _queue[i].Length = reader.ReadByte();
            _queue[i].Bytes = reader.ReadUInt64();
        }

        _reportCounter = reader.ReadInt32();
        var audio = Math.Clamp(reader.ReadInt32(), 0, AudioCapacity);
        _audioRead = 0;
        _audioWrite = audio;
        for (var i = 0; i < audio; i++)
        {
            _audioLeft[i] = (short)reader.ReadUInt16();
            _audioRight[i] = (short)reader.ReadUInt16();
        }

        for (var i = 0; i < _xaHistory.Length; i++)
        {
            _xaHistory[i] = reader.ReadInt32();
        }

        _xaPhase = reader.ReadInt64();
        _deliverAt = long.MaxValue;
        _lastAcknowledge = long.MinValue / 2;
        if (version >= 2)
        {
            _deliverAt = reader.ReadInt64();
            _lastAcknowledge = reader.ReadInt64();
        }
        else if (_interruptFlags == 0 && _queueCount > 0)
        {
            _deliverAt = _now; // a version-1 state could hold a response that was due at once
        }

        reader.EndChunk();
    }
}
