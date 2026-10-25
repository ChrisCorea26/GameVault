using GameVault.Core.Input;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services.Input;

namespace GameVault.Platform.Tests;

public class ControllerManagerTests
{
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());
    private readonly FakeBackend _backend = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly ControllerManager _manager;
    private readonly List<NavigationAction> _navigation = [];
    private readonly List<string> _notices = [];
    private int _pauseRequests;

    public ControllerManagerTests()
    {
        _manager = new ControllerManager(_input, _backend, _settings);
        _manager.Navigate += (_, a) => _navigation.Add(a);
        _manager.Notice += (_, n) => _notices.Add(n);
        _manager.PauseRequested += (_, _) => _pauseRequests++;
        _manager.StartAsync().GetAwaiter().GetResult();
    }

    private static ControllerInfo Pad(int id, string name = "Pad") => new(id, $"guid-{id}", $"{name} {id}");

    [Fact]
    public void Connecting_assigns_and_announces()
    {
        _manager.OnConnected(Pad(1));

        _notices.Should().Equal("Pad 1 is now Player 1");
        _manager.Ports[0].Sources.Select(s => s.Name).Should().Equal("Keyboard", "Pad 1");
    }

    [Fact]
    public void With_game_focus_assigned_pads_play_with_the_positional_mapping()
    {
        _manager.OnConnected(Pad(1));
        _manager.OnConnected(Pad(2));
        _manager.GameHasFocus = true;

        _manager.OnButton(1, PadButton.South, true);
        _manager.OnButton(1, PadButton.Start, true);
        _manager.OnButton(2, PadButton.East, true);
        _manager.OnButton(2, PadButton.DPadLeft, true);

        _input.Sample(0).Should().Be(ControllerState.B | ControllerState.Start);
        _input.Sample(1).Should().Be(ControllerState.A | ControllerState.Left);
        _navigation.Should().BeEmpty();
    }

    [Fact]
    public void West_and_north_are_turbo_b_and_a()
    {
        _manager.OnConnected(Pad(1));
        _manager.GameHasFocus = true;

        _manager.OnButton(1, PadButton.North, true);

        _input.Sample(0, frame: 0).Should().Be(ControllerState.A);
        _input.Sample(0, frame: 2).Should().Be(ControllerState.None);
    }

    [Fact]
    public void Left_stick_drives_the_dpad_in_game()
    {
        _manager.OnConnected(Pad(1));
        _manager.GameHasFocus = true;

        _manager.OnAxis(1, PadAxis.LeftX, 30000);
        _input.Sample(0).Should().Be(ControllerState.Right);

        _manager.OnAxis(1, PadAxis.LeftX, 0);
        _input.Sample(0).Should().Be(ControllerState.None);
    }

    [Fact]
    public void Without_game_focus_every_pad_navigates_the_ui_with_repeat()
    {
        _manager.OnConnected(Pad(1));
        _manager.OnConnected(Pad(2));
        _manager.OnConnected(Pad(3)); // unassigned pads navigate too

        _manager.OnButton(3, PadButton.DPadDown, true);
        _manager.OnTick(0);
        _manager.OnTick(300);
        _manager.OnTick(400);
        _manager.OnTick(520);
        _manager.OnButton(3, PadButton.DPadDown, false);
        _manager.OnTick(700);
        _manager.OnButton(1, PadButton.South, true);
        _manager.OnButton(1, PadButton.East, true);

        _navigation.Should().Equal(
            NavigationAction.Down, NavigationAction.Down, NavigationAction.Down,
            NavigationAction.Activate, NavigationAction.Back);
        _input.Sample(0).Should().Be(ControllerState.None, "nothing reaches the game");
    }

    [Fact]
    public void Disconnect_during_play_releases_buttons_and_reports_the_port()
    {
        ControllerDisconnectedEventArgs? disconnected = null;
        _manager.ControllerDisconnected += (_, e) => disconnected = e;
        _manager.OnConnected(Pad(1, "Xbox"));
        _manager.SetGameRunning(true);
        _manager.GameHasFocus = true;
        _manager.OnButton(1, PadButton.East, true);

        _manager.OnDisconnected(1);

        _input.Sample(0).Should().Be(ControllerState.None);
        disconnected!.Port.Should().Be(0);
        disconnected.Name.Should().Be("Xbox 1");
        _manager.Ports[0].WaitingFor.Should().Be("Xbox 1");
    }

    [Fact]
    public void Take_over_press_assigns_the_pad_without_reaching_the_game()
    {
        _manager.OnConnected(Pad(1));
        _manager.OnConnected(Pad(2));
        _manager.OnConnected(Pad(3));
        _manager.SetGameRunning(true);
        _manager.GameHasFocus = true;
        _manager.OnDisconnected(2);

        _manager.OnButton(3, PadButton.Start, true);

        _notices.Should().Contain("Pad 3 is now Player 2");
        _input.Sample(1).Should().Be(ControllerState.None, "the take-over press is swallowed");

        _manager.OnButton(3, PadButton.Start, false);
        _manager.OnButton(3, PadButton.East, true);
        _input.Sample(1).Should().Be(ControllerState.A);
    }

    [Fact]
    public void Guide_or_holding_back_and_start_requests_pause()
    {
        _manager.OnConnected(Pad(1));
        _manager.GameHasFocus = true;

        _manager.OnButton(1, PadButton.Guide, true);
        _pauseRequests.Should().Be(1);

        _manager.OnButton(1, PadButton.Back, true);
        _manager.OnButton(1, PadButton.Start, true);
        _manager.OnTick(100);
        _manager.OnTick(900);
        _pauseRequests.Should().Be(1, "not held long enough yet");
        _manager.OnTick(1100);
        _manager.OnTick(1500);
        _pauseRequests.Should().Be(2, "fires once per hold");
    }

    [Fact]
    public void Right_shoulder_holds_fast_forward()
    {
        _manager.OnConnected(Pad(1));
        _manager.GameHasFocus = true;

        _manager.OnButton(1, PadButton.RightShoulder, true);
        _input.IsHotkeyHeld(VirtualKeys.Tab).Should().BeTrue();

        _manager.OnButton(1, PadButton.RightShoulder, false);
        _input.IsHotkeyHeld(VirtualKeys.Tab).Should().BeFalse();
    }

    [Fact]
    public void Pads_are_ignored_while_the_window_is_in_the_background()
    {
        _manager.OnConnected(Pad(1));
        _manager.GameHasFocus = true;
        _manager.AppActive = false;

        _manager.OnButton(1, PadButton.East, true);

        _input.Sample(0).Should().Be(ControllerState.None);
    }

    [Fact]
    public async Task Pins_and_the_keyboard_preset_are_saved_and_restored()
    {
        _manager.OnConnected(Pad(1));
        _manager.SetPinned("pad:1", true);
        await Task.Delay(50);
        _manager.TwoPlayerKeyboard = true;
        await _settings.SaveAsync((await _settings.LoadAsync()) with { TwoPlayerKeyboard = true });

        var restarted = new ControllerManager(new InputService(KeyboardMapping.CreateDefault()), new FakeBackend(), _settings);
        await restarted.StartAsync();
        restarted.OnConnected(Pad(9) with { Key = "guid-1" });

        restarted.Ports[0].Sources.Should().Contain(s => s.Name == "Pad 9" && s.IsPinned);
        restarted.TwoPlayerKeyboard.Should().BeTrue();
    }

    private sealed class FakeBackend : IControllerBackend
    {
        public IControllerEvents? Events { get; private set; }

        public void Start(IControllerEvents events) => Events = events;

        public void Dispose()
        {
        }
    }
}
