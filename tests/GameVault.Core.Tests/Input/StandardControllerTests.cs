using GameVault.Core.Input;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests.Input;

/// <summary>NESdev Wiki "Standard controller"; bit order per docs/design/input.md §2.</summary>
public class StandardControllerTests
{
    private static NesConsole Console()
    {
        var console = NesConsole.FromBytes(TestRoms.Nrom([0xEA]));
        console.Power();
        return console;
    }

    private static int[] ReadEight(NesConsole console, ushort port)
    {
        console.Bus.Write(0x4016, 1);
        console.Bus.Write(0x4016, 0);
        return Enumerable.Range(0, 8).Select(_ => console.Bus.Read(port) & 1).ToArray();
    }

    [Fact]
    public void Reads_shift_out_a_b_select_start_up_down_left_right()
    {
        var console = Console();
        console.SetControllerState(0, ControllerState.A | ControllerState.Start | ControllerState.Left);

        ReadEight(console, 0x4016).Should().Equal(1, 0, 0, 1, 0, 0, 1, 0);
    }

    [Fact]
    public void Player_two_reads_from_4017()
    {
        var console = Console();
        console.SetControllerState(1, ControllerState.B | ControllerState.Right);

        ReadEight(console, 0x4017).Should().Equal(0, 1, 0, 0, 0, 0, 0, 1);
        ReadEight(console, 0x4016).Should().Equal(0, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void Reads_after_the_eighth_return_1()
    {
        var console = Console();
        ReadEight(console, 0x4016);

        (console.Bus.Read(0x4016) & 1).Should().Be(1);
        (console.Bus.Read(0x4016) & 1).Should().Be(1);
    }

    [Fact]
    public void While_strobe_is_high_reads_keep_returning_a()
    {
        var console = Console();
        console.SetControllerState(0, ControllerState.A);
        console.Bus.Write(0x4016, 1);

        (console.Bus.Read(0x4016) & 1).Should().Be(1);
        (console.Bus.Read(0x4016) & 1).Should().Be(1);
    }

    [Fact]
    public void Upper_bits_come_from_open_bus()
    {
        var console = Console();
        console.Bus.Write(0x4016, 0x40);

        // In a real "LDA $4016" the bus still holds the address high byte ($40); here it holds the written $40.
        (console.Bus.Read(0x4016) & 0xE0).Should().Be(0x40);
    }

    [Fact]
    public void State_round_trips_mid_read()
    {
        StateAssert.RoundTrips(() => new StandardController(), c =>
        {
            c.State = ControllerState.Up | ControllerState.B;
            c.Write(1);
            c.Write(0);
            c.Read();
            c.Read();
        });
    }
}
