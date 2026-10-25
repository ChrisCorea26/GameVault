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

public class TurboStickAndPresetTests
{
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());

    [Fact]
    public void Turbo_is_on_for_two_frames_then_off_for_two_by_frame_number()
    {
        _input.PressTurbo(5, 0, ControllerState.A);

        var pattern = Enumerable.Range(0, 8).Select(f => _input.Sample(0, f) == ControllerState.A).ToArray();

        pattern.Should().Equal(true, true, false, false, true, true, false, false);
    }

    [Theory]
    [InlineData(2, new[] { true, false, true, false, true, false })]
    [InlineData(3, new[] { true, true, false, true, true, false })]
    [InlineData(6, new[] { true, true, true, false, false, false })]
    public void Turbo_speed_is_configurable(int period, bool[] expected)
    {
        _input.TurboPeriodFrames = period;
        _input.PressTurbo(5, 0, ControllerState.A);

        Enumerable.Range(0, 6).Select(f => _input.Sample(0, f) == ControllerState.A).Should().Equal(expected);
    }

    [Fact]
    public void Turbo_speed_stays_in_range()
    {
        _input.TurboPeriodFrames = 1;
        _input.TurboPeriodFrames.Should().Be(2);
        _input.TurboPeriodFrames = 50;
        _input.TurboPeriodFrames.Should().Be(8);
    }

    [Fact]
    public void Keyboard_a_and_s_are_turbo_b_and_turbo_a_for_player_1()
    {
        _input.KeyDown(VirtualKeys.A).Should().BeTrue();
        _input.KeyDown(VirtualKeys.S).Should().BeTrue();

        var pattern = Enumerable.Range(0, 4).Select(f => _input.Sample(0, f)).ToArray();
        pattern.Should().Equal(
            ControllerState.A | ControllerState.B, ControllerState.A | ControllerState.B,
            ControllerState.None, ControllerState.None);

        _input.KeyUp(VirtualKeys.A);
        _input.KeyUp(VirtualKeys.S);
        _input.Sample(0, 0).Should().Be(ControllerState.None);
    }

    [Fact]
    public void The_two_player_preset_uses_a_and_s_for_player_2s_d_pad()
    {
        _input.SetKeyboardMapping(KeyboardMapping.CreateTwoPlayer());
        _input.KeyDown(VirtualKeys.A);

        Enumerable.Range(0, 4).Select(f => _input.Sample(1, f)).Should().OnlyContain(s => s == ControllerState.Left);
    }

    [Fact]
    public void F11_is_left_for_the_page_and_cant_be_a_game_key()
    {
        _input.KeyDown(VirtualKeys.F11).Should().BeFalse("the Player page switches full screen");
        _input.Sample(0, 0).Should().Be(ControllerState.None);

        var bind = () => new KeyboardMapping().Bind(VirtualKeys.F11, 0, ControllerState.A);
        bind.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Turbo_and_a_held_button_combine()
    {
        _input.PressTurbo(5, 0, ControllerState.A);
        _input.Press(5, 0, ControllerState.A);

        _input.Sample(0, frame: 2).Should().Be(ControllerState.A, "the held press covers turbo's off frames");
    }

    [Fact]
    public void Release_source_drops_only_that_sources_buttons()
    {
        _input.Press(5, 0, ControllerState.B);
        _input.PressTurbo(5, 0, ControllerState.A);
        _input.KeyDown(VirtualKeys.Enter);

        _input.ReleaseSource(5);

        _input.Sample(0, 0).Should().Be(ControllerState.Start);
    }

    [Theory]
    [InlineData(32767, 0, ControllerState.Right)]
    [InlineData(-32768, 0, ControllerState.Left)]
    [InlineData(0, -32768, ControllerState.Up)]
    [InlineData(0, 32767, ControllerState.Down)]
    [InlineData(25000, -25000, ControllerState.Right | ControllerState.Up)]
    [InlineData(-25000, 25000, ControllerState.Left | ControllerState.Down)]
    [InlineData(10000, 10000, ControllerState.None)] // inside the 50 % dead zone
    public void Stick_maps_to_eight_directions_with_a_dead_zone(short x, short y, ControllerState expected)
    {
        StickInput.ToDirections(x, y).Should().Be(expected);
    }

    [Fact]
    public void Two_player_keyboard_preset_splits_the_keyboard()
    {
        _input.SetKeyboardMapping(KeyboardMapping.CreateTwoPlayer());

        _input.KeyDown(VirtualKeys.Period);
        _input.KeyDown(VirtualKeys.W);
        _input.KeyDown(VirtualKeys.G);

        _input.Sample(0).Should().Be(ControllerState.A);
        _input.Sample(1).Should().Be(ControllerState.Up | ControllerState.A);
        _input.KeyDown(VirtualKeys.X).Should().BeFalse("X is unbound in the preset");
    }

    [Fact]
    public void Switching_mappings_releases_held_keys()
    {
        _input.KeyDown(VirtualKeys.X);

        _input.SetKeyboardMapping(KeyboardMapping.CreateTwoPlayer());

        _input.Sample(0).Should().Be(ControllerState.None);
    }
}
