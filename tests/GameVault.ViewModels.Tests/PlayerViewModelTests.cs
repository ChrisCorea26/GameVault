using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class PlayerViewModelTests
{
    private readonly FakeEmulator _emulator = new();
    private readonly FakeNavigationService _navigation = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly SampleInputDevicesService _devices = new();
    private readonly SampleSaveStateService _saveStates = new();
    private readonly PlayerViewModel _vm;

    public PlayerViewModelTests()
    {
        _navigation.NavigateTo(AppPage.Library);
        _navigation.NavigateTo(AppPage.Player, "game.nes");
        _vm = new PlayerViewModel(_emulator, _navigation, _settings, _devices, new SaveStatesViewModel(_saveStates));
    }

    [Fact]
    public async Task Start_loads_the_game_and_shows_its_title()
    {
        await _vm.StartCommand.ExecuteAsync(@"C:\Games\Star Courier.nes");

        _emulator.LoadedPath.Should().Be(@"C:\Games\Star Courier.nes");
        _vm.Title.Should().Be("Star Courier");
        _vm.IsRunning.Should().BeTrue();
        _vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Load_errors_are_shown_in_plain_language()
    {
        _emulator.LoadError = "This game uses a cartridge type GameVault doesn't support yet (mapper 4).";

        await _vm.StartCommand.ExecuteAsync("mmc3.nes");

        _vm.ErrorMessage.Should().Be(_emulator.LoadError);
        _vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Toggle_pause_switches_between_paused_and_running()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.TogglePauseCommand.Execute(null);
        _vm.IsPaused.Should().BeTrue();
        _vm.PauseButtonLabel.Should().Be("Resume");
        _vm.StatusText.Should().Be("Paused");

        _vm.TogglePauseCommand.Execute(null);
        _vm.IsRunning.Should().BeTrue();
        _vm.PauseButtonLabel.Should().Be("Pause");
    }

    [Theory]
    [InlineData(true, EmulatorStatus.Paused)]
    [InlineData(false, EmulatorStatus.Running)]
    public async Task Window_deactivation_pauses_only_when_the_setting_is_on(bool pauseWhenInactive, EmulatorStatus expected)
    {
        await _settings.SaveAsync(new AppSettings { PauseWhenInactive = pauseWhenInactive });
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.OnWindowDeactivated();

        _vm.Status.Should().Be(expected);
    }

    [Fact]
    public async Task Back_stops_the_game_and_returns_to_the_previous_page()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.BackCommand.Execute(null);

        _emulator.Status.Should().Be(EmulatorStatus.Idle);
        _navigation.CurrentPage.Should().Be(AppPage.Library);
    }

    [Fact]
    public async Task Faults_from_the_emulator_are_reported()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _emulator.RaiseFault("The game stopped because of an emulator error.");

        _vm.Status.Should().Be(EmulatorStatus.Faulted);
        _vm.ErrorMessage.Should().Contain("emulator error");
    }

    [Fact]
    public async Task After_stop_the_view_model_no_longer_listens()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.Stop();

        _emulator.HasListeners.Should().BeFalse();
    }

    [Fact]
    public async Task A_controller_disconnect_pauses_and_explains()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _devices.SimulateDisconnect(2, "8BitDo SN30 Pro");

        _vm.IsPaused.Should().BeTrue();
        _vm.ControllerMessage.Should().Be(
            "Player 2's controller was disconnected. Reconnect it, or press a button on another controller to take over.");
    }

    [Fact]
    public async Task The_message_clears_when_another_controller_takes_over_but_the_game_stays_paused()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _devices.SimulateDisconnect(2, "8BitDo SN30 Pro");

        _devices.AssignToPort("pad:3", 1); // the DualSense takes Player 2

        _vm.HasControllerMessage.Should().BeFalse();
        _vm.IsPaused.Should().BeTrue("the player resumes from the overlay (input.md §5)");
        _vm.Notice.Should().Be("DualSense Wireless Controller is now Player 2");
    }

    [Fact]
    public async Task Disconnects_outside_play_do_not_pause()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.Stop();

        _devices.SimulateDisconnect(1, "Xbox Wireless Controller");

        _vm.HasControllerMessage.Should().BeFalse();
    }

    [Fact]
    public async Task Emulator_and_save_state_notices_are_shown()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _emulator.RaiseNotice("Reduce input lag is off for now");
        _vm.Notice.Should().Be("Reduce input lag is off for now");

        await _saveStates.SaveAsync(2);
        _vm.Notice.Should().Be("Saved to slot 3");
    }

    private sealed class FakeEmulator : IEmulatorService
    {
        public EmulatorStatus Status { get; private set; }

        public string? GameTitle { get; private set; }

        public string? FaultMessage { get; private set; }

        public string? LoadedPath { get; private set; }

        public string? LoadError { get; set; }

        public bool HasListeners => StatusChanged is not null;

        public event EventHandler? StatusChanged;

        public event EventHandler<string>? Notice;

        public void RaiseNotice(string message) => Notice?.Invoke(this, message);

        public Task LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (LoadError is not null)
            {
                throw new GameLoadException(LoadError);
            }

            LoadedPath = path;
            GameTitle = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            Set(EmulatorStatus.Running);
            return Task.CompletedTask;
        }

        public void Pause() => Set(EmulatorStatus.Paused);

        public void Resume() => Set(EmulatorStatus.Running);

        public void Reset()
        {
        }

        public void Stop()
        {
            GameTitle = null;
            Set(EmulatorStatus.Idle);
        }

        public void RaiseFault(string message)
        {
            FaultMessage = message;
            Set(EmulatorStatus.Faulted);
        }

        private void Set(EmulatorStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
