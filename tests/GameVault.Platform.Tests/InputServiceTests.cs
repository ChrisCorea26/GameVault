using GameVault.Core.Input;
using GameVault.Platform.Input;

namespace GameVault.Platform.Tests;

/// <summary>docs/design/input.md §12: merging, opposite directions, focus release, hotkeys.</summary>
public class InputServiceTests
{
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());

    [Fact]
    public void Default_keyboard_drives_player_1()
    {
        _input.KeyDown(VirtualKeys.X).Should().BeTrue();
        _input.KeyDown(VirtualKeys.Z);
        _input.KeyDown(VirtualKeys.Enter);
        _input.KeyDown(VirtualKeys.RightShift);
        _input.KeyDown(VirtualKeys.Up);

        _input.Sample(0).Should().Be(ControllerState.A | ControllerState.B | ControllerState.Start | ControllerState.Select | ControllerState.Up);
        _input.Sample(1).Should().Be(ControllerState.None, "player 2 has no default keyboard binding");
    }

    [Fact]
    public void Key_up_releases_and_key_repeat_is_ignored()
    {
        _input.KeyDown(VirtualKeys.X);
        _input.KeyDown(VirtualKeys.X); // auto-repeat
        _input.KeyUp(VirtualKeys.X);

        _input.Sample(0).Should().Be(ControllerState.None);
    }

    [Fact]
    public void Sources_are_ored_per_port()
    {
        const int pad = 1;
        _input.KeyDown(VirtualKeys.X);
        _input.Press(pad, 0, ControllerState.Start);
        _input.Press(pad, 1, ControllerState.B);

        _input.Sample(0).Should().Be(ControllerState.A | ControllerState.Start);
        _input.Sample(1).Should().Be(ControllerState.B);

        _input.KeyUp(VirtualKeys.X);
        _input.Sample(0).Should().Be(ControllerState.Start, "the pad still holds Start");
    }

    [Fact]
    public void The_same_button_from_two_sources_stays_held_until_both_release()
    {
        _input.KeyDown(VirtualKeys.X);
        _input.Press(1, 0, ControllerState.A);

        _input.KeyUp(VirtualKeys.X);

        _input.Sample(0).Should().Be(ControllerState.A);
    }

    [Theory]
    [InlineData(VirtualKeys.Left, VirtualKeys.Right, ControllerState.Right)]
    [InlineData(VirtualKeys.Right, VirtualKeys.Left, ControllerState.Left)]
    [InlineData(VirtualKeys.Up, VirtualKeys.Down, ControllerState.Down)]
    public void Most_recent_opposite_direction_wins(int first, int second, ControllerState expected)
    {
        _input.KeyDown(first);
        _input.KeyDown(second);

        _input.Sample(0).Should().Be(expected);

        _input.KeyUp(second);
        _input.Sample(0).Should().NotBe(ControllerState.None, "the first direction is still held");
    }

    [Fact]
    public void Opposite_directions_pass_through_when_allowed()
    {
        _input.AllowOppositeDirections = true;
        _input.KeyDown(VirtualKeys.Left);
        _input.KeyDown(VirtualKeys.Right);

        _input.Sample(0).Should().Be(ControllerState.Left | ControllerState.Right);
    }

    [Fact]
    public void Release_all_clears_every_held_button()
    {
        _input.KeyDown(VirtualKeys.Left);
        _input.KeyDown(VirtualKeys.X);
        _input.Press(1, 1, ControllerState.A);

        _input.ReleaseAll();

        _input.Sample(0).Should().Be(ControllerState.None);
        _input.Sample(1).Should().Be(ControllerState.None);

        _input.KeyUp(VirtualKeys.Left); // the stale key-up after focus returns is harmless
        _input.KeyDown(VirtualKeys.Left);
        _input.Sample(0).Should().Be(ControllerState.Left);
    }

    [Theory]
    [InlineData(VirtualKeys.Escape)]
    [InlineData(VirtualKeys.Backspace)]
    [InlineData(VirtualKeys.Tab)]
    [InlineData(VirtualKeys.F5)]
    [InlineData(VirtualKeys.F8)]
    [InlineData(VirtualKeys.F12)]
    public void Hotkeys_never_reach_the_game(int key)
    {
        var handled = _input.KeyDown(key);

        handled.Should().Be(key != VirtualKeys.Escape, "Esc is left to the page's pause accelerator");
        _input.Sample(0).Should().Be(ControllerState.None);
        _input.IsHotkeyHeld(key).Should().BeTrue();
        _input.KeyUp(key);
        _input.IsHotkeyHeld(key).Should().BeFalse();

        var bind = () => new KeyboardMapping().Bind(key, 0, ControllerState.A);
        bind.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(VirtualKeys.Shift, VirtualKeys.RightShiftScanCode, VirtualKeys.RightShift)]
    [InlineData(VirtualKeys.Shift, 0x2Au, VirtualKeys.LeftShift)]
    [InlineData(VirtualKeys.X, 0x2Du, VirtualKeys.X)]
    public void Shift_keys_are_told_apart_by_scan_code(int key, uint scanCode, int expected)
    {
        VirtualKeys.Normalize(key, scanCode).Should().Be(expected);
    }
}
