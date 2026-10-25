using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels.Tests.Input;

/// <summary>docs/design/input.md §12: Settings → Input with the sample devices.</summary>
public class InputSettingsViewModelTests
{
    private readonly SampleInputDevicesService _devices = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly InputSettingsViewModel _vm;

    public InputSettingsViewModelTests()
    {
        _vm = new InputSettingsViewModel(_devices, _settings);
    }

    [Fact]
    public void Shows_the_sample_devices_from_input_md_section_9()
    {
        _vm.Ports.Select(p => p.Title).Should().Equal("Player 1", "Player 2");
        _vm.Ports[0].Sources.Select(s => s.Name).Should().Equal("Keyboard", "Xbox Wireless Controller");
        _vm.Ports[1].Sources.Select(s => s.Name).Should().Equal("8BitDo SN30 Pro");
        _vm.Unassigned.Select(u => u.Name).Should().Equal("DualSense Wireless Controller");
        _vm.HasUnassigned.Should().BeTrue();
    }

    [Fact]
    public async Task Turbo_speed_is_applied_and_saved()
    {
        _vm.TurboSpeedIndex.Should().Be(2, "15 presses per second is the default");
        _vm.TurboSpeedOptions[2].Should().Contain("default");

        _vm.TurboSpeedIndex = 0;

        _devices.TurboPeriodFrames.Should().Be(2, "30 presses per second");
        await Task.Delay(50);
        (await _settings.LoadAsync()).TurboPeriodFrames.Should().Be(2);
    }

    [Fact]
    public void Swap_players_exchanges_the_controllers_but_keeps_the_keyboard_on_player_1()
    {
        _vm.SwapPlayersCommand.Execute(null);

        _vm.Ports[0].Sources.Select(s => s.Name).Should().Equal("Keyboard", "8BitDo SN30 Pro");
        _vm.Ports[1].Sources.Select(s => s.Name).Should().Equal("Xbox Wireless Controller");
    }

    [Fact]
    public void Use_for_player_assigns_an_unassigned_controller()
    {
        _vm.Unassigned[0].UseForPlayer2Command.Execute(null);

        _vm.Ports[1].Sources.Select(s => s.Name).Should().Equal("DualSense Wireless Controller");
        _vm.Unassigned.Select(u => u.Name).Should().Equal("8BitDo SN30 Pro");
        _vm.LastNotice.Should().Be("DualSense Wireless Controller is now Player 2");
    }

    [Fact]
    public void Pinning_marks_the_controller_and_the_keyboard_cannot_be_pinned()
    {
        _vm.Ports[0].Sources[0].CanPin.Should().BeFalse();

        _vm.Ports[0].Sources[1].TogglePinCommand.Execute(null);

        _vm.Ports[0].Sources[1].IsPinned.Should().BeTrue();
        _vm.Ports[0].Sources[1].PinLabel.Should().Be("Unpin");
    }

    [Fact]
    public async Task Two_player_keyboard_puts_the_keyboard_on_both_ports_and_is_saved()
    {
        _vm.TwoPlayerKeyboard = true;
        await Task.Yield();

        _vm.Ports[1].Sources.Select(s => s.Name).Should().Contain("Keyboard");
        (await _settings.LoadAsync()).TwoPlayerKeyboard.Should().BeTrue();
    }

    [Fact]
    public void Pins_round_trip_through_the_settings_string()
    {
        var text = InputDevicesModel.SerializePins(new Dictionary<string, int> { ["b=weird"] = 1, ["a"] = 0 });

        InputDevicesModel.ParsePins(text).Should().BeEquivalentTo(new Dictionary<string, int> { ["a"] = 0, ["b=weird"] = 1 });
    }
}
