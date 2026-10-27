using GameVault.Core.Input;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services.Input;

namespace GameVault.Platform.Tests;

/// <summary>Remaps reach the keyboard and pads (SPEC §5.4; GV-001), and the defaults Remap… shows are the real ones.</summary>
public sealed class RemapTests
{
    private const string Keyboard = GameVault.ViewModels.Services.Input.IInputDevicesService.KeyboardId;

    private static PadLayout LayoutOf(InputSystem system) => Enum.Parse<PadLayout>(system.ToString());

    [Fact]
    public void The_reserved_keys_are_the_hotkeys()
    {
        RemapViewModel.ReservedKeys.Should().BeEquivalentTo(KeyboardMapping.Hotkeys);
    }

    [Theory]
    [MemberData(nameof(Systems))]
    public void Every_default_key_in_remap_is_what_the_keyboard_really_does(InputSystem system)
    {
        var input = new InputService(KeyboardMapping.CreateDefault()) { Layout = LayoutOf(system) };
        foreach (var action in InputActions.For(system))
        {
            input.KeyboardBindings.TryGetBinding(action.DefaultKey, out var port, out var button, out var turbo, out var kind)
                .Should().BeTrue($"{system} {action.Label}");
            (port, button, turbo, kind).Should().Be((0, action.Button, action.Turbo, action.Kind), $"{system} {action.Label}");
        }
    }

    [Theory]
    [MemberData(nameof(Systems))]
    public void Every_default_pad_button_in_remap_is_what_the_pad_really_does(InputSystem system)
    {
        foreach (var action in InputActions.For(system))
        {
            if (!InputCodes.TryPad(action.DefaultPad, out var name))
            {
                continue; // keyboard-only, or a trigger or stick
            }

            var input = new InputService(KeyboardMapping.CreateDefault()) { Layout = LayoutOf(system) };
            using var manager = new ControllerManager(input, new FakeBackend(), new InMemorySettingsStore());
            manager.OnConnected(new ControllerInfo(1, "guid|serial", "Pad"));
            manager.GameHasFocus = true;

            manager.OnButton(1, Enum.Parse<PadButton>(name), true);

            input.Sample(0, frame: 0).Buttons().Should().Be(action.Button, $"{system} {action.Label}");
            if (action.Turbo)
            {
                input.Sample(0, frame: 2).Buttons().Should().Be(ControllerState.None, $"{system} {action.Label} is turbo");
            }
        }
    }

    public static TheoryData<InputSystem> Systems() => new(InputActions.Systems);

    [Fact]
    public void Keyboard_remaps_apply_per_layout_and_per_game()
    {
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var manager = new ControllerManager(input, new FakeBackend(), new InMemorySettingsStore());
        manager.Remaps = InputRemapSet.Empty
            .Bind(Keyboard, 0, InputSystem.Nes, null, "B", InputCodes.Key(VirtualKeys.K))
            .Bind(Keyboard, 0, InputSystem.Nes, "sha1:aa", "A", InputCodes.Key(VirtualKeys.L))
            .Bind(Keyboard, 0, InputSystem.Nintendo64, null, "Up", InputCodes.Key(VirtualKeys.W)); // D-pad up

        input.KeyDown(VirtualKeys.K);
        input.Sample(0).Should().Be(ControllerState.B);
        input.KeyUp(VirtualKeys.K);
        input.KeyDown(VirtualKeys.Z);
        input.Sample(0).Should().Be(ControllerState.None, "Z no longer presses B");
        input.KeyUp(VirtualKeys.Z);
        input.KeyDown(VirtualKeys.L);
        input.Sample(0).Should().Be(ControllerState.None, "L is A only in that game");
        input.KeyUp(VirtualKeys.L);

        manager.GameKey = "sha1:aa";
        input.KeyDown(VirtualKeys.L);
        input.Sample(0).Should().Be(ControllerState.A);
        input.KeyUp(VirtualKeys.L);

        input.Layout = PadLayout.Nintendo64;
        input.KeyDown(VirtualKeys.W);
        input.Sample(0).Should().Be(ControllerState.Up, "W is the D-pad, not the stick");
        input.KeyUp(VirtualKeys.W);
        input.KeyDown(VirtualKeys.T);
        input.Sample(0).Should().Be(ControllerState.None, "T was the D-pad's key");
    }

    [Fact]
    public void Pad_remaps_follow_the_controller_model_and_player()
    {
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var manager = new ControllerManager(input, new FakeBackend(), new InMemorySettingsStore());
        manager.OnConnected(new ControllerInfo(1, "model-a|1", "Pad A"));
        manager.OnConnected(new ControllerInfo(2, "model-b|2", "Pad B"));
        manager.GameHasFocus = true;
        manager.Remaps = InputRemapSet.Empty
            .Bind("model-a", 0, InputSystem.Nes, null, "B", InputCodes.Pad("East"))
            .Bind("model-a", 0, InputSystem.Nes, null, "Start", InputCodes.Pad("LeftShoulder"));

        manager.OnButton(1, PadButton.East, true);
        input.Sample(0).Should().Be(ControllerState.B);
        manager.OnButton(1, PadButton.East, false);
        manager.OnButton(1, PadButton.LeftShoulder, true);
        input.Sample(0).Should().Be(ControllerState.Start);
        input.IsHotkeyHeld(VirtualKeys.Backspace).Should().BeFalse("the shoulder is Start now, not rewind");
        manager.OnButton(1, PadButton.LeftShoulder, false);

        manager.OnButton(2, PadButton.East, true);
        input.Sample(1).Should().Be(ControllerState.A, "another model keeps the defaults");
    }

    [Fact]
    public void While_remap_waits_pad_buttons_are_captured_instead_of_played()
    {
        var input = new InputService(KeyboardMapping.CreateDefault());
        using var manager = new ControllerManager(input, new FakeBackend(), new InMemorySettingsStore());
        var captured = new List<CapturedInput>();
        var navigation = new List<NavigationAction>();
        manager.InputCaptured += (_, c) => captured.Add(c);
        manager.Navigate += (_, n) => navigation.Add(n);
        manager.OnConnected(new ControllerInfo(1, "model-a|1", "Pad A"));

        manager.CaptureMode = true;
        manager.OnButton(1, PadButton.South, true);
        manager.CaptureMode = false;
        manager.OnButton(1, PadButton.South, false);

        captured.Should().Equal(new CapturedInput("pad:1", "pad:South"));
        navigation.Should().BeEmpty("the press doesn't also click whatever has focus");
    }

    private sealed class FakeBackend : IControllerBackend
    {
        public void Start(IControllerEvents events)
        {
        }

        public void Dispose()
        {
        }
    }
}
