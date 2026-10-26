using System.Buffers.Binary;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The PlayStation 2's CD/DVD drive and its "mechacon" (ps2tek: "CDVD Drive"), at $1F402000. N commands (reads, seeks,
/// the TOC) run in the background and end with the CDVD interrupt (IOP IRQ 2); S commands (the clock, the NVM, the
/// console's identity, the tray) answer at once through the S result register. Read sectors go to IOP DMA channel 3
/// one at a time, in 2,048-byte blocks, CD raw blocks of 2,328 or 2,340 bytes, or DVD blocks of 2,064 bytes.
/// </summary>
/// <remarks>
/// Timing is deterministic: a sector takes 36,864,000 × 2,048 / (speed × 153,600) IOP cycles on a CD and
/// … / (speed × 1,382,400) on a DVD (ps2tek: "CDVD Reads and Seeks"); a seek takes a third of a second to spin up a
/// stopped disc, nothing extra for a jump of under 8 (CD) or 16 (DVD) sectors, 30 ms within 4,371 (CD) or 14,764 (DVD)
/// sectors and 100 ms beyond. The drive holds one sector until the DMA takes it. The real-time clock starts at
/// <see cref="RtcStart"/> and advances with emulated time, so runs are reproducible. The NVM (the 1 KB EEPROM holding
/// the BIOS's settings) starts blank. Command details and the constants the BIOS checks (the mechacon version, the
/// MagicGate replies) follow PCSX2's CDVD.cpp, read for facts only; the interrupt reason for a finished N command is
/// bit 0 of I_STAT, as there.
/// </remarks>
public sealed class Ps2Cdvd : IStateful
{
    public const uint Base = 0x1F402000;
    public const int NvmSize = 1024;
    public const long Clock = 36_864_000;

    // Drive status (register 0Ah).
    private const byte StatusStop = 0x00;
    private const byte StatusTrayOpen = 0x01;
    private const byte StatusSpin = 0x02;
    private const byte StatusRead = 0x06;
    private const byte StatusPause = 0x0A;
    private const byte StatusSeek = 0x12;

    // N command status (register 05h).
    private const byte ReadyError = 0x01;
    private const byte ReadyMechaInit = 0x08;
    private const byte ReadyReady = 0x40;
    private const byte ReadyBusy = 0x80;

    // I_STAT reasons.
    private const byte IrqCommandComplete = 0x01;
    private const byte IrqEject = 0x08;

    private const int MaxBlock = 2448;

    private static readonly StateTag Tag = StateTag.From("CDVD");

    private readonly byte[] _nParams = new byte[16];
    private readonly byte[] _sParams = new byte[16];
    private readonly byte[] _sResult = new byte[16];
    private readonly byte[] _buffer = new byte[MaxBlock];
    private readonly byte[] _raw = new byte[ICdImage.SectorSize];
    private readonly byte[] _mgBuffer = new byte[2048];

    private IPs2Disc? _disc;
    private bool _trayOpen;

    private byte _nCommand;
    private int _nParamCount;
    private byte _ready = ReadyReady | ReadyMechaInit;
    private byte _error;
    private byte _howTo;
    private byte _iStat;
    private byte _status = StatusStop;
    private byte _sticky;
    private byte _sCommand;
    private int _sParamCount;
    private int _sResultCount;
    private int _sResultPos;

    // The drive's mechanics.
    private bool _spinning;
    private int _spindle;
    private long _currentSector;

    // The N command in progress: a seek or read ends at _eventAt; a read then delivers a sector every _blockTime.
    private enum DriveAction : byte
    {
        None,
        Seek,
        Standby,
        Stop,
        Read,
    }

    private DriveAction _action;
    private long _eventAt = long.MaxValue;
    private long _readLba;
    private long _readRemaining;
    private int _blockSize;
    private int _readKind; // 0 a CD data read, 1 a CD audio read, 2 a DVD read
    private long _blockTime;
    private bool _bufferFull;

    // S command state: the configuration area that is open, MagicGate's buffer, the clock.
    private int _configMode;
    private int _configArea;
    private int _configBlocks;
    private int _configIndex;
    private int _mgSize;
    private DateTime _rtcStart = new(2000, 1, 1);
    private long _rtcBaseTicks = new DateTime(2000, 1, 1).Ticks;
    private long _rtcBaseCycle;

    /// <summary>The disc in the drive (null for none). Changing it is like opening and closing the tray.</summary>
    public IPs2Disc? Disc
    {
        get => _disc;
        set
        {
            if (_disc is not null && !ReferenceEquals(_disc, value))
            {
                RaiseInterrupt(IrqEject);
            }

            _disc = value;
            _spinning = false;
            CancelCommand();
            _status = _trayOpen ? StatusTrayOpen : StatusStop;
            _sticky |= _status;
        }
    }

    /// <summary>Whether the tray is open (S command 06h moves it too).</summary>
    public bool TrayOpen
    {
        get => _trayOpen;
        set
        {
            if (value && !_trayOpen)
            {
                RaiseInterrupt(IrqEject);
                CancelCommand();
            }

            _trayOpen = value;
            _spinning = false;
            _status = value ? StatusTrayOpen : StatusStop;
            _sticky |= _status;
        }
    }

    /// <summary>
    /// The time the real-time clock reads at cycle 0 (and after a reset); setting it restarts the clock from it at
    /// cycle 0. A fixed date keeps runs deterministic.
    /// </summary>
    public DateTime RtcStart
    {
        get => _rtcStart;
        set
        {
            _rtcStart = value;
            _rtcBaseTicks = value.Ticks;
            _rtcBaseCycle = 0;
        }
    }

    /// <summary>The mechacon version S command 03h:00h reports (PCSX2 uses this one; newer CDVDMANs check it).</summary>
    public uint MechaconVersion { get; set; } = 0x00020603;

    /// <summary>The NVM (EEPROM): the BIOS's configuration, the console and i.LINK ids, the model number. Blank by default.</summary>
    public byte[] Nvm { get; } = new byte[NvmSize];

    /// <summary>
    /// The NVM's layout: BIOSes from v1.70 on keep the configuration blocks elsewhere (PCSX2's NVMLayout tables).
    /// </summary>
    public bool NewNvmLayout { get; set; }

    /// <summary>Raised when I_STAT gains a reason (IOP IRQ 2).</summary>
    public System.Action? Interrupt { get; set; }

    /// <summary>
    /// The TOC, which the drive writes straight to IOP DMA channel 3's address (as PCSX2 does) rather than as a
    /// sector.
    /// </summary>
    public Action<ReadOnlySpan<byte>>? DirectTransfer { get; set; }

    /// <summary>The cycle of the drive's next event, for the scheduler.</summary>
    public long NextEvent => _bufferFull ? long.MaxValue : _eventAt;

    /// <summary>A read sector is waiting for DMA channel 3.</summary>
    public bool SectorReady => _bufferFull;

    /// <summary>The waiting sector's bytes.</summary>
    public ReadOnlySpan<byte> Sector => _buffer.AsSpan(0, _blockSize);

    /// <summary>The CDVD's interrupt reasons (register 08h).</summary>
    public byte InterruptStatus => _iStat;

    /// <summary>The drive status (register 0Ah).</summary>
    public byte DriveStatus => _status;

    /// <summary>The disc type register (0Fh): 00h no disc, 12h a PS2 CD, 14h a PS2 DVD (single or dual layer).</summary>
    public byte DiscType => _disc is null || _trayOpen ? (byte)0x00 : _disc.Type == Ps2DiscType.Cd ? (byte)0x12 : (byte)0x14;

    private bool IsDvd => _disc is not null && _disc.Type != Ps2DiscType.Cd;

    public void Reset()
    {
        Array.Clear(_nParams);
        Array.Clear(_sParams);
        Array.Clear(_sResult);
        _nCommand = 0;
        _nParamCount = 0;
        _ready = ReadyReady | ReadyMechaInit;
        _error = 0;
        _howTo = 0;
        _iStat = 0;
        _status = _trayOpen ? StatusTrayOpen : StatusStop;
        _sticky = _status;
        _sCommand = 0;
        _sParamCount = 0;
        _sResultCount = _sResultPos = 0;
        _spinning = false;
        _spindle = 0;
        _currentSector = 0;
        CancelCommand();
        _configMode = _configArea = _configBlocks = _configIndex = 0;
        _mgSize = 0;
        _rtcBaseTicks = RtcStart.Ticks;
        _rtcBaseCycle = 0;
    }

    private void CancelCommand()
    {
        _action = DriveAction.None;
        _eventAt = long.MaxValue;
        _readRemaining = 0;
        _bufferFull = false;
    }

    private void RaiseInterrupt(byte reason)
    {
        var fresh = (_iStat & reason) != reason;
        _iStat |= reason;
        if (fresh)
        {
            Interrupt?.Invoke();
        }
    }

    // ---- Registers ----

    /// <summary>A register read; <paramref name="offset"/> is from $1F402000.</summary>
    public byte Read(uint offset, long now)
    {
        Advance(now);
        switch (offset)
        {
            case 0x04:
                return _nCommand;
            case 0x05:
                return _ready;
            case 0x06:
            {
                var error = _error;
                _error = 0;
                return error;
            }

            case 0x08:
                return _iStat;
            case 0x0A:
                return _status;
            case 0x0B:
                return _sticky;
            case 0x0C:
                return Bcd((int)(_currentSector / (60 * 75)));
            case 0x0D:
                return Bcd((int)(_currentSector / 75 % 60 + 2));
            case 0x0E:
                return Bcd((int)(_currentSector % 75));
            case 0x0F:
                return DiscType;
            case 0x13:
                return SpeedRegister();
            case 0x16:
                return _sCommand;
            case 0x17:
                return (byte)(_sResultPos < _sResultCount ? 0x00 : 0x40);
            case 0x18:
                return _sResultPos < _sResultCount ? _sResult[_sResultPos++] : (byte)0;
            default:
                return 0;
        }
    }

    public void Write(uint offset, byte value, long now)
    {
        Advance(now);
        switch (offset)
        {
            case 0x04:
                StartNCommand(value, now);
                break;
            case 0x05:
                if (_nParamCount >= _nParams.Length)
                {
                    _nParamCount = 0;
                }

                _nParams[_nParamCount++] = value;
                break;
            case 0x06:
                _howTo = value;
                break;
            case 0x07: // BREAK: abandon the N command in progress
                if ((_ready & ReadyBusy) != 0)
                {
                    CancelCommand();
                    _status = _spinning ? StatusPause : StatusStop;
                    _ready = ReadyReady | ReadyMechaInit;
                    RaiseInterrupt(IrqCommandComplete);
                }

                break;
            case 0x08:
                _iStat &= (byte)~value;
                break;
            case 0x16:
                RunSCommand(value, now);
                break;
            case 0x17:
                if (_sParamCount < _sParams.Length)
                {
                    _sParams[_sParamCount++] = value;
                }

                break;
        }
    }

    private byte SpeedRegister()
    {
        if (!_spinning || _disc is null)
        {
            return 0;
        }

        var speed = _spindle & 0x3F;
        if (speed == 0)
        {
            speed = IsDvd ? 3 : 5;
        }

        return (byte)(IsDvd ? speed + 0xF : speed - 1);
    }

    // ---- N commands ----

    private void StartNCommand(byte command, long now)
    {
        var paramCount = _nParamCount;
        _nParamCount = 0;
        if ((_ready & ReadyReady) == 0)
        {
            Fail(0x13); // not ready
            return;
        }

        _nCommand = command;
        if (command > 0x00 && (_trayOpen || _disc is null))
        {
            Fail(_disc is null ? (byte)0x12 : (byte)0x11); // no disc / tray open
            return;
        }

        switch (command)
        {
            case 0x00: // NOP
            case 0x0C: // ReadKey
            case 0x0F: // ChgSpdlCtrl
                Complete();
                break;
            case 0x01: // Reset: the drive stops
                _spinning = false;
                _status = StatusStop;
                _sResultCount = _sResultPos = 0;
                Complete();
                break;
            case 0x02: // Standby: back to sector 0, paused and spinning
                BeginSeek(0, now, DriveAction.Standby);
                break;
            case 0x03: // Stop: a sixth of a second to spin down
                _ready = ReadyBusy | ReadyMechaInit;
                _status = StatusSpin;
                _action = DriveAction.Stop;
                _eventAt = now + Clock / 6;
                break;
            case 0x04: // Pause
                CancelCommand();
                _status = StatusPause;
                Complete();
                break;
            case 0x05: // Seek
                BeginSeek(BinaryPrimitives.ReadUInt32LittleEndian(_nParams), now, DriveAction.Seek);
                break;
            case 0x06: // ReadCd
            case 0x07: // ReadCdda
            case 0x08: // ReadDvd
            case 0x0E: // ReadXCdda
                StartRead(command, paramCount, now);
                break;
            case 0x09: // GetToc
                WriteToc();
                _status = StatusPause;
                Complete();
                break;
            default:
                Fail(0x10); // unsupported command
                break;
        }
    }

    private void Fail(byte error)
    {
        _error = error;
        _ready |= ReadyError;
        RaiseInterrupt(IrqCommandComplete);
    }

    private void Complete()
    {
        _ready = ReadyReady | ReadyMechaInit;
        RaiseInterrupt(IrqCommandComplete);
    }

    private void StartRead(byte command, int paramCount, long now)
    {
        var lba = BinaryPrimitives.ReadUInt32LittleEndian(_nParams);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(_nParams.AsSpan(4));
        if (paramCount >= 10)
        {
            _spindle = _nParams[9];
        }

        var mode = _nParams[10];
        int speed;
        if (command == 0x08)
        {
            _readKind = 2;
            _blockSize = 2064;
            speed = (_spindle & 0x3F) switch { 1 => 1, 2 => 2, _ => 4 };
        }
        else
        {
            _readKind = command == 0x06 ? 0 : 1;
            _blockSize = _readKind == 0
                ? mode switch { 1 => 2328, 2 => 2340, _ => 2048 }
                : mode switch { 1 => 2368, 2 => 2448, _ => 2352 };
            speed = (_spindle & 0x3F) switch { 1 => 1, 2 => 2, 3 => 4, 4 => 12, _ => 24 };
        }

        var rate = (IsDvd ? 1_382_400L : 153_600L) * speed;
        _blockTime = Math.Max(1, Clock * IPs2Disc.SectorSize / rate);
        _readLba = lba;
        _readRemaining = count;
        _bufferFull = false;
        if (count == 0)
        {
            _currentSector = lba;
            _status = StatusPause;
            Complete();
            return;
        }

        BeginSeek(lba, now, DriveAction.Read);
        _eventAt += _blockTime; // the first sector arrives one sector's time after the seek
    }

    private void BeginSeek(long target, long now, DriveAction action)
    {
        var dvd = IsDvd;
        if (_blockTime == 0 || action != DriveAction.Read)
        {
            _blockTime = Clock * IPs2Disc.SectorSize / (dvd ? 1_382_400L * 4 : 153_600L * 24);
        }

        long seekTime;
        var delta = Math.Abs(target - _currentSector);
        if (!_spinning)
        {
            seekTime = Clock / 3;
            _spinning = true;
        }
        else if (delta < (dvd ? 16 : 8))
        {
            seekTime = delta * _blockTime;
        }
        else if (delta < (dvd ? 14_764 : 4_371))
        {
            seekTime = Clock * 30 / 1000;
        }
        else
        {
            seekTime = Clock * 100 / 1000;
        }

        _ready = ReadyBusy | ReadyMechaInit;
        _status = StatusSeek;
        _sticky |= _status;
        _action = action;
        _currentSector = target;
        _eventAt = now + Math.Max(1, seekTime);
    }

    /// <summary>Runs the drive up to <paramref name="now"/>: seeks end, sectors arrive.</summary>
    public void Advance(long now)
    {
        while (!_bufferFull && now >= _eventAt)
        {
            var at = _eventAt;
            switch (_action)
            {
                case DriveAction.Seek:
                case DriveAction.Standby:
                    _action = DriveAction.None;
                    _eventAt = long.MaxValue;
                    _status = StatusPause;
                    Complete();
                    break;
                case DriveAction.Stop:
                    _action = DriveAction.None;
                    _eventAt = long.MaxValue;
                    _spinning = false;
                    _currentSector = 0;
                    _status = StatusStop;
                    Complete();
                    break;
                case DriveAction.Read:
                    _status = StatusRead;
                    _sticky |= _status;
                    FillSector(_readLba);
                    _currentSector = _readLba;
                    _readLba++;
                    _readRemaining--;
                    _bufferFull = true;
                    _eventAt = at + _blockTime;
                    break;
                default:
                    _eventAt = long.MaxValue;
                    break;
            }
        }
    }

    /// <summary>DMA channel 3 took the waiting sector; the last one ends the read.</summary>
    public void TakeSector(long now)
    {
        if (!_bufferFull)
        {
            return;
        }

        _bufferFull = false;
        if (_readRemaining == 0)
        {
            _action = DriveAction.None;
            _eventAt = long.MaxValue;
            _status = StatusPause;
            _currentSector = _readLba;
            Complete();
            return;
        }

        if (_eventAt < now)
        {
            _eventAt = now; // the drive kept reading while the sector waited
        }
    }

    private void FillSector(long lba)
    {
        var block = _buffer.AsSpan(0, _blockSize);
        block.Clear();
        var disc = _disc!;
        switch (_readKind)
        {
            case 2:
            {
                // DVD block: layer and sector number, then the data (ps2tek: ReadDvd).
                var layer = disc.Type == Ps2DiscType.DvdDualLayer && disc.Layer1Start > 0 && lba >= disc.Layer1Start ? 1 : 0;
                var number = (layer == 1 ? lba - disc.Layer1Start : lba) + 0x30000;
                block[0] = (byte)(0x20 | layer);
                block[1] = (byte)(number >> 16);
                block[2] = (byte)(number >> 8);
                block[3] = (byte)number;
                disc.ReadSector(lba, block.Slice(12, IPs2Disc.SectorSize));
                break;
            }

            case 1:
                // Audio: the raw sector (subchannel bytes, when asked for, read as zeros).
                if (disc.Cd is { } audio)
                {
                    audio.ReadSector((int)(audio.Tracks[0].Start + lba), _raw);
                    _raw.CopyTo(block);
                }

                break;
            default:
                if (_blockSize == IPs2Disc.SectorSize)
                {
                    disc.ReadSector(lba, block);
                }
                else if (disc.Cd is { } cd)
                {
                    // 2,328 bytes from the subheader on (mode 2), 2,340 from the header on.
                    cd.ReadSector((int)(cd.Tracks[0].Start + lba), _raw);
                    _raw.AsSpan(_blockSize == 2328 ? 24 : 12, _blockSize).CopyTo(block);
                }
                else
                {
                    // No raw sectors: the user data where it would sit, the header and subheader as zeros.
                    disc.ReadSector(lba, _raw);
                    _raw.AsSpan(0, IPs2Disc.SectorSize).CopyTo(block[(_blockSize == 2328 ? 8 : 12)..]);
                }

                break;
        }
    }

    private void WriteToc()
    {
        var disc = _disc!;
        if (disc.Type != Ps2DiscType.Cd)
        {
            // The DVD's physical format information (fixed values, then the last sector + 30000h − 1).
            Span<byte> toc = stackalloc byte[2048];
            toc.Clear();
            var dual = disc.Type == Ps2DiscType.DvdDualLayer && disc.Layer1Start > 0;
            toc[0] = (byte)(dual ? 0x24 : 0x04);
            toc[1] = 0x02;
            toc[2] = 0xF2;
            toc[4] = (byte)(dual ? 0x41 : 0x86);
            toc[5] = (byte)(dual ? 0x95 : 0x72);
            toc[12] = 0x01;
            toc[13] = 0x02;
            toc[14] = (byte)(dual ? 0x21 : 0x01);
            toc[15] = (byte)(dual ? 0x10 : 0x00);
            toc[17] = 0x03;
            BinaryPrimitives.WriteUInt32BigEndian(toc[20..], (uint)((dual ? disc.Layer1Start : disc.SectorCount) + 0x30000 - 1));
            DirectTransfer?.Invoke(toc);
            return;
        }

        Span<byte> cdToc = stackalloc byte[1024];
        cdToc.Clear();
        var tracks = disc.Cd?.Tracks;
        var first = tracks is { Count: > 0 } ? tracks[0].Number : 1;
        var last = tracks is { Count: > 0 } ? tracks[^1].Number : 1;
        var leadOut = disc.Cd?.LeadOut ?? (int)disc.SectorCount + 150;
        cdToc[0] = 0x41;
        cdToc[2] = 0xA0;
        cdToc[7] = Bcd(first);
        cdToc[12] = 0xA1;
        cdToc[17] = Bcd(last);
        cdToc[22] = 0xA2;
        WriteMsf(cdToc[27..], leadOut);
        if (tracks is not null)
        {
            foreach (var track in tracks)
            {
                if (track.Number is < 1 or > 99)
                {
                    continue;
                }

                var entry = cdToc[(track.Number * 10 + 30)..];
                entry[0] = (byte)(track.Type == CdTrackType.Audio ? 0x01 : 0x41);
                entry[2] = Bcd(track.Number);
                WriteMsf(entry[7..], track.Start);
            }
        }
        else
        {
            cdToc[40] = 0x41;
            cdToc[42] = 0x01;
            WriteMsf(cdToc[47..], 150);
        }

        DirectTransfer?.Invoke(cdToc);
    }

    private static void WriteMsf(Span<byte> destination, int address)
    {
        destination[0] = Bcd(address / (60 * 75));
        destination[1] = Bcd(address / 75 % 60);
        destination[2] = Bcd(address % 75);
    }

    private static byte Bcd(int value) => (byte)((value / 10 % 10) << 4 | value % 10);

    private static int FromBcd(byte value) => (value >> 4) * 10 + (value & 0xF);

    // ---- S commands ----

    private int Config0 => NewNvmLayout ? 0x270 : 0x280;

    private int Config1 => NewNvmLayout ? 0x2B0 : 0x300;

    private const int Config2 = 0x200;

    private int ConsoleIdOffset => NewNvmLayout ? 0x1F0 : 0x1C8;

    private int ILinkIdOffset => NewNvmLayout ? 0x1E0 : 0x1C0;

    private int ModelNumberOffset => NewNvmLayout ? 0x1B0 : 0x1A0;

    private const int RegionParamsOffset = 0x180;
    private const int MacOffset = 0x198;

    private void Result(int count)
    {
        _sResultCount = Math.Min(count, _sResult.Length);
        _sResultPos = 0;
        Array.Clear(_sResult);
    }

    private void RunSCommand(byte command, long now)
    {
        _sCommand = command;
        var p = _sParams.AsSpan(0, _sParamCount);
        _sParamCount = 0;
        switch (command)
        {
            case 0x02: // ReadSubQ: the position as the subchannel reports it
                Result(11);
                _sResult[1] = 0x41;
                _sResult[2] = 0x01;
                _sResult[3] = 0x01;
                WriteMsf(_sResult.AsSpan(4), (int)_currentSector);
                WriteMsf(_sResult.AsSpan(8), (int)_currentSector + 150);
                break;
            case 0x03:
                RunMechaconCommand(p);
                break;
            case 0x05: // the tray's change detection
                Result(1);
                _sticky = _status;
                break;
            case 0x06: // open (0) or close the tray
                Result(1);
                var open = p.Length > 0 && p[0] == 0;
                if (open == _trayOpen)
                {
                    _sResult[0] = 0x80;
                }
                else
                {
                    TrayOpen = open;
                }

                break;
            case 0x08: // ReadRTC: BCD seconds, minutes, hours, 0, day, month, year
            {
                Result(8);
                var time = RtcNow(now);
                _sResult[1] = Bcd(time.Second);
                _sResult[2] = Bcd(time.Minute);
                _sResult[3] = Bcd(time.Hour);
                _sResult[5] = Bcd(time.Day);
                _sResult[6] = Bcd(time.Month);
                _sResult[7] = Bcd(time.Year % 100);
                break;
            }

            case 0x09: // WriteRTC
                Result(1);
                if (p.Length >= 7)
                {
                    var o = p.Length - 7;
                    try
                    {
                        var year = 2000 + FromBcd(p[o + 6]) % 100;
                        var month = Math.Clamp(FromBcd((byte)(p[o + 5] & 0x7F)), 1, 12);
                        var day = Math.Clamp(FromBcd(p[o + 4]), 1, DateTime.DaysInMonth(year, month));
                        var time = new DateTime(year, month, day, FromBcd(p[o + 2]) % 24, FromBcd(p[o + 1]) % 60, FromBcd(p[o]) % 60);
                        _rtcBaseTicks = time.Ticks;
                        _rtcBaseCycle = now;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        _sResult[0] = 0x80;
                    }
                }

                break;
            case 0x0A: // ReadNVM: a 16-bit word, high byte first in the result
            {
                Result(3);
                var address = p.Length >= 2 ? (p[0] << 8) | p[1] : 0;
                if (address * 2 + 1 < NvmSize)
                {
                    _sResult[1] = Nvm[address * 2 + 1];
                    _sResult[2] = Nvm[address * 2];
                }
                else
                {
                    Result(1);
                    _sResult[0] = 0xFF;
                }

                break;
            }

            case 0x0B: // WriteNVM
            {
                Result(1);
                var address = p.Length >= 2 ? (p[0] << 8) | p[1] : 0;
                if (p.Length >= 4 && address * 2 + 1 < NvmSize)
                {
                    Nvm[address * 2] = p[3];
                    Nvm[address * 2 + 1] = p[2];
                }
                else
                {
                    _sResult[0] = 0xFF;
                }

                break;
            }

            case 0x12: // ReadILinkId
                Result(9);
                ReadNvmOrDefault(ILinkIdOffset, [0x00, 0xAC, 0xFF, 0xFF, 0xFF, 0xFF, 0xB9, 0x86]);
                break;
            case 0x13: // WriteILinkId
                Result(1);
                WriteNvm(ILinkIdOffset, p);
                break;
            case 0x15: // ForbidDVD
                Result(1);
                _sResult[0] = 5;
                break;
            case 0x17: // ReadModelNumber (two halves of 8 bytes)
                Result(9);
                Nvm.AsSpan(ModelNumberOffset + (p.Length > 0 ? p[0] & 8 : 0), 8).CopyTo(_sResult.AsSpan(1));
                break;
            case 0x18: // WriteModelNumber
                Result(1);
                if (p.Length >= 9)
                {
                    p.Slice(1, 8).CopyTo(Nvm.AsSpan(ModelNumberOffset + (p[0] & 8)));
                }

                break;
            case 0x1A: // BootCertify
                Result(1);
                _sResult[0] = 1;
                break;
            case 0x1E: // Remote2Read
                Result(5);
                _sResult[1] = 0x14;
                break;
            case 0x20: // Remote2_6
                Result(3);
                _sResult[1] = 0x01;
                break;
            case 0x22: // ReadWakeUpTime
                Result(10);
                break;
            case 0x27: // PS1 boot parameter (Chinese models)
                Result(13);
                break;
            case 0x32: // GetMediumRemoval
                Result(2);
                break;
            case 0x36: // ReadRegionParams: the MagicGate zone, then the region parameters from the NVM
                Result(15);
                _sResult[1] = (byte)(1 << (int)(MechaconVersion & 7));
                Nvm.AsSpan(RegionParamsOffset, 8).CopyTo(_sResult.AsSpan(3));
                break;
            case 0x37: // ReadMAC
                Result(9);
                Nvm.AsSpan(MacOffset, 8).CopyTo(_sResult.AsSpan(1));
                break;
            case 0x38: // WriteMAC
                Result(1);
                WriteNvm(MacOffset, p);
                break;
            case 0x3E: // WriteRegionParams
                Result(1);
                if (p.Length > 2)
                {
                    WriteNvm(RegionParamsOffset, p[2..]);
                }

                break;
            case 0x40: // OpenConfig: read (0) or write (1), the area, the number of 16-byte blocks
                Result(1);
                _configMode = p.Length > 0 ? p[0] : 0;
                _configArea = p.Length > 1 ? p[1] : 0;
                _configBlocks = p.Length > 2 ? p[2] : 0;
                _configIndex = 0;
                break;
            case 0x41: // ReadConfig
                Result(16);
                if (_configMode != 0)
                {
                    _sResult[0] = 0x80;
                }
                else if (ConfigOffset() is { } read)
                {
                    Nvm.AsSpan(read, 16).CopyTo(_sResult);
                }

                break;
            case 0x42: // WriteConfig
                Result(1);
                if (_configMode == 1 && ConfigOffset() is { } write && p.Length >= 16)
                {
                    p[..16].CopyTo(Nvm.AsSpan(write));
                }
                else
                {
                    _sResult[0] = 0x80;
                }

                break;
            case 0x43: // CloseConfig
                Result(1);
                _configMode = _configArea = _configBlocks = _configIndex = 0;
                break;
            case 0x84: // MagicGate authentication, answered as emulators do
                Result(13);
                ReadOnlySpan<byte> auth84 = [0x21, 0xDC, 0x31, 0x96, 0xCE, 0x72, 0xE0, 0xC8, 0x69, 0xDA, 0x34, 0x9B];
                auth84.CopyTo(_sResult.AsSpan(1));
                break;
            case 0x85:
                Result(13);
                ReadOnlySpan<byte> auth85 = [0xEB, 0x01, 0xC7, 0xA9, 0x3F, 0x9C, 0x5B, 0x19, 0x31, 0xA0, 0xB3, 0xA3];
                auth85.CopyTo(_sResult.AsSpan(1));
                break;
            case 0x8D: // MagicGate: data in
                Result(1);
                if (_mgSize + p.Length > _mgBuffer.Length)
                {
                    _sResult[0] = 0x80;
                }
                else
                {
                    p.CopyTo(_mgBuffer.AsSpan(_mgSize));
                    _mgSize += p.Length;
                }

                break;
            case 0x8E: // MagicGate: data out
            {
                var count = Math.Min(16, _mgSize);
                Result(count);
                _mgBuffer.AsSpan(0, count).CopyTo(_sResult);
                _mgBuffer.AsSpan(count, _mgSize - count).CopyTo(_mgBuffer);
                _mgSize -= count;
                break;
            }

            case 0x90: // MagicGate: start of a header
                Result(1);
                _mgSize = 0;
                break;
            case 0x91: // MagicGate: BIT length
                Result(3);
                break;
            default:
                // The rest (power, LEDs, audio out, the remote, game start notices, MagicGate steps…) succeed with 0.
                Result(1);
                break;
        }
    }

    private void RunMechaconCommand(ReadOnlySpan<byte> p)
    {
        var sub = p.Length > 0 ? p[0] : (byte)0;
        switch (sub)
        {
            case 0x00: // the mechacon version
                Result(4);
                BinaryPrimitives.WriteUInt32LittleEndian(_sResult, MechaconVersion);
                break;
            case 0x30:
                Result(2);
                _sResult[0] = _status;
                _sResult[1] = (byte)((_status & StatusTrayOpen) != 0 ? 8 : 0);
                break;
            case 0x44: // write the console id
                Result(1);
                if (p.Length > 1)
                {
                    WriteNvm(ConsoleIdOffset, p[1..]);
                }

                break;
            case 0x45: // read the console id
                Result(9);
                Nvm.AsSpan(ConsoleIdOffset, 8).CopyTo(_sResult.AsSpan(1));
                break;
            case 0xEF: // the temperature
                Result(3);
                _sResult[1] = 0x0F;
                _sResult[2] = 0x05;
                break;
            case 0xFD: // the renewal date (BCD)
                Result(6);
                _sResult[1] = 0x04;
                _sResult[2] = 0x12;
                _sResult[3] = 0x10;
                _sResult[4] = 0x01;
                _sResult[5] = 0x30;
                break;
            default:
                Result(1);
                _sResult[0] = 0x81;
                break;
        }
    }

    private void ReadNvmOrDefault(int offset, ReadOnlySpan<byte> fallback)
    {
        var stored = Nvm.AsSpan(offset, 8);
        (stored.IndexOfAnyExcept((byte)0) >= 0 ? stored : fallback).CopyTo(_sResult.AsSpan(1));
    }

    private void WriteNvm(int offset, ReadOnlySpan<byte> data) => data[..Math.Min(8, data.Length)].CopyTo(Nvm.AsSpan(offset));

    /// <summary>The NVM offset of the open configuration area's next 16-byte block, or null past its end.</summary>
    private int? ConfigOffset()
    {
        var limit = _configArea switch { 0 => 4, 1 => 2, _ => 7 };
        if (_configIndex >= _configBlocks || _configIndex >= limit)
        {
            return null;
        }

        var start = _configArea switch { 0 => Config0, 2 => Config2, _ => Config1 };
        return start + _configIndex++ * 16;
    }

    private DateTime RtcNow(long now) => new DateTime(_rtcBaseTicks).AddSeconds((now - _rtcBaseCycle) / (double)Clock);

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBool(_trayOpen);
        writer.WriteByte(_nCommand);
        writer.WriteInt32(_nParamCount);
        writer.WriteBytes(_nParams);
        writer.WriteByte(_ready);
        writer.WriteByte(_error);
        writer.WriteByte(_howTo);
        writer.WriteByte(_iStat);
        writer.WriteByte(_status);
        writer.WriteByte(_sticky);
        writer.WriteByte(_sCommand);
        writer.WriteInt32(_sParamCount);
        writer.WriteBytes(_sParams);
        writer.WriteInt32(_sResultCount);
        writer.WriteInt32(_sResultPos);
        writer.WriteBytes(_sResult);
        writer.WriteBool(_spinning);
        writer.WriteInt32(_spindle);
        writer.WriteInt64(_currentSector);
        writer.WriteByte((byte)_action);
        writer.WriteInt64(_eventAt);
        writer.WriteInt64(_readLba);
        writer.WriteInt64(_readRemaining);
        writer.WriteInt32(_blockSize);
        writer.WriteInt32(_readKind);
        writer.WriteInt64(_blockTime);
        writer.WriteBool(_bufferFull);
        writer.WriteBytes(_buffer);
        writer.WriteInt32(_configMode);
        writer.WriteInt32(_configArea);
        writer.WriteInt32(_configBlocks);
        writer.WriteInt32(_configIndex);
        writer.WriteInt32(_mgSize);
        writer.WriteBytes(_mgBuffer);
        writer.WriteInt64(_rtcBaseTicks);
        writer.WriteInt64(_rtcBaseCycle);
        writer.WriteBytes(Nvm);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported CDVD state version.");
        }

        _trayOpen = reader.ReadBool();
        _nCommand = reader.ReadByte();
        _nParamCount = Math.Clamp(reader.ReadInt32(), 0, _nParams.Length);
        reader.ReadBytes(_nParams);
        _ready = reader.ReadByte();
        _error = reader.ReadByte();
        _howTo = reader.ReadByte();
        _iStat = reader.ReadByte();
        _status = reader.ReadByte();
        _sticky = reader.ReadByte();
        _sCommand = reader.ReadByte();
        _sParamCount = Math.Clamp(reader.ReadInt32(), 0, _sParams.Length);
        reader.ReadBytes(_sParams);
        _sResultCount = Math.Clamp(reader.ReadInt32(), 0, _sResult.Length);
        _sResultPos = Math.Clamp(reader.ReadInt32(), 0, _sResult.Length);
        reader.ReadBytes(_sResult);
        _spinning = reader.ReadBool();
        _spindle = reader.ReadInt32();
        _currentSector = reader.ReadInt64();
        _action = (DriveAction)reader.ReadByte();
        _eventAt = reader.ReadInt64();
        _readLba = reader.ReadInt64();
        _readRemaining = reader.ReadInt64();
        _blockSize = Math.Clamp(reader.ReadInt32(), 0, MaxBlock);
        _readKind = reader.ReadInt32();
        _blockTime = reader.ReadInt64();
        _bufferFull = reader.ReadBool();
        reader.ReadBytes(_buffer);
        _configMode = reader.ReadInt32();
        _configArea = reader.ReadInt32();
        _configBlocks = reader.ReadInt32();
        _configIndex = reader.ReadInt32();
        _mgSize = Math.Clamp(reader.ReadInt32(), 0, _mgBuffer.Length);
        reader.ReadBytes(_mgBuffer);
        _rtcBaseTicks = reader.ReadInt64();
        _rtcBaseCycle = reader.ReadInt64();
        reader.ReadBytes(Nvm);
        reader.EndChunk();
    }
}
