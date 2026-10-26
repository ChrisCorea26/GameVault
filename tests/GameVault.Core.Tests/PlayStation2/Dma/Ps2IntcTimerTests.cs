using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Dma;

/// <summary>The EE's INTC and timers (ps2tek: "EE Interrupt Controller (INTC)", "EE Timers").</summary>
public sealed class Ps2IntcTimerTests
{
    private const uint T0Count = 0x10000000;
    private const uint T0Mode = 0x10000010;
    private const uint T0Comp = 0x10000020;
    private const uint T0Hold = 0x10000030;
    private const uint T1 = 0x800;
    private const uint T3 = 0x1800;

    private const uint Enable = 0x80;
    private const uint CompareIrq = 0x100;
    private const uint OverflowIrq = 0x200;
    private const uint CompareFlag = 0x400;
    private const uint OverflowFlag = 0x800;
    private const uint ZeroReturn = 0x40;

    // ---- INTC ----

    [Fact]
    public void IntcStatIsClearedByWritingOnesAndMaskTogglesOnWrite()
    {
        var intc = new Ps2Intc();
        intc.Raise(Ps2Interrupt.VBlankStart);
        intc.Raise(Ps2Interrupt.Timer2);
        intc.Read(Ps2Intc.StatAddress).Should().Be((1u << 2) | (1u << 11));
        intc.Int0.Should().BeFalse("nothing is unmasked");

        intc.Write(Ps2Intc.MaskAddress, 1u << 11);
        intc.Int0.Should().BeTrue();
        intc.Write(Ps2Intc.MaskAddress, (1u << 11) | (1u << 3));
        intc.Read(Ps2Intc.MaskAddress).Should().Be(1u << 3, "writing 1 reverses a mask bit");
        intc.Int0.Should().BeFalse();

        intc.Write(Ps2Intc.StatAddress, 1u << 2);
        intc.Stat.Should().Be(1u << 11, "writing 1 acknowledges a source; 0 has no effect");
        intc.Write(Ps2Intc.StatAddress, 0);
        intc.Stat.Should().Be(1u << 11);
    }

    [Fact]
    public void IntcRoundTripsThroughASaveState()
    {
        var intc = new Ps2Intc();
        intc.Raise(Ps2Interrupt.Gs);
        intc.Raise(Ps2Interrupt.Vif1);
        intc.Write(Ps2Intc.MaskAddress, 0x21);
        var writer = new StateWriter();
        intc.SaveState(writer);

        var copy = new Ps2Intc();
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.Stat.Should().Be(intc.Stat);
        copy.Mask.Should().Be(intc.Mask);
        copy.Int0.Should().BeTrue();
    }

    // ---- Timers ----

    private static (Ps2EeTimers Timers, Ps2Intc Intc) NewTimers()
    {
        var intc = new Ps2Intc();
        return (new Ps2EeTimers(intc), intc);
    }

    [Theory]
    [InlineData(0u, 100L, 100u)]
    [InlineData(1u, 40L, 2u)]
    [InlineData(2u, 1000L, 3u)]
    public void TimersCountTheBusClockAndItsDividers(uint clock, long cycles, uint expected)
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | clock);
        timers.Advance(cycles);
        timers.Read(T0Count).Should().Be(expected);
    }

    [Fact]
    public void TheDividersCarryTheirRemainderAcrossAdvances()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | 1);
        for (var i = 0; i < 16; i++)
        {
            timers.Advance(3);
        }

        timers.Read(T0Count).Should().Be(3, "48 bus cycles are three ticks of bus/16");
    }

    [Fact]
    public void ADisabledTimerDoesNotCount()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, 0);
        timers.Advance(1000);
        timers.Read(T0Count).Should().Be(0);
    }

    [Fact]
    public void ReachingTheCompareValueSetsTheFlagAndRaisesTheTimersInterrupt()
    {
        var (timers, intc) = NewTimers();
        timers.Write(T0Comp + T1, 10);
        timers.Write(T0Mode + T1, Enable | CompareIrq);
        timers.Advance(9);
        intc.Stat.Should().Be(0);
        timers.Advance(1);
        timers.Read(T0Mode + T1).Should().Be(Enable | CompareIrq | CompareFlag);
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Timer1);
        timers.Read(T0Count + T1).Should().Be(10, "without ZRET the count goes on");

        // Writing the flag back clears it.
        timers.Write(T0Mode + T1, Enable | CompareIrq | CompareFlag);
        timers.Read(T0Mode + T1).Should().Be(Enable | CompareIrq);
    }

    [Fact]
    public void InterruptsAreEdgeTriggeredOnTheFlags()
    {
        var (timers, intc) = NewTimers();
        timers.Write(T0Comp, 10);
        timers.Write(T0Mode, Enable | CompareIrq | ZeroReturn);
        timers.Advance(10);
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Timer0);
        intc.Write(Ps2Intc.StatAddress, 0xFFFF);

        timers.Advance(10);
        intc.Stat.Should().Be(0, "the flag was still set, so it didn't go from 0 to 1 (ps2tek)");

        timers.Write(T0Mode, Enable | CompareIrq | ZeroReturn | CompareFlag);
        timers.Advance(10);
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Timer0);
    }

    [Fact]
    public void ZeroReturnClearsTheCounterOnReachingTheCompareValue()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Comp, 10);
        timers.Write(T0Mode, Enable | ZeroReturn);
        timers.Advance(25);
        timers.Read(T0Count).Should().Be(5);
        timers.Read(T0Mode).Should().Be(Enable | ZeroReturn | CompareFlag);

        timers.Advance(1_000_003);
        timers.Read(T0Count).Should().Be((5 + 1_000_003) % 10);
    }

    [Fact]
    public void OverflowWrapsTheCounterAndRaisesItsInterrupt()
    {
        var (timers, intc) = NewTimers();
        timers.Write(T0Count + T3, 0xFFF0);
        timers.Write(T0Comp + T3, 0x8000);
        timers.Write(T0Mode + T3, Enable | OverflowIrq);
        timers.Advance(0x20);
        timers.Read(T0Count + T3).Should().Be(0x10);
        timers.Read(T0Mode + T3).Should().Be(Enable | OverflowIrq | OverflowFlag);
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Timer3);
    }

    [Fact]
    public void LongAdvancesStillEndOnTheRightCount()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Comp, 0x1234);
        timers.Write(T0Mode, Enable);
        timers.Advance(10_000_000);
        timers.Read(T0Count).Should().Be((uint)(10_000_000 % 0x10000));
        timers.Read(T0Mode).Should().Be(Enable | CompareFlag | OverflowFlag);
    }

    [Fact]
    public void TheHBlankClockCountsHBlanks()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | 3);
        timers.Advance(100_000);
        timers.HBlankStart();
        timers.HBlankEnd();
        timers.HBlankStart();
        timers.HBlankEnd();
        timers.HBlankStart();
        timers.Read(T0Count).Should().Be(3);
    }

    [Fact]
    public void GateModeZeroCountsOnlyOutsideTheBlank()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | 0x4 | 0x8); // gate on V-blank, mode 0
        timers.VBlankStart();
        timers.Advance(100);
        timers.Read(T0Count).Should().Be(0);
        timers.VBlankEnd();
        timers.Advance(5);
        timers.Read(T0Count).Should().Be(5);
    }

    [Theory]
    [InlineData(1u, true, false)]
    [InlineData(2u, false, true)]
    [InlineData(3u, true, true)]
    public void GateModesResetTheCounterOnTheBlanksEdges(uint gateMode, bool resetAtStart, bool resetAtEnd)
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | 0x4 | (gateMode << 4)); // gate on H-blank
        timers.Advance(50);
        timers.HBlankStart();
        timers.Read(T0Count).Should().Be(resetAtStart ? 0u : 50u);
        timers.Advance(7);
        timers.HBlankEnd();
        timers.Read(T0Count).Should().Be(resetAtEnd ? 0u : resetAtStart ? 7u : 57u);
    }

    [Fact]
    public void TheHBlankGateIsIgnoredWhenTheTimerCountsHBlanks()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable | 3 | 0x4); // H-blank clock, H-blank gate, mode 0
        timers.HBlankStart();
        timers.HBlankEnd();
        timers.HBlankStart();
        timers.Read(T0Count).Should().Be(2);
    }

    [Fact]
    public void CyclesUntilNextEventPointsAtTheNextEnabledInterrupt()
    {
        var (timers, _) = NewTimers();
        timers.CyclesUntilNextEvent().Should().Be(long.MaxValue);
        timers.Write(T0Comp, 100);
        timers.Write(T0Mode, Enable | 1 | CompareIrq);
        timers.CyclesUntilNextEvent().Should().Be(1600);
        timers.Advance(15);
        timers.CyclesUntilNextEvent().Should().Be(1585);

        timers.Write(T0Count + T1, 0xFFFE);
        timers.Write(T0Mode + T1, Enable | OverflowIrq);
        timers.CyclesUntilNextEvent().Should().Be(2);
        timers.Advance(2);
        timers.CyclesUntilNextEvent().Should().Be(1583, "timer 1's flag is set, so it can't interrupt again");
    }

    [Fact]
    public void HoldExistsOnlyOnT0AndT1()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Mode, Enable);
        timers.Write(T0Mode + T1, Enable);
        timers.Advance(77);
        timers.LatchHold();
        timers.Read(T0Hold).Should().Be(77);
        timers.Read(T0Hold + T1).Should().Be(77);
        timers.Write(T0Hold + 0x1000, 5);
        timers.Read(T0Hold + 0x1000).Should().Be(0);
    }

    [Fact]
    public void TimersRoundTripThroughASaveState()
    {
        var (timers, _) = NewTimers();
        timers.Write(T0Comp, 300);
        timers.Write(T0Mode, Enable | 2 | CompareIrq);
        timers.Write(T0Mode + T1, Enable | 0x4 | 0x8);
        timers.VBlankStart();
        timers.Advance(1000);
        var writer = new StateWriter();
        timers.SaveState(writer);

        var (copy, copyIntc) = NewTimers();
        copy.LoadState(new StateReader(writer.ToArray()));
        foreach (var t in new[] { timers, copy })
        {
            t.Advance(300 * 256);
        }

        copy.Read(T0Count).Should().Be(timers.Read(T0Count));
        copy.Read(T0Mode).Should().Be(timers.Read(T0Mode));
        copy.Read(T0Count + T1).Should().Be(0, "the V-blank gate held through the restore");
        copyIntc.Stat.Should().Be(1u << (int)Ps2Interrupt.Timer0);
    }
}
