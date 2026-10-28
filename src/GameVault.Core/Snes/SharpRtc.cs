using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>
/// The Sharp S-RTC (snes-chips-3.md §1), Daikaijuu Monogatari II's clock: $2800 reads and $2801 writes, four bits at a
/// time. $0D starts a read (15, the 13 digits, 15 again); $0E starts a command: $00 then 12 digits sets the time
/// (the weekday is worked out from the date), $04 clears it. The digits are the seconds, minutes, hours and day (ones
/// then tens), the month, the year's three digits counted from 1000, and the weekday (0 is Sunday).
/// </summary>
/// <remarks>Behaviour follows ares' sharprtc and fullsnes ("SNES Cart S-RTC").</remarks>
public sealed class SharpRtc : ISnesCoprocessor, ISnesRealTimeClock
{
    private static readonly StateTag Tag = StateTag.From("SRTC");
    private static readonly int[] DaysInMonth = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    private enum Mode
    {
        Ready,
        Command,
        Read,
        Write,
    }

    private readonly long _clocksPerSecond;
    private Mode _mode;
    private int _index;
    private int _second, _minute, _hour, _day, _month, _year, _weekday;
    private long _clock;     // master clocks since the frame started, as far as the clock has counted
    private long _elapsed;   // master clocks towards the next second

    /// <param name="masterClock">The console's master clock (NTSC or PAL), which the clock's seconds are counted in.</param>
    public SharpRtc(double masterClock)
    {
        _clocksPerSecond = (long)Math.Round(masterClock);
        SetTime(new DateTime(2000, 1, 1));
    }

    public string Name => "S-RTC";

    public bool Irq => false;

    /// <summary>The time the clock holds now (for tests and the debugger).</summary>
    public (int Year, int Month, int Day, int Hour, int Minute, int Second, int Weekday) Time =>
        (1000 + _year, _month, _day, _hour, _minute, _second, _weekday);

    public void SetTime(DateTime time)
    {
        _second = Math.Min(59, time.Second);
        _minute = time.Minute;
        _hour = time.Hour;
        _day = time.Day;
        _month = time.Month;
        _year = (time.Year - 1000) & 0xFFF;
        _weekday = (int)time.DayOfWeek;
    }

    public bool Claims(uint address) => (address & 0x40FFFE) == 0x2800;

    public byte Read(uint address, byte openBus)
    {
        if ((address & 1) != 0)
        {
            return openBus;
        }

        if (_mode != Mode.Read)
        {
            return 0;
        }

        if (_index < 0)
        {
            _index++;
            return 15;
        }

        if (_index > 12)
        {
            _index = -1;
            return 15;
        }

        return (byte)Digit(_index++);
    }

    public void Write(uint address, byte value)
    {
        if ((address & 1) == 0)
        {
            return;
        }

        var data = value & 0x0F;
        switch (data)
        {
            case 0x0D:
                _mode = Mode.Read;
                _index = -1;
                return;
            case 0x0E:
                _mode = Mode.Command;
                return;
            case 0x0F:
                return;
        }

        if (_mode == Mode.Command)
        {
            if (data == 0)
            {
                _mode = Mode.Write;
                _index = 0;
            }
            else
            {
                _mode = Mode.Ready;
                if (data == 4)
                {
                    _index = -1;
                    _second = _minute = _hour = _day = _month = _year = _weekday = 0;
                }
            }

            return;
        }

        if (_mode == Mode.Write && _index is >= 0 and < 12)
        {
            SetDigit(_index++, data);
            if (_index == 12)
            {
                _weekday = Weekday(1000 + _year, _month, _day);
            }
        }
    }

    private int Digit(int index) => index switch
    {
        0 => _second % 10,
        1 => _second / 10,
        2 => _minute % 10,
        3 => _minute / 10,
        4 => _hour % 10,
        5 => _hour / 10,
        6 => _day % 10,
        7 => _day / 10,
        8 => _month,
        9 => _year % 10,
        10 => _year / 10 % 10,
        11 => _year / 100,
        12 => _weekday,
        _ => 0,
    };

    private void SetDigit(int index, int data)
    {
        switch (index)
        {
            case 0: _second = _second / 10 * 10 + data; break;
            case 1: _second = data * 10 + _second % 10; break;
            case 2: _minute = _minute / 10 * 10 + data; break;
            case 3: _minute = data * 10 + _minute % 10; break;
            case 4: _hour = _hour / 10 * 10 + data; break;
            case 5: _hour = data * 10 + _hour % 10; break;
            case 6: _day = _day / 10 * 10 + data; break;
            case 7: _day = data * 10 + _day % 10; break;
            case 8: _month = data; break;
            case 9: _year = _year / 10 * 10 + data; break;
            case 10: _year = _year / 100 * 100 + data * 10 + _year % 10; break;
            case 11: _year = data * 100 + _year % 100; break;
        }
    }

    /// <summary>The weekday (0 is Sunday) of a date, counted from 1 January 1000, a Wednesday.</summary>
    internal static int Weekday(int year, int month, int day)
    {
        year = Math.Max(1000, year);
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, 31);
        long days = 0;
        for (var y = 1000; y < year; y++)
        {
            days += IsLeap(y) ? 366 : 365;
        }

        for (var m = 1; m < month; m++)
        {
            days += DaysInMonth[m - 1] + (m == 2 && IsLeap(year) ? 1 : 0);
        }

        days += day - 1;
        return (int)((days + 3) % 7);
    }

    private static bool IsLeap(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    // ---- Time passing ----

    public void CatchUp(long clock)
    {
        if (clock <= _clock)
        {
            return;
        }

        _elapsed += clock - _clock;
        _clock = clock;
        while (_elapsed >= _clocksPerSecond)
        {
            _elapsed -= _clocksPerSecond;
            TickSecond();
        }
    }

    public void EndFrame(long frameClocks) => _clock -= frameClocks;

    private void TickSecond()
    {
        if (++_second < 60)
        {
            return;
        }

        _second = 0;
        if (++_minute < 60)
        {
            return;
        }

        _minute = 0;
        if (++_hour < 24)
        {
            return;
        }

        _hour = 0;
        TickDay();
    }

    private void TickDay()
    {
        // The weekday counts on with the day (ares leaves it until the next time the game sets the clock).
        _weekday = (_weekday + 1) % 7;
        var days = DaysInMonth[(_month + 11) % 12] + (_month == 2 && IsLeap(1000 + _year) ? 1 : 0);
        if (_day++ < days)
        {
            return;
        }

        _day = 1;
        if (_month++ < 12)
        {
            return;
        }

        _month = 1;
        _year = (_year + 1) & 0xFFF;
    }

    public void Power()
    {
        Reset();
        _clock = 0;
        _elapsed = 0;
    }

    public void Reset()
    {
        _mode = Mode.Read;
        _index = -1;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteInt32((int)_mode);
        writer.WriteInt32(_index);
        writer.WriteInt32(_second);
        writer.WriteInt32(_minute);
        writer.WriteInt32(_hour);
        writer.WriteInt32(_day);
        writer.WriteInt32(_month);
        writer.WriteInt32(_year);
        writer.WriteInt32(_weekday);
        writer.WriteInt64(_clock);
        writer.WriteInt64(_elapsed);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _mode = (Mode)(reader.ReadInt32() & 3);
        _index = Math.Clamp(reader.ReadInt32(), -1, 13);
        _second = reader.ReadInt32() % 100;
        _minute = reader.ReadInt32() % 100;
        _hour = reader.ReadInt32() % 100;
        _day = reader.ReadInt32() % 100;
        _month = reader.ReadInt32() & 0x0F;
        _year = reader.ReadInt32() & 0xFFF;
        _weekday = reader.ReadInt32() & 0x0F;
        _clock = reader.ReadInt64();
        _elapsed = Math.Clamp(reader.ReadInt64(), 0, _clocksPerSecond);
        reader.EndChunk();
    }
}
