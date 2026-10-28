using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>The Sharp S-RTC (snes-chips-3.md §1).</summary>
public sealed class SharpRtcTests
{
    private const double Clock = SnesConsole.NtscMasterClock;

    private static SharpRtc Rtc(DateTime time)
    {
        var rtc = new SharpRtc(Clock);
        rtc.Power();
        rtc.SetTime(time);
        return rtc;
    }

    /// <summary>A read as the game does it: $0D, then 15, the 13 digits and 15 again from $2800.</summary>
    private static int[] ReadDigits(SharpRtc rtc)
    {
        rtc.Write(0x002801, 0x0D);
        rtc.Read(0x002800, 0).Should().Be(15, "a read starts with 15");
        var digits = Enumerable.Range(0, 13).Select(_ => (int)rtc.Read(0x002800, 0)).ToArray();
        rtc.Read(0x002800, 0).Should().Be(15, "and ends with 15");
        return digits;
    }

    [Fact]
    public void A_game_reads_the_time_as_digits()
    {
        var rtc = Rtc(new DateTime(2026, 10, 28, 13, 45, 59)); // a Wednesday

        ReadDigits(rtc).Should().Equal(9, 5, 5, 4, 3, 1, 8, 2, 10, 6, 2, 10, 3);
    }

    [Fact]
    public void A_game_sets_the_time_and_the_weekday_is_worked_out()
    {
        var rtc = Rtc(new DateTime(2026, 10, 28));

        rtc.Write(0x802801, 0x0E); // command, in the $80 mirror
        rtc.Write(0x802801, 0x00); // set the time
        foreach (var digit in new[] { 0, 3, 0, 2, 1, 1, 5, 2, 7, 9, 9, 9 }) // 11:20:30, 25 July (1)999
        {
            rtc.Write(0x802801, (byte)digit);
        }

        rtc.Time.Should().Be((1999, 7, 25, 11, 20, 30, 0), "25 July 1999 was a Sunday");
        ReadDigits(rtc)[12].Should().Be(0);
    }

    [Fact]
    public void The_reset_command_clears_the_time()
    {
        var rtc = Rtc(new DateTime(2026, 10, 28));

        rtc.Write(0x002801, 0x0E);
        rtc.Write(0x002801, 0x04);

        rtc.Time.Should().Be((1000, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Seconds_pass_with_the_master_clock()
    {
        var rtc = Rtc(new DateTime(2026, 10, 28, 23, 59, 0));
        var frame = (long)(Clock / 60);
        for (var i = 0; i <= 60 * 61; i++) // a frame over 61 seconds
        {
            rtc.CatchUp(frame);
            rtc.EndFrame(frame);
        }

        rtc.Time.Should().Be((2026, 10, 29, 0, 0, 1, 4), "61 seconds later it's past midnight, Thursday");
    }

    [Theory]
    [InlineData(2028, 2, 28, 2028, 2, 29)] // a leap year
    [InlineData(2026, 2, 28, 2026, 3, 1)]
    [InlineData(2000, 2, 28, 2000, 2, 29)] // divisible by 400
    [InlineData(2026, 12, 31, 2027, 1, 1)]
    public void Days_roll_over_into_the_next_month_and_year(int year, int month, int day, int toYear, int toMonth, int toDay)
    {
        var rtc = Rtc(new DateTime(year, month, day, 23, 59, 59));

        rtc.CatchUp((long)Clock);

        var time = rtc.Time;
        (time.Year, time.Month, time.Day, time.Hour).Should().Be((toYear, toMonth, toDay, 0));
    }

    [Fact]
    public void The_cartridge_finds_the_chip_and_states_keep_the_time()
    {
        var cartridge = new SnesCartridge(SnesTests.Rom([0x80, 0xFE], hiRom: true, chip: 0x55, ramSize: 3));
        var rtc = cartridge.Coprocessor.Should().BeOfType<SharpRtc>().Subject;
        cartridge.HasBattery.Should().BeTrue();
        rtc.Claims(0x002800).Should().BeTrue();
        rtc.Claims(0xBF2801).Should().BeTrue();
        rtc.Claims(0x402800).Should().BeFalse();
        rtc.Time.Should().Be((2000, 1, 1, 0, 0, 0, 6), "a fixed start until the host sets it");

        rtc.SetTime(new DateTime(2026, 10, 28, 8, 0, 0));
        var writer = new StateWriter();
        rtc.SaveState(writer);
        rtc.SetTime(new DateTime(2001, 1, 1));
        rtc.LoadState(new StateReader(writer.ToArray()));

        rtc.Time.Should().Be((2026, 10, 28, 8, 0, 0, 3));
    }
}
