using GameVault.Core.Audio;
using GameVault.Core.Cartridge;
using GameVault.Core.Cpu.M68000;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.SegaCd;

/// <summary>
/// The Sega CD (Mega-CD), as the Genesis sees it plugged in underneath (sega-cd-32x.md, milestone 1): the sub CPU
/// and its memory, the gate array that joins the two CPUs, the drive (<see cref="SegaCdDrive"/>), the CDC
/// (<see cref="Lc8951"/>), the PCM chip (<see cref="Rf5c164"/>) and the graphics ASIC (<see cref="SegaCdGraphics"/>).
/// <see cref="Genesis.GenesisConsole"/> routes the main CPU's $000000–$3FFFFF and $A12000–$A1202F here and runs the
/// unit in step with its master clock.
/// </summary>
/// <remarks>
/// The sub CPU runs at 12.5 MHz. Word RAM is held in its 2M layout; in 1M mode bank 0 is its even words and bank 1
/// its odd ones, as on the hardware, so switching modes moves nothing. Interrupts: 1 the graphics ASIC, 2 the main
/// CPU, 3 the timer, 4 the drive, 5 the CDC. Register behaviour follows Sega's Mega-CD hardware manual as Genesis
/// Plus GX's scd.c applies it, read for behaviour only.
/// </remarks>
public sealed class SegaCdUnit : IM68000Bus, IStateful, IBatteryBacked
{
    public const int BiosSize = 0x20000;
    public const int SubClock = 12_500_000;

    private const int ClocksPerTick = 384; // the timer, stopwatch and PCM clock (30.72 µs)

    private static readonly StateTag Tag = StateTag.From("SCD ");

    private readonly byte[] _bios;
    private readonly byte[] _prgRam = new byte[0x80000];
    private readonly byte[] _wordRam = new byte[0x40000];
    private readonly byte[] _backupRam = new byte[0x2000];
    private readonly byte[] _commCommand = new byte[16];
    private readonly byte[] _commStatus = new byte[16];
    private readonly long _masterClock;

    private bool _subReset = true;
    private bool _subBusRequest;
    private byte _writeProtect;
    private int _prgBank;
    private bool _mode1M;
    private bool _dmna;
    private bool _ret = true;
    private int _priorityMode;
    private ushort _hintVector;
    private byte _mainFlags;
    private byte _subFlags;
    private byte _led;
    private byte _timer;
    private int _timerCount;
    private int _stopwatch;
    private byte _interruptMask;
    private byte _pending;
    private ushort _fader;
    private byte _driveControl;
    private byte _fontColor;
    private ushort _fontBits;
    private ushort _dmaAddressRegister;
    private int _dmaAddress;
    private bool _dmaRunning;
    private bool _cdcInterrupt;

    private long _subCycles;
    private long _frameBase;      // master clocks before this frame
    private int _tickCountdown = ClocksPerTick;
    private long _cddTicks;
    private long _lastCddCycle;
    private float _lastLeft;
    private float _lastRight;

    /// <param name="bios">The player's 128 KB Sega CD BIOS.</param>
    /// <param name="disc">The disc in the drive, or none.</param>
    /// <param name="masterClock">The Genesis' master clock (NTSC or PAL).</param>
    /// <exception cref="RomFormatException">The BIOS isn't 128 KB.</exception>
    public SegaCdUnit(byte[] bios, ICdImage? disc, double masterClock)
    {
        if (bios.Length != BiosSize)
        {
            throw new RomFormatException("The Sega CD BIOS file should be 128 KB (131,072 bytes).");
        }

        _bios = bios;
        _masterClock = (long)Math.Round(masterClock);
        Cpu = new M68000Cpu(this);
        Drive = new SegaCdDrive(disc);
    }

    /// <summary>
    /// The disc's identity for save states and saves: a hash of its first sixteen sectors of track 1 (the system and
    /// boot areas), or of nothing without a disc.
    /// </summary>
    public ulong DiscHash
    {
        get
        {
            var hash = 14695981039346656037UL;
            if (Drive.Disc is { } disc)
            {
                var sector = new byte[ICdImage.SectorSize];
                var start = disc.Tracks[0].Start;
                for (var i = 0; i < 16; i++)
                {
                    disc.ReadSector(start + i, sector);
                    foreach (var b in sector)
                    {
                        hash = (hash ^ b) * 1099511628211UL;
                    }
                }
            }

            return hash;
        }
    }

    public M68000Cpu Cpu { get; }

    public SegaCdDrive Drive { get; }

    public Lc8951 Cdc { get; } = new();

    public Rf5c164 Pcm { get; } = new();

    public SegaCdGraphics Graphics { get; } = new();

    public Span<byte> PrgRam => _prgRam;

    public Span<byte> WordRam => _wordRam;

    /// <summary>The audio sink the PCM and CD-DA output go to (the Genesis' resampler).</summary>
    public BandLimitedResampler? Audio { get; set; }

    public bool HasBattery => true;

    /// <summary>The 8 KB backup RAM, the system's saves.</summary>
    public Span<byte> BatteryRam => _backupRam;

    /// <summary>The sub CPU is running (not held in reset or for the main CPU's bus request).</summary>
    public bool SubRunning => !_subReset && !_subBusRequest;

    public void Power()
    {
        Array.Clear(_prgRam);
        Array.Clear(_wordRam);
        Pcm.Power();
        Cdc.Power();
        Reset();
        _subCycles = 0;
        _frameBase = 0;
        _cddTicks = 0;
        _lastCddCycle = 0;
    }

    /// <summary>The console's reset: the gate array returns to its power-on state and the sub CPU is held.</summary>
    public void Reset()
    {
        Array.Clear(_commCommand);
        Array.Clear(_commStatus);
        _subReset = true;
        _subBusRequest = false;
        _writeProtect = 0;
        _prgBank = 0;
        _mode1M = false;
        _dmna = false;
        _ret = true;
        _priorityMode = 0;
        _hintVector = 0;
        _mainFlags = _subFlags = 0;
        _led = 0;
        _timer = 0;
        _timerCount = 0;
        _stopwatch = 0;
        _interruptMask = 0;
        _pending = 0;
        _fader = 0;
        _driveControl = 0;
        _fontColor = 0;
        _fontBits = 0;
        _dmaAddressRegister = 0;
        _dmaAddress = 0;
        _dmaRunning = false;
        _cdcInterrupt = false;
        _tickCountdown = ClocksPerTick;
        Pcm.Reset();
        Cdc.Reset();
        Graphics.Reset();
        Drive.Reset();
        _lastLeft = _lastRight = 0;
    }

    // ---- Timing ----

    /// <summary>Runs the unit up to <paramref name="master"/>, the main CPU's master clock within the frame.</summary>
    public void RunTo(long master)
    {
        var target = (long)((_frameBase + master) * ((double)SubClock / _masterClock));
        while (_subCycles < target)
        {
            int cycles;
            if (SubRunning)
            {
                Cpu.InterruptLevel = InterruptLevel();
                cycles = Cpu.Step();
            }
            else
            {
                cycles = (int)Math.Min(target - _subCycles, 64);
            }

            Advance(cycles);
        }
    }

    /// <summary>The frame ended after <paramref name="frameClocks"/> master clocks.</summary>
    public void EndFrame(long frameClocks) => _frameBase += frameClocks;

    private int InterruptLevel()
    {
        var active = _pending & _interruptMask;
        for (var level = 6; level >= 1; level--)
        {
            if ((active & (1 << level)) != 0)
            {
                return level;
            }
        }

        return 0;
    }

    private void Raise(int level)
    {
        if ((_interruptMask & (1 << level)) != 0)
        {
            _pending |= (byte)(1 << level);
        }
    }

    private void Advance(int cycles)
    {
        _subCycles += cycles;

        _tickCountdown -= cycles;
        while (_tickCountdown <= 0)
        {
            _tickCountdown += ClocksPerTick;
            Tick();
        }

        // The drive's 75 Hz clock.
        while (_subCycles >= (_cddTicks + 1) * SubClock / 75)
        {
            _cddTicks++;
            _lastCddCycle = _cddTicks * SubClock / 75;
            Drive.Tick(Cdc);
            if ((_driveControl & 0x04) != 0)
            {
                Raise(4); // HOCK: the drive reports each tick
            }
        }

        if (Graphics.Busy)
        {
            Graphics.BusyClocks -= cycles;
            if (!Graphics.Busy)
            {
                Graphics.BusyClocks = 0;
                Raise(1);
            }
        }

        RunDma(cycles);

        var cdc = Cdc.Interrupt;
        if (cdc && !_cdcInterrupt)
        {
            Raise(5);
        }

        _cdcInterrupt = cdc;
    }

    /// <summary>Every 384 sub CPU clocks: the stopwatch, the timer and one PCM sample.</summary>
    private void Tick()
    {
        _stopwatch = (_stopwatch + 1) & 0x0FFF;
        if (_timer != 0 && --_timerCount <= 0)
        {
            _timerCount = _timer;
            Raise(3);
        }

        Pcm.Sample();
        if (Audio is { } audio)
        {
            float left = Pcm.Left * (0.25f / 32768), right = Pcm.Right * (0.25f / 32768);
            if (Drive.AudioPlaying)
            {
                var span = SubClock / 75;
                var index = (int)Math.Clamp((_subCycles - _lastCddCycle) * 588 / span, 0, 587);
                var volume = ((_fader >> 4) & 0x7FF) / 1024f * (0.3f / 32768);
                left += Drive.AudioSector[index * 2] * volume;
                right += Drive.AudioSector[index * 2 + 1] * volume;
            }

            if (left != _lastLeft || right != _lastRight)
            {
                var clock = (long)(_subCycles * ((double)_masterClock / SubClock)) - _frameBase;
                audio.AddDelta((int)Math.Max(0, clock), left - _lastLeft, right - _lastRight);
                _lastLeft = left;
                _lastRight = right;
            }
        }
    }

    /// <summary>The CDC's DMA to PCM RAM, PRG-RAM or Word RAM: a byte every two sub CPU clocks.</summary>
    private void RunDma(int cycles)
    {
        if (!Cdc.DmaActive)
        {
            _dmaRunning = false;
            return;
        }

        if (!_dmaRunning)
        {
            _dmaRunning = true;
            _dmaAddress = Cdc.Destination switch
            {
                4 => (_dmaAddressRegister & 0x3FF) << 2,
                _ => _dmaAddressRegister << 3,
            };
        }

        for (var i = 0; i < (cycles + 1) / 2 && Cdc.DmaActive; i++)
        {
            var value = Cdc.NextByte();
            switch (Cdc.Destination)
            {
                case 4:
                    Pcm.WriteWindow(_dmaAddress & 0x0FFF, value);
                    break;
                case 5:
                    _prgRam[_dmaAddress & 0x7FFFF] = value;
                    break;
                case 7:
                    if (_mode1M)
                    {
                        _wordRam[BankAddress(SubBank, _dmaAddress & 0x1FFFF)] = value;
                    }
                    else
                    {
                        _wordRam[_dmaAddress & 0x3FFFF] = value;
                    }

                    break;
            }

            _dmaAddress++;
        }
    }

    // ---- Word RAM ----

    /// <summary>In 1M mode RET gives bank 1 to the main CPU when set, bank 0 when clear; the sub CPU has the other.</summary>
    private int MainBank => _ret ? 1 : 0;

    private int SubBank => _ret ? 0 : 1;

    /// <summary>A 1M bank's byte in the 2M layout: bank 0 is the even words, bank 1 the odd.</summary>
    private static int BankAddress(int bank, int offset) => ((((offset >> 1) << 1) | bank) << 1 | (offset & 1)) & 0x3FFFF;

    private bool MainHasWordRam => _mode1M || _ret;

    private bool SubHasWordRam => _mode1M || !_ret;

    /// <summary>A dot written into 1M mode's dot image (or by the graphics ASIC), as the priority mode allows.</summary>
    private void WriteDot(int byteAddress, bool high, int value)
    {
        var old = _wordRam[byteAddress];
        var current = high ? old >> 4 : old & 0x0F;
        value &= 0x0F;
        var keep = _priorityMode switch
        {
            1 => current != 0, // underwrite: only where the dot is clear
            2 => value == 0, // overwrite: only with a non-clear dot
            _ => false,
        };
        if (keep)
        {
            return;
        }

        _wordRam[byteAddress] = (byte)(high ? (old & 0x0F) | (value << 4) : (old & 0xF0) | value);
    }

    // ---- The main CPU's side ----

    public byte MainReadByte(uint address)
    {
        address &= 0x3FFFFF;
        if (address < 0x200000)
        {
            var offset = (int)(address & 0x3FFFF);
            if (offset < 0x20000)
            {
                // The H-interrupt vector's low word is the gate array's register.
                return offset switch
                {
                    0x72 => (byte)(_hintVector >> 8),
                    0x73 => (byte)_hintVector,
                    _ => _bios[offset],
                };
            }

            return _prgRam[(_prgBank << 17) | (offset - 0x20000)];
        }

        var word = (int)(address & 0x3FFFF);
        if (_mode1M)
        {
            return word < 0x20000 ? _wordRam[BankAddress(MainBank, word)] : (byte)0;
        }

        return MainHasWordRam ? _wordRam[word] : (byte)0;
    }

    public ushort MainReadWord(uint address) => (ushort)((MainReadByte(address) << 8) | MainReadByte(address | 1));

    public void MainWriteByte(uint address, byte value)
    {
        address &= 0x3FFFFF;
        if (address < 0x200000)
        {
            var offset = (int)(address & 0x3FFFF);
            if (offset >= 0x20000)
            {
                _prgRam[(_prgBank << 17) | (offset - 0x20000)] = value;
            }

            return;
        }

        var word = (int)(address & 0x3FFFF);
        if (_mode1M)
        {
            if (word < 0x20000)
            {
                _wordRam[BankAddress(MainBank, word)] = value;
            }
        }
        else if (MainHasWordRam)
        {
            _wordRam[word] = value;
        }
    }

    public void MainWriteWord(uint address, ushort value)
    {
        MainWriteByte(address, (byte)(value >> 8));
        MainWriteByte(address | 1, (byte)value);
    }

    /// <summary>A read of the gate array at $A12000–$A1203F.</summary>
    public byte MainReadGate(uint address)
    {
        var a = (int)(address & 0x3F);
        return a switch
        {
            0x00 => (byte)(((_interruptMask & 0x04) != 0 ? 0x80 : 0) | ((_pending & 0x04) != 0 ? 0x01 : 0)),
            0x01 => (byte)((_subBusRequest ? 0x02 : 0) | (_subReset ? 0 : 0x01)),
            0x02 => _writeProtect,
            0x03 => MemoryMode(),
            0x04 => CdcMode(),
            0x06 => (byte)(_hintVector >> 8),
            0x07 => (byte)_hintVector,
            0x08 => Cdc.Destination == 2 ? HostByte(high: true) : (byte)0,
            0x09 => _hostLow,
            0x0C => (byte)(_stopwatch >> 8),
            0x0D => (byte)_stopwatch,
            0x0E => _mainFlags,
            0x0F => _subFlags,
            >= 0x10 and < 0x20 => _commCommand[a - 0x10],
            >= 0x20 and < 0x30 => _commStatus[a - 0x20],
            _ => 0,
        };
    }

    public ushort MainReadGateWord(uint address)
    {
        if ((address & 0x3E) == 0x08)
        {
            return Cdc.Destination == 2 ? Cdc.ReadHostWord() : (ushort)0;
        }

        return (ushort)((MainReadGate(address & ~1u) << 8) | MainReadGate(address | 1));
    }

    private byte _hostLow;

    private byte HostByte(bool high)
    {
        var word = Cdc.ReadHostWord();
        _hostLow = (byte)word;
        return (byte)(word >> 8);
    }

    public void MainWriteGate(uint address, byte value)
    {
        var a = (int)(address & 0x3F);
        switch (a)
        {
            case 0x00:
                if ((value & 0x01) != 0)
                {
                    Raise(2); // IFL2: the main CPU interrupts the sub CPU
                }

                break;
            case 0x01:
            {
                var reset = (value & 0x01) == 0;
                if (_subReset && !reset)
                {
                    Cpu.Reset(); // released: the sub CPU starts from PRG-RAM's vectors
                }

                _subReset = reset;
                _subBusRequest = (value & 0x02) != 0;
                break;
            }

            case 0x02:
                _writeProtect = value;
                break;
            case 0x03:
                _prgBank = value >> 6;
                if ((value & 0x02) != 0)
                {
                    _dmna = true;
                    if (!_mode1M)
                    {
                        _ret = false; // 2M: Word RAM goes to the sub CPU
                    }
                }

                break;
            case 0x06:
                _hintVector = (ushort)((_hintVector & 0x00FF) | (value << 8));
                break;
            case 0x07:
                _hintVector = (ushort)((_hintVector & 0xFF00) | value);
                break;
            case 0x0E:
                _mainFlags = value;
                break;
            case >= 0x10 and < 0x20:
                _commCommand[a - 0x10] = value;
                break;
        }
    }

    public void MainWriteGateWord(uint address, ushort value)
    {
        if ((address & 0x3E) == 0x00)
        {
            // A word write to $A12000 sets both halves (the interrupt and the reset/bus request bits).
            MainWriteGate(0x00, (byte)(value >> 8));
            MainWriteGate(0x01, (byte)value);
            return;
        }

        MainWriteGate(address & ~1u, (byte)(value >> 8));
        MainWriteGate(address | 1, (byte)value);
    }

    private byte MemoryMode() =>
        (byte)((_prgBank << 6) | (_priorityMode << 3) | (_mode1M ? 0x04 : 0) | (_dmna ? 0x02 : 0) | (_ret ? 0x01 : 0));

    private byte CdcMode() =>
        (byte)((Cdc.TransferEnded ? 0x80 : 0) | (Cdc.DataReady ? 0x40 : 0) | Cdc.Destination);

    // ---- The sub CPU's side ----

    public byte ReadByte(uint address)
    {
        address &= 0xFFFFFF;
        switch (address)
        {
            case < 0x080000:
                return _prgRam[address];
            case < 0x0C0000:
                if (_mode1M)
                {
                    // The dot image: each byte is one dot of the sub CPU's bank.
                    var dot = (int)(address - 0x080000);
                    var value = _wordRam[BankAddress(SubBank, dot >> 1)];
                    return (byte)((dot & 1) == 0 ? value >> 4 : value & 0x0F);
                }

                return SubHasWordRam ? _wordRam[address - 0x080000] : (byte)0;
            case < 0x0E0000:
                return _mode1M ? _wordRam[BankAddress(SubBank, (int)(address - 0x0C0000))] : (byte)0;
            case >= 0xFE0000 and < 0xFF0000:
                return (address & 1) != 0 ? _backupRam[(address >> 1) & 0x1FFF] : (byte)0xFF;
            case >= 0xFF0000 and < 0xFF8000:
                return PcmRead(address);
            case >= 0xFF8000:
                return GateRead((int)(address & 0x1FF));
            default:
                return 0;
        }
    }

    public ushort ReadWord(uint address)
    {
        address &= 0xFFFFFE;
        if (address >= 0xFF8000 && (address & 0x1FE) == 0x08)
        {
            return Cdc.Destination == 3 ? Cdc.ReadHostWord() : (ushort)0;
        }

        if (address >= 0xFF8000 && (address & 0x1FE) is >= 0x50 and < 0x58)
        {
            return FontWord((int)(address & 0x1FE));
        }

        return (ushort)((ReadByte(address) << 8) | ReadByte(address | 1));
    }

    public void WriteByte(uint address, byte value)
    {
        address &= 0xFFFFFF;
        switch (address)
        {
            case < 0x080000:
                if (address >= (uint)(_writeProtect << 9))
                {
                    _prgRam[address] = value;
                }

                break;
            case < 0x0C0000:
                if (_mode1M)
                {
                    var dot = (int)(address - 0x080000);
                    WriteDot(BankAddress(SubBank, dot >> 1), (dot & 1) == 0, value);
                }
                else if (SubHasWordRam)
                {
                    _wordRam[address - 0x080000] = value;
                }

                break;
            case < 0x0E0000:
                if (_mode1M)
                {
                    _wordRam[BankAddress(SubBank, (int)(address - 0x0C0000))] = value;
                }

                break;
            case >= 0xFE0000 and < 0xFF0000:
                if ((address & 1) != 0)
                {
                    _backupRam[(address >> 1) & 0x1FFF] = value;
                }

                break;
            case >= 0xFF0000 and < 0xFF8000:
                PcmWrite(address, value);
                break;
            case >= 0xFF8000:
                GateWrite((int)(address & 0x1FF), value);
                break;
        }
    }

    public void WriteWord(uint address, ushort value)
    {
        address &= 0xFFFFFE;
        if (address is >= 0xFF0000 and < 0xFF8000)
        {
            PcmWrite(address | 1, (byte)value); // the PCM chip is on the low byte
            return;
        }

        if (address >= 0xFF8000)
        {
            var a = (int)(address & 0x1FE);
            switch (a)
            {
                case 0x58: Graphics.StampSize = (ushort)(value & 0x07); return;
                case 0x5A: Graphics.StampMap = value; return;
                case 0x5C: Graphics.ImageCells = value; return;
                case 0x5E: Graphics.ImageStart = value; return;
                case 0x60: Graphics.ImageOffset = value; return;
                case 0x62: Graphics.ImageWidth = value; return;
                case 0x64: Graphics.ImageHeight = value; return;
                case 0x66: StartGraphics(value); return;
            }
        }

        WriteByte(address, (byte)(value >> 8));
        WriteByte(address | 1, (byte)value);
    }

    public int AcknowledgeInterrupt(int level)
    {
        _pending &= (byte)~(1 << level);
        return -1; // autovectored
    }

    private byte PcmRead(uint address)
    {
        if ((address & 1) == 0)
        {
            return 0;
        }

        var offset = (int)(address & 0x7FFF) >> 1;
        return offset >= 0x1000 ? Pcm.ReadWindow(offset) : Pcm.ReadRegister(offset & 0x1F);
    }

    private void PcmWrite(uint address, byte value)
    {
        if ((address & 1) == 0)
        {
            return;
        }

        var offset = (int)(address & 0x7FFF) >> 1;
        if (offset >= 0x1000)
        {
            Pcm.WriteWindow(offset, value);
        }
        else
        {
            Pcm.WriteRegister(offset & 0x1F, value);
        }
    }

    private byte GateRead(int a)
    {
        switch (a)
        {
            case 0x00: return _led;
            case 0x01: return 0x01; // RES0: the peripherals are ready
            case 0x02: return _writeProtect;
            case 0x03: return MemoryMode();
            case 0x04: return CdcMode();
            case 0x05: return (byte)Cdc.RegisterAddress;
            case 0x07: return Cdc.ReadRegister();
            case 0x0A: return (byte)(_dmaAddressRegister >> 8);
            case 0x0B: return (byte)_dmaAddressRegister;
            case 0x0C: return (byte)(_stopwatch >> 8);
            case 0x0D: return (byte)_stopwatch;
            case 0x0E: return _mainFlags;
            case 0x0F: return _subFlags;
            case >= 0x10 and < 0x20: return _commCommand[a - 0x10];
            case >= 0x20 and < 0x30: return _commStatus[a - 0x20];
            case 0x31: return _timer;
            case 0x33: return _interruptMask;
            case 0x34: return (byte)(_fader >> 8);
            case 0x35: return (byte)_fader;
            case 0x37: return _driveControl;
            case >= 0x38 and < 0x42: return Drive.Status[a - 0x38];
            case >= 0x42 and < 0x4C: return Drive.Command[a - 0x42];
            case 0x4D: return _fontColor;
            case 0x4E: return (byte)(_fontBits >> 8);
            case 0x4F: return (byte)_fontBits;
            case >= 0x50 and < 0x58:
            {
                var word = FontWord(a & ~1);
                return (a & 1) == 0 ? (byte)(word >> 8) : (byte)word;
            }

            case 0x58: return (byte)(Graphics.Busy ? 0x80 : 0);
            case 0x59: return (byte)Graphics.StampSize;
            default: return 0;
        }
    }

    private void GateWrite(int a, byte value)
    {
        switch (a)
        {
            case 0x00:
                _led = value;
                break;
            case 0x03:
                _priorityMode = (value >> 3) & 3;
                _mode1M = (value & 0x04) != 0;
                if (_mode1M)
                {
                    _ret = (value & 0x01) != 0; // 1M: RET picks the banks, and the swap is done
                    _dmna = false;
                }
                else if ((value & 0x01) != 0)
                {
                    _ret = true; // 2M: Word RAM returns to the main CPU
                    _dmna = false;
                }

                break;
            case 0x04:
                Cdc.Destination = value & 7;
                break;
            case 0x05:
                Cdc.RegisterAddress = value;
                break;
            case 0x07:
                Cdc.WriteRegister(value);
                break;
            case 0x0A:
                _dmaAddressRegister = (ushort)((_dmaAddressRegister & 0x00FF) | (value << 8));
                break;
            case 0x0B:
                _dmaAddressRegister = (ushort)((_dmaAddressRegister & 0xFF00) | value);
                break;
            case 0x0C:
            case 0x0D:
                _stopwatch = 0;
                break;
            case 0x0F:
                _subFlags = value;
                break;
            case >= 0x20 and < 0x30:
                _commStatus[a - 0x20] = value;
                break;
            case 0x31:
                _timer = value;
                _timerCount = value;
                break;
            case 0x33:
                _interruptMask = (byte)(value & 0x7E);
                _pending &= _interruptMask;
                break;
            case 0x34:
                _fader = (ushort)((_fader & 0x00FF) | (value << 8));
                break;
            case 0x35:
                _fader = (ushort)((_fader & 0xFF00) | value);
                break;
            case 0x37:
                _driveControl = value;
                break;
            case >= 0x42 and < 0x4C:
                Drive.Command[a - 0x42] = (byte)(value & 0x0F);
                if (a == 0x4B)
                {
                    Drive.ProcessCommand();
                }

                break;
            case 0x4D:
                _fontColor = value;
                break;
            case 0x4E:
                _fontBits = (ushort)((_fontBits & 0x00FF) | (value << 8));
                break;
            case 0x4F:
                _fontBits = (ushort)((_fontBits & 0xFF00) | value);
                break;
            case 0x59:
                Graphics.StampSize = (ushort)(value & 0x07);
                break;
        }
    }

    /// <summary>The font generator: four bits of FONT BIT, each as the foreground or background colour's nibble.</summary>
    private ushort FontWord(int a)
    {
        var shift = 12 - (a - 0x50) * 2;
        var bits = (_fontBits >> shift) & 0x0F;
        int foreground = _fontColor >> 4, background = _fontColor & 0x0F;
        var word = 0;
        for (var i = 3; i >= 0; i--)
        {
            word = (word << 4) | (((bits >> i) & 1) != 0 ? foreground : background);
        }

        return (ushort)word;
    }

    private void StartGraphics(ushort traceTable) =>
        Graphics.Start(traceTable, _wordRam, (address, high, value) => WriteDot(address, high, value));

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        Cpu.SaveState(writer);
        Drive.SaveState(writer);
        Cdc.SaveState(writer);
        Pcm.SaveState(writer);
        Graphics.SaveState(writer);
        writer.WriteBytes(_prgRam);
        writer.WriteBytes(_wordRam);
        writer.WriteBytes(_backupRam);
        writer.WriteBytes(_commCommand);
        writer.WriteBytes(_commStatus);
        writer.WriteBool(_subReset);
        writer.WriteBool(_subBusRequest);
        writer.WriteByte(_writeProtect);
        writer.WriteInt32(_prgBank);
        writer.WriteBool(_mode1M);
        writer.WriteBool(_dmna);
        writer.WriteBool(_ret);
        writer.WriteInt32(_priorityMode);
        writer.WriteUInt16(_hintVector);
        writer.WriteByte(_mainFlags);
        writer.WriteByte(_subFlags);
        writer.WriteByte(_led);
        writer.WriteByte(_timer);
        writer.WriteInt32(_timerCount);
        writer.WriteInt32(_stopwatch);
        writer.WriteByte(_interruptMask);
        writer.WriteByte(_pending);
        writer.WriteUInt16(_fader);
        writer.WriteByte(_driveControl);
        writer.WriteByte(_fontColor);
        writer.WriteUInt16(_fontBits);
        writer.WriteUInt16(_dmaAddressRegister);
        writer.WriteInt32(_dmaAddress);
        writer.WriteBool(_dmaRunning);
        writer.WriteBool(_cdcInterrupt);
        writer.WriteByte(_hostLow);
        writer.WriteInt64(_subCycles);
        writer.WriteInt64(_frameBase);
        writer.WriteInt32(_tickCountdown);
        writer.WriteInt64(_cddTicks);
        writer.WriteInt64(_lastCddCycle);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Cpu.LoadState(reader);
        Drive.LoadState(reader);
        Cdc.LoadState(reader);
        Pcm.LoadState(reader);
        Graphics.LoadState(reader);
        reader.ReadBytes(_prgRam);
        reader.ReadBytes(_wordRam);
        reader.ReadBytes(_backupRam);
        reader.ReadBytes(_commCommand);
        reader.ReadBytes(_commStatus);
        _subReset = reader.ReadBool();
        _subBusRequest = reader.ReadBool();
        _writeProtect = reader.ReadByte();
        _prgBank = reader.ReadInt32() & 3;
        _mode1M = reader.ReadBool();
        _dmna = reader.ReadBool();
        _ret = reader.ReadBool();
        _priorityMode = reader.ReadInt32() & 3;
        _hintVector = reader.ReadUInt16();
        _mainFlags = reader.ReadByte();
        _subFlags = reader.ReadByte();
        _led = reader.ReadByte();
        _timer = reader.ReadByte();
        _timerCount = reader.ReadInt32();
        _stopwatch = reader.ReadInt32() & 0x0FFF;
        _interruptMask = reader.ReadByte();
        _pending = reader.ReadByte();
        _fader = reader.ReadUInt16();
        _driveControl = reader.ReadByte();
        _fontColor = reader.ReadByte();
        _fontBits = reader.ReadUInt16();
        _dmaAddressRegister = reader.ReadUInt16();
        _dmaAddress = reader.ReadInt32();
        _dmaRunning = reader.ReadBool();
        _cdcInterrupt = reader.ReadBool();
        _hostLow = reader.ReadByte();
        _subCycles = reader.ReadInt64();
        _frameBase = reader.ReadInt64();
        _tickCountdown = Math.Clamp(reader.ReadInt32(), 1, ClocksPerTick);
        _cddTicks = reader.ReadInt64();
        _lastCddCycle = reader.ReadInt64();
        reader.EndChunk();
        _lastLeft = _lastRight = 0;
    }
}
