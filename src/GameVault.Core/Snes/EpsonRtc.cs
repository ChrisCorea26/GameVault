using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The Epson RTC-4513 (snes-chips-3.md §2), Far East of Eden Zero's clock on the SPC7110 board, at $4840–$4842: $4840
/// is the chip select, $4841 the data (four bits), $4842 bit 7 the ready flag. After chip select 1 a game writes a mode
/// ($03 write, $0C read) and a register number, then writes or reads the sixteen BCD registers from there. Each access
/// clears ready for eight of the chip's clocks.
/// </summary>
/// <remarks>
/// Registers 0–12 are the seconds, minutes, hours, day, month and year (ones then tens), with flags in their spare bits,
/// and the weekday; 13–15 are the control registers (hold, calendar, the periodic flag, rounding to the nearest minute;
/// the periodic flag's mask, duty and period; pause, stop, 24-hour mode and test). The counters roll over the way the
/// chip does, including for digits that aren't valid BCD. Behaviour follows ares' epsonrtc. The periodic flag is set at
/// the chosen period (1/64 s, 1 s, 1 minute or 1 hour) and, in duty mode, cleared every 1/128 s.
/// </remarks>
public sealed class EpsonRtc : IStateful, ISnesRealTimeClock
{
    private const long ChipHz = 32_768 * 64; // the counter that drives everything: 2^21 a second

    private static readonly StateTag Tag = StateTag.From("ERTC");

    private enum Mode
    {
        Mode,
        Seek,
        Read,
        Write,
    }

    private readonly double _masterClock;
    private long _master;   // master clocks since power-on
    private long _ticks;    // chip clocks since power-on
    private long _readyAt;  // the chip clock when ready returns
    private int _seconds;   // seconds counted towards the minute and hour flags

    private int _chipSelect;
    private Mode _mode;
    private int _mdr;
    private int _offset;
    private bool _ready;
    private bool _holdTick;

    private int _secondLo, _secondHi, _batteryFailure;
    private int _minuteLo, _minuteHi, _resync;
    private int _hourLo, _hourHi, _meridian;
    private int _dayLo, _dayHi, _dayRam;
    private int _monthLo, _monthHi, _monthRam;
    private int _yearLo, _yearHi;
    private int _weekday;
    private int _hold, _calendar, _irqFlag, _roundSeconds;
    private int _irqMask, _irqDuty, _irqPeriod;
    private int _pause, _stop, _atime, _test;

    public EpsonRtc(double masterClock)
    {
        _masterClock = masterClock;

        // A fresh chip in ares has the calendar off and the battery-failure flag set; GameVault always starts it from a
        // real time, so the calendar runs and the battery reads as good.
        _calendar = 1;
        SetTime(new DateTime(2000, 1, 1));
    }

    /// <summary>The time as the chip holds it, decoded (for tests): 24-hour, year in the century.</summary>
    public (int Year, int Month, int Day, int Hour, int Minute, int Second, int Weekday) Time
    {
        get
        {
            var hour = _hourHi * 10 + _hourLo;
            if (_atime == 0)
            {
                hour = hour % 12 + (_meridian != 0 ? 12 : 0);
            }

            return (_yearHi * 10 + _yearLo, _monthHi * 10 + _monthLo, _dayHi * 10 + _dayLo, hour,
                _minuteHi * 10 + _minuteLo, _secondHi * 10 + _secondLo, _weekday);
        }
    }

    public void SetTime(DateTime time)
    {
        var second = Math.Min(59, time.Second);
        _secondLo = second % 10;
        _secondHi = second / 10;
        _minuteLo = time.Minute % 10;
        _minuteHi = time.Minute / 10;
        var hour = time.Hour;
        if (_atime != 0)
        {
            _hourLo = hour % 10;
            _hourHi = hour / 10;
        }
        else
        {
            _meridian = hour >= 12 ? 1 : 0;
            hour %= 12;
            if (hour == 0)
            {
                hour = 12;
            }

            _hourLo = hour % 10;
            _hourHi = hour / 10;
        }

        _dayLo = time.Day % 10;
        _dayHi = time.Day / 10;
        _monthLo = time.Month % 10;
        _monthHi = time.Month / 10;
        _yearLo = time.Year % 100 % 10;
        _yearHi = time.Year % 100 / 10;
        _weekday = (int)time.DayOfWeek;
        _resync = 1; // tells the program the time changed
    }

    public void Power()
    {
        _master = _ticks = 0;
        _readyAt = 0;
        _seconds = 0;
        _chipSelect = 0;
        _mode = Mode.Mode;
        _offset = 0;
        _ready = false;
        _holdTick = false;
    }

    // ---- The CPU's side ($4840–$4842) ----

    public byte Read(int register, byte openBus)
    {
        UpdateReady();
        switch (register & 3)
        {
            case 0:
                return (byte)_chipSelect;
            case 1:
                if (_chipSelect != 1 || !_ready)
                {
                    return 0;
                }

                if (_mode == Mode.Write)
                {
                    return (byte)_mdr;
                }

                if (_mode != Mode.Read)
                {
                    return 0;
                }

                Busy();
                return (byte)ReadRegister(_offset++ & 15);
            case 2:
                return (byte)(_ready ? 0x80 : 0);
            default:
                return openBus;
        }
    }

    public void Write(int register, byte value)
    {
        UpdateReady();
        var data = value & 15;
        switch (register & 3)
        {
            case 0:
                _chipSelect = data & 3;
                if (_chipSelect != 1)
                {
                    _mode = Mode.Mode;
                    _offset = 0;
                    _resync = 0;
                    _pause = 0;
                    _test = 0;
                }

                _ready = true;
                break;
            case 1:
                if (_chipSelect != 1 || !_ready)
                {
                    return;
                }

                switch (_mode)
                {
                    case Mode.Mode:
                        if (data is not (0x03 or 0x0C))
                        {
                            return;
                        }

                        _mode = Mode.Seek;
                        break;
                    case Mode.Seek:
                        _mode = _mdr == 0x03 ? Mode.Write : Mode.Read;
                        _offset = data;
                        break;
                    case Mode.Write:
                        WriteRegister(_offset++ & 15, data);
                        break;
                    default:
                        return;
                }

                _mdr = data;
                Busy();
                break;
        }
    }

    /// <summary>Ready drops for eight chip clocks after each access.</summary>
    private void Busy()
    {
        _ready = false;
        _readyAt = _ticks + 8;
    }

    private void UpdateReady()
    {
        if (!_ready && _readyAt != 0 && _ticks >= _readyAt)
        {
            _ready = true;
            _readyAt = 0;
        }
    }

    private int ReadRegister(int address)
    {
        switch (address)
        {
            case 0: return _secondLo;
            case 1: return _secondHi | _batteryFailure << 3;
            case 2: return _minuteLo;
            case 3: return _minuteHi | _resync << 3;
            case 4: return _hourLo;
            case 5: return _hourHi | _meridian << 2 | _resync << 3;
            case 6: return _dayLo;
            case 7: return _dayHi | _dayRam << 2 | _resync << 3;
            case 8: return _monthLo;
            case 9: return _monthHi | _monthRam << 1 | _resync << 3;
            case 10: return _yearLo;
            case 11: return _yearHi;
            case 12: return _weekday | _resync << 3;
            case 13:
            {
                var flag = _irqFlag & (_irqMask ^ 1);
                _irqFlag = 0;
                return _hold | _calendar << 1 | flag << 2 | _roundSeconds << 3;
            }

            case 14: return _irqMask | _irqDuty << 1 | _irqPeriod << 2;
            default: return _pause | _stop << 1 | _atime << 2 | _test << 3;
        }
    }

    private void WriteRegister(int address, int data)
    {
        switch (address)
        {
            case 0: _secondLo = data; break;
            case 1: _secondHi = data & 7; _batteryFailure = data >> 3; break;
            case 2: _minuteLo = data; break;
            case 3: _minuteHi = data & 7; break;
            case 4: _hourLo = data; break;
            case 5:
                _hourHi = data & 3;
                _meridian = (data >> 2) & 1;
                if (_atime == 1)
                {
                    _meridian = 0;
                }
                else
                {
                    _hourHi &= 1;
                }

                break;
            case 6: _dayLo = data; break;
            case 7: _dayHi = data & 3; _dayRam = (data >> 2) & 1; break;
            case 8: _monthLo = data; break;
            case 9: _monthHi = data & 1; _monthRam = (data >> 1) & 3; break;
            case 10: _yearLo = data; break;
            case 11: _yearHi = data; break;
            case 12: _weekday = data & 7; break;
            case 13:
            {
                var held = _hold;
                _hold = data & 1;
                _calendar = (data >> 1) & 1;
                _roundSeconds = (data >> 3) & 1;
                if (held == 1 && _hold == 0 && _holdTick)
                {
                    // A second that passed while held is counted as the hold ends.
                    _holdTick = false;
                    TickSecond();
                }

                break;
            }

            case 14:
                _irqMask = data & 1;
                _irqDuty = (data >> 1) & 1;
                _irqPeriod = (data >> 2) & 3;
                break;
            default:
                _pause = data & 1;
                _stop = (data >> 1) & 1;
                _atime = (data >> 2) & 1;
                _test = (data >> 3) & 1;
                if (_atime == 1)
                {
                    _meridian = 0;
                }
                else
                {
                    _hourHi &= 1;
                }

                if (_pause != 0)
                {
                    _secondLo = _secondHi = 0;
                }

                break;
        }
    }

    // ---- Time passing ----

    /// <summary>Counts <paramref name="masterClocks"/> more master clocks.</summary>
    public void Advance(long masterClocks)
    {
        if (masterClocks <= 0)
        {
            return;
        }

        _master += masterClocks;
        var target = (long)(_master * (double)ChipHz / _masterClock);

        // Step from one 1/128 s boundary to the next: the duty, the 1/64 s period and the second all fall on them.
        while (true)
        {
            var next = (_ticks | 0x3FFF) + 1;
            if (next > target)
            {
                _ticks = target;
                break;
            }

            _ticks = next;
            var inSecond = next & (ChipHz - 1);
            if (inSecond == 0)
            {
                Round();
            }

            if (_irqDuty != 0)
            {
                _irqFlag = 0;
            }

            if ((inSecond & 0x7FFF) == 0)
            {
                Periodic(0);
            }

            if (inSecond == 0)
            {
                _seconds++;
                Periodic(1);
                if (_seconds % 60 == 0)
                {
                    Periodic(2);
                }

                if (_seconds % 3600 == 0)
                {
                    Periodic(3);
                    _seconds = 0;
                }

                Tick();
            }
        }

        UpdateReady();
    }

    private void Periodic(int period)
    {
        if (_stop == 0 && _pause == 0 && period == _irqPeriod)
        {
            _irqFlag = 1;
        }
    }

    /// <summary>Rounding to the nearest minute (register 13 bit 3), done at the next second.</summary>
    private void Round()
    {
        if (_roundSeconds == 0)
        {
            return;
        }

        _roundSeconds = 0;
        if (_secondHi >= 3)
        {
            TickMinute();
        }

        _secondLo = _secondHi = 0;
    }

    private void Tick()
    {
        if (_stop != 0 || _pause != 0)
        {
            return;
        }

        if (_hold != 0)
        {
            _holdTick = true;
            return;
        }

        _resync = 1;
        TickSecond();
    }

    // The counters as the chip counts them: a digit steps on from 0–8 (and 12), otherwise wraps, so that values
    // that aren't valid BCD behave as they do on the hardware.

    private void TickSecond()
    {
        if (_secondLo <= 8 || _secondLo == 12)
        {
            _secondLo = (_secondLo + 1) & 15;
            return;
        }

        _secondLo = 0;
        if (_secondHi < 5)
        {
            _secondHi++;
            return;
        }

        _secondHi = 0;
        TickMinute();
    }

    private void TickMinute()
    {
        if (_minuteLo <= 8 || _minuteLo == 12)
        {
            _minuteLo = (_minuteLo + 1) & 15;
            return;
        }

        _minuteLo = 0;
        if (_minuteHi < 5)
        {
            _minuteHi++;
            return;
        }

        _minuteHi = 0;
        TickHour();
    }

    private void TickHour()
    {
        if (_atime != 0)
        {
            if (_hourHi < 2)
            {
                if (_hourLo <= 8 || _hourLo == 12)
                {
                    _hourLo = (_hourLo + 1) & 15;
                }
                else
                {
                    _hourLo = (_hourLo & 1) ^ 1;
                    _hourHi = (_hourHi + 1) & 3;
                }
            }
            else if (_hourLo != 3 && (_hourLo & 4) == 0)
            {
                if (_hourLo <= 8 || _hourLo >= 12)
                {
                    _hourLo = (_hourLo + 1) & 15;
                }
                else
                {
                    _hourLo = (_hourLo & 1) ^ 1;
                    _hourHi = (_hourHi + 1) & 3;
                }
            }
            else
            {
                _hourLo = (_hourLo & 1) ^ 1;
                _hourHi = 0;
                TickDay();
            }

            return;
        }

        if (_hourHi == 0)
        {
            if (_hourLo <= 8 || _hourLo == 12)
            {
                _hourLo = (_hourLo + 1) & 15;
            }
            else
            {
                _hourLo = (_hourLo & 1) ^ 1;
                _hourHi ^= 1;
            }

            return;
        }

        if ((_hourLo & 1) != 0)
        {
            _meridian ^= 1;
        }

        if (_hourLo is < 2 or 4 or 5 or 8 or 12)
        {
            _hourLo = (_hourLo + 1) & 15;
        }
        else
        {
            _hourLo = (_hourLo & 1) ^ 1;
            _hourHi ^= 1;
        }

        if (_meridian == 0 && (_hourLo & 1) == 0)
        {
            TickDay();
        }
    }

    // January–September are $01–$09, October–December $10–$12.
    private static readonly int[] DaysInMonth =
    [
        30, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31, 30, 31, 30,
        31, 30, 31, 30, 31, 30, 31, 30, 31, 30, 31, 30, 31, 30, 31, 30,
    ];

    private void TickDay()
    {
        if (_calendar == 0)
        {
            return;
        }

        _weekday = (_weekday + 1 + (_weekday == 6 ? 1 : 0)) & 7;

        var days = DaysInMonth[(_monthHi << 4 | _monthLo) & 31];
        if (days == 28)
        {
            // A leap year: the year (as BCD) divisible by four.
            if ((_yearHi & 1) == 0 && ((_yearLo - 0) & 3) == 0)
            {
                days++;
            }
            else if ((_yearHi & 1) == 1 && ((_yearLo - 2) & 3) == 0)
            {
                days++;
            }
        }

        var monthEnds = days switch
        {
            28 => _dayHi == 3 || (_dayHi == 2 && _dayLo >= 8),
            29 => _dayHi == 3 || (_dayHi == 2 && _dayLo > 8 && _dayLo != 12),
            30 => _dayHi == 3 || (_dayHi == 2 && _dayLo is 10 or 14),
            _ => _dayHi == 3 && (_dayLo & 3) != 0,
        };
        if (monthEnds)
        {
            _dayLo = 1;
            _dayHi = 0;
            TickMonth();
            return;
        }

        if (_dayLo <= 8 || _dayLo == 12)
        {
            _dayLo = (_dayLo + 1) & 15;
        }
        else
        {
            _dayLo = (_dayLo & 1) ^ 1;
            _dayHi = (_dayHi + 1) & 3;
        }
    }

    private void TickMonth()
    {
        if (_monthHi == 0 || (_monthLo & 2) == 0)
        {
            if (_monthLo <= 8 || _monthLo == 12)
            {
                _monthLo = (_monthLo + 1) & 15;
            }
            else
            {
                _monthLo = (_monthLo & 1) ^ 1;
                _monthHi ^= 1;
            }

            return;
        }

        _monthLo = (_monthLo & 1) ^ 1;
        _monthHi = 0;
        TickYear();
    }

    private void TickYear()
    {
        if (_yearLo <= 8 || _yearLo == 12)
        {
            _yearLo = (_yearLo + 1) & 15;
            return;
        }

        _yearLo = (_yearLo & 1) ^ 1;
        if (_yearHi <= 8 || _yearHi == 12)
        {
            _yearHi = (_yearHi + 1) & 15;
        }
        else
        {
            _yearHi = (_yearHi & 1) ^ 1;
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteInt64(_master);
        writer.WriteInt64(_ticks);
        writer.WriteInt64(_readyAt);
        writer.WriteInt32(_seconds);
        writer.WriteInt32(_chipSelect);
        writer.WriteInt32((int)_mode);
        writer.WriteInt32(_mdr);
        writer.WriteInt32(_offset);
        writer.WriteBool(_ready);
        writer.WriteBool(_holdTick);
        foreach (var field in Fields())
        {
            writer.WriteByte((byte)field);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _master = reader.ReadInt64();
        _ticks = reader.ReadInt64();
        _readyAt = reader.ReadInt64();
        _seconds = reader.ReadInt32();
        _chipSelect = reader.ReadInt32() & 3;
        _mode = (Mode)(reader.ReadInt32() & 3);
        _mdr = reader.ReadInt32() & 15;
        _offset = reader.ReadInt32() & 15;
        _ready = reader.ReadBool();
        _holdTick = reader.ReadBool();
        Span<int> fields = stackalloc int[Fields().Length];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = reader.ReadByte() & 15;
        }

        (_secondLo, _secondHi, _batteryFailure, _minuteLo, _minuteHi, _resync, _hourLo, _hourHi, _meridian) =
            (fields[0], fields[1] & 7, fields[2] & 1, fields[3], fields[4] & 7, fields[5] & 1, fields[6], fields[7] & 3, fields[8] & 1);
        (_dayLo, _dayHi, _dayRam, _monthLo, _monthHi, _monthRam, _yearLo, _yearHi, _weekday) =
            (fields[9], fields[10] & 3, fields[11] & 1, fields[12], fields[13] & 1, fields[14] & 3, fields[15], fields[16], fields[17] & 7);
        (_hold, _calendar, _irqFlag, _roundSeconds, _irqMask, _irqDuty, _irqPeriod) =
            (fields[18] & 1, fields[19] & 1, fields[20] & 1, fields[21] & 1, fields[22] & 1, fields[23] & 1, fields[24] & 3);
        (_pause, _stop, _atime, _test) = (fields[25] & 1, fields[26] & 1, fields[27] & 1, fields[28] & 1);
        reader.EndChunk();
    }

    private int[] Fields() =>
    [
        _secondLo, _secondHi, _batteryFailure, _minuteLo, _minuteHi, _resync, _hourLo, _hourHi, _meridian,
        _dayLo, _dayHi, _dayRam, _monthLo, _monthHi, _monthRam, _yearLo, _yearHi, _weekday,
        _hold, _calendar, _irqFlag, _roundSeconds, _irqMask, _irqDuty, _irqPeriod,
        _pause, _stop, _atime, _test,
    ];
}
