using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Input;

/// <summary>The Power Pad and the Arkanoid paddle on port 2 (SPEC §4.7; NESdev Wiki: "Power Pad", "Arkanoid controller").</summary>
public class PowerPadAndPaddleTests
{
    private static NesConsole Console(InputSetup setup)
    {
        var console = NesConsole.FromBytes(TestRoms.Nrom([0x4C, 0x00, 0xC0])); // JMP * (loop)
        console.ConnectInput(setup);
        console.Power();
        return console;
    }

    private static (int D3, int D4)[] ReadPort2(NesConsole console, int count)
    {
        console.Bus.Write(0x4016, 1);
        console.Bus.Write(0x4016, 0);
        var bits = new (int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var value = console.Bus.Read(0x4017);
            bits[i] = ((value >> 3) & 1, (value >> 4) & 1);
        }

        return bits;
    }

    [Fact]
    public void The_power_pad_shifts_out_its_buttons_in_the_mats_order()
    {
        var console = Console(InputSetup.PowerPad);
        console.PowerPad.Buttons = (1 << (1 - 1)) | (1 << (6 - 1)) | (1 << (3 - 1)) | (1 << (12 - 1)); // 1, 6, 3 and 12

        var bits = ReadPort2(console, 10);

        // D3: 2, 1, 5, 9, 6, 10, 11, 7, then 1s. D4: 4, 3, 12, 8, then 1s.
        bits.Select(b => b.D3).Should().Equal(0, 1, 0, 0, 1, 0, 0, 0, 1, 1);
        bits.Select(b => b.D4).Should().Equal(0, 1, 1, 0, 1, 1, 1, 1, 1, 1);
    }

    [Fact]
    public void The_paddle_shifts_out_its_position_inverted_with_fire_on_d3()
    {
        var console = Console(InputSetup.ArkanoidPaddle);
        console.Paddle.Position = 0b1010_1100; // 172
        console.Paddle.Fire = true;

        var bits = ReadPort2(console, 8);

        bits.Select(b => b.D4).Should().Equal(0, 1, 0, 1, 0, 0, 1, 1); // MSB first, inverted
        bits.Should().OnlyContain(b => b.D3 == 1);
    }

    [Fact]
    public void The_paddle_stays_within_its_knobs_travel()
    {
        var paddle = new ArkanoidPaddle { Position = 0 };
        paddle.Position.Should().Be(ArkanoidPaddle.MinPosition);
        paddle.SetFraction(1);
        paddle.Position.Should().Be(ArkanoidPaddle.MaxPosition);
        paddle.SetFraction(0.5);
        paddle.Position.Should().Be(170);
    }

    [Theory]
    [InlineData(InputSetup.PowerPad)]
    [InlineData(InputSetup.ArkanoidPaddle)]
    public void Save_states_round_trip_the_devices(InputSetup setup)
    {
        var console = Console(setup);
        console.PowerPad.Buttons = 0x0A5A;
        console.Paddle.Position = 200;
        console.Paddle.Fire = true;
        console.Bus.Write(0x4016, 1);
        console.Bus.Write(0x4016, 0);
        console.Bus.Read(0x4017); // part-way through the shift register
        var snapshot = console.SaveSnapshot();

        var other = Console(setup);
        other.LoadSnapshot(snapshot);

        other.PowerPad.Buttons.Should().Be(0x0A5A);
        other.Paddle.Position.Should().Be(200);
        other.Paddle.Fire.Should().BeTrue();
        other.Bus.Read(0x4017).Should().Be(console.Bus.Read(0x4017));
        other.SaveSnapshot().Should().Equal(console.SaveSnapshot());
    }
}
