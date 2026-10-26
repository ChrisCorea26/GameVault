using GameVault.Core.Input;

namespace GameVault.Core.Tests.Input;

/// <summary>The 64-bit controller state (docs/design/input.md §2): buttons, the left stick and the DualShock 2's right stick.</summary>
public class ControllerStateTests
{
    [Fact]
    public void The_state_is_64_bits_and_keeps_the_existing_bits()
    {
        Enum.GetUnderlyingType(typeof(ControllerState)).Should().Be(typeof(ulong));
        ((ulong)ControllerState.A).Should().Be(1);
        ((ulong)ControllerState.Right).Should().Be(0x80);
        ((ulong)ControllerState.R2).Should().Be(0x8000);
        ((ulong)ControllerState.L3).Should().Be(1UL << 48);
        ((ulong)ControllerState.R3).Should().Be(1UL << 49);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(127, -128, -1, 1)]
    [InlineData(-128, 127, 100, -100)]
    public void Both_sticks_round_trip_without_touching_each_other_or_the_buttons(int lx, int ly, int rx, int ry)
    {
        var buttons = ControllerState.B | ControllerState.R2 | ControllerState.L3 | ControllerState.R3;

        var state = buttons.WithStick((sbyte)lx, (sbyte)ly).WithRightStick((sbyte)rx, (sbyte)ry);

        state.StickX().Should().Be((sbyte)lx);
        state.StickY().Should().Be((sbyte)ly);
        state.RightStickX().Should().Be((sbyte)rx);
        state.RightStickY().Should().Be((sbyte)ry);
        state.Buttons().Should().Be(buttons);

        // Moving one stick leaves the other where it was.
        var moved = state.WithStick(5, -5);
        moved.RightStickX().Should().Be((sbyte)rx);
        moved.RightStickY().Should().Be((sbyte)ry);
        var movedRight = state.WithRightStick(-7, 7);
        movedRight.StickX().Should().Be((sbyte)lx);
        movedRight.StickY().Should().Be((sbyte)ly);
        movedRight.Buttons().Should().Be(buttons);
    }

    [Fact]
    public void The_sticks_sit_in_bits_16_to_47()
    {
        var state = ControllerState.None.WithStick(0x12, 0x34).WithRightStick(0x56, 0x78);

        ((ulong)state).Should().Be(0x0000_7856_3412_0000UL);
        (ControllerStick.SticksMask & (ControllerState.L3 | ControllerState.R3)).Should().Be(ControllerState.None);
    }
}
