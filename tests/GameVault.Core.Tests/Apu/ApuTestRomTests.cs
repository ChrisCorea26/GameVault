using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Apu;

/// <summary>APU and interrupt-timing suites (SPEC §4.4), all NROM.</summary>
public class ApuTestRomTests
{
    public static TheoryData<string> ApuTest { get; } = new(
        "1-len_ctr", "2-len_table", "3-irq_flag", "4-jitter", "5-len_timing", "6-irq_flag_timing", "7-dmc_basics", "8-dmc_rates");

    public static TheoryData<string> Apu2005 { get; } = new(
        "01.len_ctr", "02.len_table", "03.irq_flag", "04.clock_jitter", "05.len_timing_mode0", "06.len_timing_mode1",
        "07.irq_flag_timing", "08.irq_timing", "09.reset_timing", "10.len_halt_timing", "11.len_reload_timing");

    public static TheoryData<string> ApuReset { get; } = new(
        "4015_cleared", "4017_timing", "4017_written", "irq_flag_cleared", "len_ctrs_enabled", "works_immediately");

    public static TheoryData<string> CpuInterrupts { get; } = new(
        "1-cli_latency", "2-nmi_and_brk", "3-nmi_and_irq", "4-irq_and_dma", "5-branch_delays_irq");

    public static TheoryData<string> InstrTiming { get; } = new("1-instr_timing", "2-branch_timing");

    public static TheoryData<string> DmcTests { get; } = new("buffer_retained", "latency", "status", "status_irq");

    [TestRomTheory("apu_test/1-len_ctr.nes")]
    [MemberData(nameof(ApuTest))]
    public void Apu_test(string name) => AssertPasses($"apu_test/{name}.nes");

    [TestRomTheory("apu_reset/4015_cleared.nes")]
    [MemberData(nameof(ApuReset))]
    public void Apu_reset(string name) => AssertPasses($"apu_reset/{name}.nes");

    [TestRomTheory("cpu_interrupts_v2/1-cli_latency.nes")]
    [MemberData(nameof(CpuInterrupts))]
    public void Cpu_interrupts(string name) => AssertPasses($"cpu_interrupts_v2/{name}.nes");

    /// <summary>Deferred from Phase 1: these time instructions with the APU length counter.</summary>
    [TestRomTheory("instr_timing/1-instr_timing.nes")]
    [MemberData(nameof(InstrTiming))]
    public void Instr_timing(string name) => AssertPasses($"instr_timing/{name}.nes", maxFrames: 60 * 60 * 2);

    [TestRomTheory("blargg_apu_2005.07.30/01.len_ctr.nes")]
    [MemberData(nameof(Apu2005))]
    public void Blargg_apu_2005(string name)
    {
        var result = TestRomRunner.RunWithZeroPageResult(
            TestRomLocator.ReadCached($"blargg_apu_2005.07.30/{name}.nes"), 0x00F0, out var console);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
        GoldenImages.AssertMatches($"blargg_apu/{name}", console.Ppu.FrameBuffer);
    }

    /// <summary>
    /// The dmc_tests are listening tests: they report through DAC clicks and a final tone, which software
    /// can't judge. We check that each runs to its final "JMP *" loop instead of hanging (for example on
    /// a $4015 wait). Their behavior is checked by apu_test 7-dmc_basics and 8-dmc_rates.
    /// </summary>
    [TestRomTheory("dmc_tests/status.nes")]
    [MemberData(nameof(DmcTests))]
    public void Dmc_tests_run_to_completion(string name)
    {
        var console = NesConsole.FromBytes(TestRomLocator.ReadCached($"dmc_tests/{name}.nes"));
        console.Power();

        var finished = false;
        for (var frame = 0; frame < 600 && !finished; frame++)
        {
            console.RunFrame();
            var pc = console.Cpu.PC;
            finished = console.Bus.Peek(pc) == 0x4C
                && console.Bus.Peek((ushort)(pc + 1)) == (byte)pc
                && console.Bus.Peek((ushort)(pc + 2)) == (byte)(pc >> 8);
        }

        finished.Should().BeTrue("the ROM should reach its final JMP-to-self loop");
    }

    private static void AssertPasses(string path, int maxFrames = 60 * 60)
    {
        var result = TestRomRunner.Run(TestRomLocator.ReadCached(path), maxFrames);

        result.Status.Should().Be(TestRomStatus.Passed, result.ToString());
    }
}
