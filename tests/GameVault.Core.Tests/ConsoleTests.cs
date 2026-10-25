using GameVault.Core.State;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests;

public class ConsoleTests
{
    // Enables NMI, then counts frames in $00 from the NMI handler while the main loop counts in $01.
    private static readonly byte[] FrameCounterProgram =
    [
        0xA9, 0x80,       // C000: LDA #$80
        0x8D, 0x00, 0x20, // C002: STA $2000   (enable NMI)
        0xE6, 0x01,       // C005: INC $01
        0x4C, 0x05, 0xC0, // C007: JMP $C005
        0xE6, 0x00,       // C00A: INC $00     (NMI handler)
        0x40,             // C00C: RTI
    ];

    private static NesConsole Boot()
    {
        var console = NesConsole.FromBytes(TestRoms.Nrom(FrameCounterProgram, nmi: 0xC00A));
        console.Power();
        return console;
    }

    [Fact]
    public void Vblank_nmi_fires_once_per_frame()
    {
        var console = Boot();

        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        console.Bus.Peek(0x0000).Should().Be(10);
    }

    [Fact]
    public void Ntsc_frame_is_about_29780_cpu_cycles()
    {
        var console = Boot();
        console.RunFrame();
        var start = console.Cpu.CycleCount;

        console.RunFrame();

        // 341 * 262 / 3 = 29780.67 cycles; RunFrame stops at an instruction boundary.
        (console.Cpu.CycleCount - start).Should().BeInRange(29775, 29787);
    }

    [Fact]
    public void Same_inputs_produce_identical_state()
    {
        var a = Boot();
        var b = Boot();

        for (var i = 0; i < 5; i++)
        {
            a.RunFrame();
            b.RunFrame();
        }

        StateAssert.Save(a).Should().Equal(StateAssert.Save(b));
    }

    [Fact]
    public void Save_load_then_run_matches_an_uninterrupted_run()
    {
        var uninterrupted = Boot();
        var restored = Boot();
        for (var i = 0; i < 3; i++)
        {
            uninterrupted.RunFrame();
            restored.RunFrame();
        }

        var snapshot = StateAssert.Save(restored);
        restored.RunFrame();                    // diverge...
        restored.Bus.Write(0x0001, 0xFF);
        restored.LoadState(new StateReader(snapshot)); // ...then rewind

        for (var i = 0; i < 5; i++)
        {
            uninterrupted.RunFrame();
            restored.RunFrame();
        }

        StateAssert.Save(restored).Should().Equal(StateAssert.Save(uninterrupted));
    }

    [Fact]
    public void Console_state_round_trips()
    {
        StateAssert.RoundTrips(Boot, c =>
        {
            c.RunFrame();
            c.RunFrame();
        });
    }

    [Fact]
    public void Save_and_load_take_well_under_a_millisecond()
    {
        var console = Boot();
        console.RunFrame();
        var writer = new StateWriter();
        console.SaveState(writer); // warm up
        var data = writer.ToArray();
        console.LoadState(new StateReader(data));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 200;
        for (var i = 0; i < iterations; i++)
        {
            writer.Reset();
            console.SaveState(writer);
            console.LoadState(new StateReader(data));
        }

        var perRoundTrip = watch.Elapsed.TotalMilliseconds / iterations;
        perRoundTrip.Should().BeLessThan(1.0, "run-ahead and rewind need save+load under 1 ms (SPEC §4.6)");
    }
}
