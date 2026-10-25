using GameVault.Core.Cpu;

namespace GameVault.Core.Tests.Cpu;

/// <summary>Interrupt timing per NESdev Wiki "CPU interrupts".</summary>
public class CpuInterruptTests : CpuTestBase
{
    private const ushort NmiHandler = 0x9000;
    private const ushort IrqHandler = 0xA000;

    private void BootWithVectors(params byte[] program)
    {
        Boot(program);
        Bus.SetVector(0xFFFA, NmiHandler);
        Bus.SetVector(0xFFFE, IrqHandler);
    }

    [Fact]
    public void Nmi_raised_before_an_instruction_is_taken_after_it()
    {
        BootWithVectors(0xEA, 0xEA);
        Cpu.NmiLine = true;

        Step().Should().Be(2 + 7);

        Cpu.PC.Should().Be(NmiHandler);
        Cpu.InterruptDisable.Should().BeTrue();
        Bus.Memory[0x01FD].Should().Be(0x80);
        Bus.Memory[0x01FC].Should().Be(0x01);
        (Bus.Memory[0x01FB] & 0x10).Should().Be(0, "hardware interrupts push B clear");
    }

    [Fact]
    public void Nmi_is_edge_triggered()
    {
        BootWithVectors(0xEA);
        Bus.Memory[NmiHandler] = 0xEA;
        Bus.Memory[NmiHandler + 1] = 0xEA;
        Cpu.NmiLine = true;

        Cpu.Step();
        Cpu.PC.Should().Be(NmiHandler);

        Cpu.Step();
        Cpu.PC.Should().Be(NmiHandler + 1, "the line stayed high, so there is no new edge");
    }

    [Fact]
    public void Nmi_raised_on_the_last_cycle_waits_one_more_instruction()
    {
        BootWithVectors(0xEA, 0xEA);
        Bus.BeforeAccess = n =>
        {
            if (n == 1)
            {
                Cpu.NmiLine = true; // during the NOP's final (dummy read) cycle
            }
        };

        Cpu.Step();
        Cpu.PC.Should().Be(0x8001);

        Cpu.Step();
        Cpu.PC.Should().Be(NmiHandler);
    }

    [Fact]
    public void Irq_is_ignored_while_interrupts_are_disabled()
    {
        BootWithVectors(0xEA, 0xEA);
        Cpu.SetIrq(IrqSource.External);

        StepN(2);

        Cpu.PC.Should().Be(0x8002);
    }

    [Fact]
    public void Irq_is_level_triggered_and_taken_when_enabled()
    {
        BootWithVectors(0xEA);
        Cpu.InterruptDisable = false;
        Cpu.SetIrq(IrqSource.Mapper);

        Step().Should().Be(2 + 7);
        Cpu.PC.Should().Be(IrqHandler);
    }

    [Fact]
    public void Cli_delays_a_pending_irq_by_one_instruction()
    {
        BootWithVectors(0x58, 0xEA, 0xEA); // CLI; NOP; NOP
        Cpu.SetIrq(IrqSource.External);

        Cpu.Step();
        Cpu.PC.Should().Be(0x8001, "the IRQ is not taken immediately after CLI");

        Cpu.Step();
        Cpu.PC.Should().Be(IrqHandler);
        Bus.Memory[0x01FC].Should().Be(0x02, "the NOP after CLI runs before the IRQ");
    }

    [Fact]
    public void Sei_still_lets_a_pending_irq_through_with_i_set_on_the_stack()
    {
        BootWithVectors(0x78, 0xEA); // SEI; NOP
        Cpu.InterruptDisable = false;
        Cpu.SetIrq(IrqSource.External);

        Cpu.Step();

        Cpu.PC.Should().Be(IrqHandler);
        (Bus.Memory[0x01FB] & 0x04).Should().Be(0x04);
    }

    [Fact]
    public void Clearing_every_irq_source_releases_the_line()
    {
        BootWithVectors(0xEA, 0xEA);
        Cpu.InterruptDisable = false;
        Cpu.SetIrq(IrqSource.Mapper);
        Cpu.SetIrq(IrqSource.FrameCounter);
        Cpu.ClearIrq(IrqSource.Mapper);
        Cpu.ClearIrq(IrqSource.FrameCounter);

        Cpu.Step();

        Cpu.PC.Should().Be(0x8001);
    }

    [Fact]
    public void Nmi_hijacks_brk()
    {
        BootWithVectors(0x00, 0x00);
        Bus.BeforeAccess = n =>
        {
            if (n == 2)
            {
                Cpu.NmiLine = true; // while BRK pushes PCH
            }
        };

        Step().Should().Be(7);

        Cpu.PC.Should().Be(NmiHandler);
        (Bus.Memory[0x01FB] & 0x10).Should().Be(0x10, "the B flag from BRK is still pushed");
    }

    [Theory]
    [InlineData(false, 513)] // DMA halt lands on an even cycle
    [InlineData(true, 514)]  // a 3-cycle LDA first: halt lands on an odd cycle, so an alignment cycle is added
    public void Oam_dma_stalls_the_cpu_for_513_or_514_cycles(bool leadingLda, long expected)
    {
        // STA $4014 ends on cycle 11 after power (7 reset cycles + 4), so the halt cycle is cycle 12.
        Boot(leadingLda ? [0xA5, 0x00, 0x8D, 0x14, 0x40, 0xEA] : [0x8D, 0x14, 0x40, 0xEA]);
        Bus.AfterWrite = (address, value) =>
        {
            if (address == 0x4014)
            {
                Cpu.RequestOamDma(value);
            }
        };
        if (leadingLda)
        {
            Cpu.Step();
        }

        var start = Cpu.CycleCount;
        Cpu.Step(); // STA $4014 (4 cycles)
        Cpu.Step(); // NOP (2 cycles), delayed by the DMA

        (Cpu.CycleCount - start - 4 - 2).Should().Be(expected);
        Bus.Log.Count(a => a is { IsWrite: true, Address: 0x2004 }).Should().Be(256);
    }
}
