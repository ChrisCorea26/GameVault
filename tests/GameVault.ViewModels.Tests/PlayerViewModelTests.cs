using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

public class PlayerViewModelTests
{
    private readonly FakeEmulator _emulator = new();
    private readonly FakeNavigationService _navigation = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly SampleInputDevicesService _devices = new();
    private readonly SampleTimelineService _timeline = new();
    private readonly SampleLibraryService _library = new();
    private readonly SamplePasswordService _passwords = new();
    private readonly SampleJournalService _journal = new();
    private readonly InMemoryPracticeRecords _records = new();
    private readonly PlayerViewModel _vm;

    public PlayerViewModelTests()
    {
        _navigation.NavigateTo(AppPage.Library);
        _navigation.NavigateTo(AppPage.Player, "game.nes");
        _vm = new PlayerViewModel(
            _emulator, _navigation, _settings, _devices, new TimelineViewModel(_timeline), _library, _passwords, _journal,
            new AssistViewModel(_emulator), new PracticeViewModel(_emulator, _records));
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
    public async Task Returning_to_the_window_resumes_a_game_that_paused_itself()
    {
        await _settings.SaveAsync(new AppSettings { PauseWhenInactive = true });
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.OnWindowDeactivated();
        _vm.OnWindowActivated();

        _vm.Status.Should().Be(EmulatorStatus.Running);
    }

    [Fact]
    public async Task Returning_to_the_window_keeps_a_game_the_player_paused()
    {
        await _settings.SaveAsync(new AppSettings { PauseWhenInactive = true });
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.TogglePauseCommand.Execute(null);

        _vm.OnWindowDeactivated();
        _vm.OnWindowActivated();

        _vm.Status.Should().Be(EmulatorStatus.Paused);
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

        await _timeline.SaveAsync();
        _vm.Notice.Should().Be("Saved");
    }

    [Fact]
    public async Task Offers_to_resume_from_the_last_exit_save()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.HasResumeOffer.Should().BeTrue();
        _vm.Timeline.Items.Should().HaveCount(4);
        _vm.Timeline.CanSave.Should().BeTrue();

        await _vm.ResumeCommand.ExecuteAsync(null);
        _vm.HasResumeOffer.Should().BeFalse();
        _vm.Notice.Should().Be("Loaded");
    }

    [Fact]
    public async Task A_launch_with_a_state_loads_it_instead_of_offering_resume()
    {
        await _vm.StartCommand.ExecuteAsync(new GameLaunch("game.nes", "sample:2", "a"));

        _vm.HasResumeOffer.Should().BeFalse();
        _vm.Notice.Should().Be("Loaded");
    }

    [Fact]
    public async Task Stopping_saves_a_resume_point()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        var before = (await _timeline.GetResumePointAsync("sample:2"))!.Id;

        _vm.Stop();

        (await _timeline.GetResumePointAsync("sample:2"))!.Id.Should().NotBe(before);
    }

    [Fact]
    public async Task Rewind_state_and_playtime_flow_through()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        var before = (await _library.GetGameAsync("sample:2"))!.Playtime;

        _emulator.RaiseRewind(true, 12.5);
        _vm.IsRewinding.Should().BeTrue();
        _vm.RewindSeconds.Should().Be(12.5);
        _vm.HasResumeOffer.Should().BeFalse("rewinding means the player has moved on");

        _emulator.RaisePlaytime(TimeSpan.FromMinutes(1));
        await Task.Yield();
        (await _library.GetGameAsync("sample:2"))!.Playtime.Should().Be(before + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task F9_pauses_and_saves_a_typed_password()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _emulator.PressF9();
        await Task.Yield();
        _vm.IsEnteringPassword.Should().BeTrue();
        _emulator.Status.Should().Be(EmulatorStatus.Paused);
        _vm.SaveManualPasswordCommand.CanExecute(null).Should().BeFalse();

        _vm.ManualPassword = " QUEST 2024 ";
        _vm.ManualPasswordNote = "After the boss";
        await _vm.SaveManualPasswordCommand.ExecuteAsync(null);

        _vm.IsEnteringPassword.Should().BeFalse();
        _emulator.Status.Should().Be(EmulatorStatus.Running);
        (await _passwords.GetAsync("sample:2")).Should().Contain(p => p.Password == "QUEST 2024" && p.Note == "After the boss");
        _vm.Notice.Should().Be("Password saved: QUEST 2024");
    }

    [Fact]
    public async Task F12_saves_a_screenshot_to_the_journal()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _emulator.PressF12();
        await Task.Yield();

        (await _journal.GetImagesAsync("sample:2")).Should().ContainSingle();
        _vm.Notice.Should().Be("Screenshot saved to the journal");
    }

    [Fact]
    public async Task Practice_retry_and_splits_track_personal_bests()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _emulator.FrameCount = 100;
        await _vm.Practice.SetPointCommand.ExecuteAsync(null);

        _emulator.FrameCount = 160;
        _emulator.PressF7(); // segment 1: 60 frames
        _vm.Practice.Segments.Should().ContainSingle().Which.IsPersonalBest.Should().BeTrue();
        _vm.Practice.Segments[0].Time.Should().Be("0:00.99", "60 frames at 60.0988 fps");

        _emulator.FrameCount = 200;
        _emulator.PressF6(); // retry
        await Task.Yield();
        _emulator.RestoredState.Should().Equal(100);
        _vm.Practice.Segments.Should().BeEmpty();
        _vm.Practice.BestForCurrent.Should().Be("Best 0:00.99");

        _emulator.FrameCount = 290; // 90 frames: slower, not a best
        _emulator.PressF7();
        _vm.Practice.Segments[0].IsPersonalBest.Should().BeFalse();
    }

    [Fact]
    public async Task Assist_mode_is_sent_to_the_emulator_and_starts_off()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _emulator.Assist.Should().Be(AssistOptions.Off);

        _vm.Assist.SpeedIndex = 1;
        _emulator.Assist.Should().Be(AssistOptions.Off, "nothing applies until Assist is turned on");

        _vm.Assist.IsEnabled = true;
        _vm.Assist.PaletteIndex = 1;
        _vm.Assist.RemoveFlicker = true;
        _emulator.Assist.Should().Be(new AssistOptions(50, false, true, ColorVision.Deuteranopia));
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

        public event EventHandler? RewindChanged;

        public event EventHandler<TimeSpan>? PlaytimeElapsed;

        public string? GameKey { get; set; }

        public bool UsesZapper { get; set; }

        public bool UsesFourScore { get; set; }

        public bool IsRewinding { get; set; }

        public double RewindSeconds { get; set; }

        public void RaiseNotice(string message) => Notice?.Invoke(this, message);

        public void RaiseRewind(bool rewinding, double seconds)
        {
            IsRewinding = rewinding;
            RewindSeconds = seconds;
            RewindChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RaisePlaytime(TimeSpan time) => PlaytimeElapsed?.Invoke(this, time);

        public long FrameCount { get; set; }

        public AssistOptions Assist { get; private set; } = AssistOptions.Off;

        public byte[]? RestoredState { get; private set; }

        public event EventHandler? ManualPasswordRequested;

        public event EventHandler? ScreenshotRequested;

        public event EventHandler? PracticeRetryRequested;

        public event EventHandler? PracticeSplitRequested;

        public void PressF9() => ManualPasswordRequested?.Invoke(this, EventArgs.Empty);

        public void PressF12() => ScreenshotRequested?.Invoke(this, EventArgs.Empty);

        public void PressF6() => PracticeRetryRequested?.Invoke(this, EventArgs.Empty);

        public void PressF7() => PracticeSplitRequested?.Invoke(this, EventArgs.Empty);

        public void SetAssist(AssistOptions options) => Assist = options;

        public Task<byte[]?> CaptureStateAsync() => Task.FromResult<byte[]?>([(byte)FrameCount]);

        public Task<bool> RestoreStateAsync(byte[] state)
        {
            RestoredState = state;
            return Task.FromResult(true);
        }

        public Task<byte[]?> CaptureScreenshotAsync() => Task.FromResult<byte[]?>([0x89, 0x50]);

        public Task LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (LoadError is not null)
            {
                throw new GameLoadException(LoadError);
            }

            LoadedPath = path;
            GameKey = "sample:2";
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
            GameKey = null;
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
