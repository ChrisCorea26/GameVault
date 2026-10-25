using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Bus;

public class NesBusTests
{
    private static NesConsole Console(params byte[] program)
    {
        var console = NesConsole.FromBytes(TestRoms.Nrom(program));
        console.Power();
        return console;
    }

    [Fact]
    public void Internal_ram_is_mirrored_every_2k()
    {
        var console = Console();

        console.Bus.Write(0x0001, 0xAB);

        console.Bus.Read(0x0801).Should().Be(0xAB);
        console.Bus.Read(0x1001).Should().Be(0xAB);
        console.Bus.Read(0x1801).Should().Be(0xAB);
    }

    [Fact]
    public void Unmapped_reads_return_the_last_value_on_the_data_bus()
    {
        var console = Console();

        console.Bus.Write(0x0000, 0x5C);

        console.Bus.Read(0x4018).Should().Be(0x5C);
    }

    [Fact]
    public void Each_cpu_cycle_advances_the_ppu_three_dots_on_ntsc()
    {
        var console = Console(0xEA, 0xEA);
        var dot = console.Ppu.Dot;

        console.StepInstruction(); // NOP: 2 cycles

        console.Ppu.Dot.Should().Be(dot + 6);
    }

    [Fact]
    public void Peek_has_no_side_effects()
    {
        var console = Console();
        var cycles = console.Cpu.CycleCount;

        console.Bus.Peek(0x2002);
        console.Bus.Peek(0xC000);

        console.Cpu.CycleCount.Should().Be(cycles);
        console.Bus.Peek(0x4015).Should().Be(0xFF);
    }
}
