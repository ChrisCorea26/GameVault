using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>The Epson RTC-4513 on Far East of Eden Zero's SPC7110 board (snes-chips-3.md §2).</summary>
public sealed class EpsonRtcTests
{
    private const double Clock = SnesConsole.NtscMasterClock;

    private readonly EpsonRtc _rtc = new(Clock);

    public EpsonRtcTests() => _rtc.Power();

    /// <summary>Waits out the chip's busy time (eight of its 2 MHz clocks), as a game polls $4842.</summary>
    private void Wait()
    {
        _rtc.Advance(100);
        (_rtc.Read(2, 0) & 0x80).Should().Be(0x80, "ready again");
    }

    private void Select() => _rtc.Write(0, 1);

    private int[] ReadAll()
    {
        Select();
        _rtc.Write(1, 0x0C); // read
        Wait();
        _rtc.Write(1, 0x00); // from register 0
        Wait();
        var registers = new int[16];
        for (var i = 0; i < 16; i++)
        {
            registers[i] = _rtc.Read(1, 0);
            Wait();
        }

        _rtc.Write(0, 0); // deselect
        return registers;
    }

    private void WriteAll(int first, params int[] values)
    {
        Select();
        _rtc.Write(1, 0x03); // write
        Wait();
        _rtc.Write(1, (byte)first);
        Wait();
        foreach (var value in values)
        {
            _rtc.Write(1, (byte)value);
            Wait();
        }

        _rtc.Write(0, 0);
    }

    [Fact]
    public void A_game_reads_the_time_in_bcd_with_its_flags()
    {
        _rtc.SetTime(new DateTime(2026, 10, 28, 13, 45, 59)); // a Wednesday, 1:45:59 PM

        var r = ReadAll();

        // The BCD digits, PM in register 5, and the resync flag (bit 3) saying the time was just set.
        r[..13].Should().Equal(9, 5, 5, 4 | 8, 1, 0 | 4 | 8, 8, 2 | 8, 0, 1 | 8, 6, 2, 3 | 8);
    }

    [Fact]
    public void Each_access_makes_the_chip_busy_for_a_moment()
    {
        Select();
        (_rtc.Read(2, 0) & 0x80).Should().Be(0x80);

        _rtc.Write(1, 0x0C);
        (_rtc.Read(2, 0) & 0x80).Should().Be(0, "busy straight after a write");
        _rtc.Write(1, 0x05);
        _rtc.Read(1, 0).Should().Be(0, "and accesses while busy are ignored");
        _rtc.Advance(100);
        (_rtc.Read(2, 0) & 0x80).Should().Be(0x80);
    }

    [Fact]
    public void A_game_sets_24_hour_mode_and_the_time_and_seconds_count_on()
    {
        WriteAll(15, 0x04); // register 15: 24-hour mode
        WriteAll(0, 9, 5, 9, 5, 3, 2, 1, 3, 2, 1, 9, 9, 5); // 23:59:59, 31/12/99, Friday

        _rtc.Advance((long)Clock); // one second

        _rtc.Time.Should().Be((0, 1, 1, 0, 0, 0, 6), "midnight on the 1st of January, a Saturday");
    }

    [Fact]
    public void The_12_hour_clock_turns_pm_at_noon()
    {
        _rtc.SetTime(new DateTime(2026, 10, 28, 11, 59, 59));

        _rtc.Advance((long)Clock);

        _rtc.Time.Hour.Should().Be(12);
        (ReadAll()[5] & 4).Should().Be(4, "PM");
    }

    [Fact]
    public void A_leap_day_comes_in_february_of_a_leap_year()
    {
        WriteAll(15, 0x04);
        WriteAll(0, 9, 5, 9, 5, 3, 2, 8, 2, 2, 0, 8, 2, 1); // 23:59:59, 28/02/28

        _rtc.Advance((long)Clock);

        _rtc.Time.Should().Be((28, 2, 29, 0, 0, 0, 2));
    }

    [Fact]
    public void Rounding_goes_to_the_nearest_minute_at_the_next_second()
    {
        _rtc.SetTime(new DateTime(2026, 10, 28, 10, 20, 44));
        WriteAll(13, 0x02 | 0x08); // calendar on, round

        _rtc.Advance((long)Clock);

        var time = _rtc.Time;
        (time.Minute, time.Second).Should().Be((21, 1), "44 seconds rounds up, then the second ticks");
    }

    [Fact]
    public void The_periodic_flag_comes_every_second_when_chosen()
    {
        WriteAll(14, 1 << 2); // period 1: every second, flag readable (mask clear)
        _rtc.Advance((long)(Clock * 1.2));
        (ReadAll()[13] & 4).Should().Be(4, "a second boundary passed");
        (ReadAll()[13] & 4).Should().Be(0, "reading cleared it");
    }

    [Fact]
    public void Pausing_stops_the_clock_and_clears_the_seconds()
    {
        _rtc.SetTime(new DateTime(2026, 10, 28, 10, 20, 30));
        Select();
        _rtc.Write(1, 0x03);
        Wait();
        _rtc.Write(1, 15);
        Wait();
        _rtc.Write(1, 0x01); // pause, staying selected (deselecting the chip resets it, which ends the pause)

        _rtc.Advance((long)Clock * 3);

        var time = _rtc.Time;
        (time.Minute, time.Second).Should().Be((20, 0));

        _rtc.Write(0, 0);
        _rtc.Advance((long)Clock);
        _rtc.Time.Second.Should().Be(1, "running again once deselected");
    }

    [Fact]
    public void A_save_state_keeps_the_time_and_the_protocol()
    {
        _rtc.SetTime(new DateTime(2026, 10, 28, 9, 0, 0));
        _rtc.Advance((long)(Clock * 2.5));
        Select();
        _rtc.Write(1, 0x0C);

        var writer = new StateWriter();
        _rtc.SaveState(writer);
        var copy = new EpsonRtc(Clock);
        copy.LoadState(new StateReader(writer.ToArray()));
        _rtc.Advance((long)Clock);
        copy.Advance((long)Clock);

        copy.Time.Should().Be(_rtc.Time);
        copy.Read(2, 0).Should().Be(_rtc.Read(2, 0));
    }

    [Fact]
    public void Far_east_of_eden_zeros_board_has_the_clock_at_4840()
    {
        var image = new byte[0x110000];
        var header = SnesTests.Rom([0x80, 0xFE], hiRom: true, chip: 0xF9, ramSize: 3);
        header.CopyTo(image, 0);
        var cartridge = new SnesCartridge(image);
        var chip = cartridge.Coprocessor.Should().BeOfType<Spc7110>().Subject;
        chip.Power();
        chip.Rtc.Should().NotBeNull();
        cartridge.HasBattery.Should().BeTrue();

        chip.SetTime(new DateTime(2026, 10, 28, 8, 0, 0));
        chip.Write(0x004840, 1);
        chip.Read(0x004842, 0).Should().Be(0x80);
        chip.Read(0x804840, 0).Should().Be(1, "the chip select reads back, in the $80 mirror too");
        chip.Rtc!.Time.Hour.Should().Be(8);
    }
}
