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
    private readonly SampleCheatService _cheats = new();
    private readonly InMemoryWindowService _window = new();
    private readonly InMemorySessionLog _sessions = new();
    private readonly PlayerViewModel _vm;

    public PlayerViewModelTests()
    {
        _navigation.NavigateTo(AppPage.Library);
        _navigation.NavigateTo(AppPage.Player, "game.nes");
        _vm = new PlayerViewModel(
            _emulator, _navigation, _settings, _devices, new TimelineViewModel(_timeline), _library, _passwords, _journal,
            new AssistViewModel(_emulator), new PracticeViewModel(_emulator, _records),
            new CodesViewModel(_cheats, new SampleCodeFilePicker()), _cheats,
            new NetplayViewModel(_emulator, new InMemoryClipboard()), _window,
            new RecapViewModel(_sessions, _journal, _passwords, _timeline), _sessions,
            new ChallengesViewModel(_emulator, new InMemoryChallengeFiles()),
            new PlayLimitViewModel(_emulator, new InMemoryFamilyService(withSamples: false), _timeline),
            picker: new SampleGameFilePicker());
    }

    [Fact]
    public async Task Full_screen_toggles_and_esc_or_leaving_the_player_ends_it()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.IsWindowed.Should().BeTrue();

        _vm.ToggleFullScreenCommand.Execute(null);
        _vm.IsFullScreen.Should().BeTrue();
        _vm.FullScreenButtonLabel.Should().Be("Exit full screen");

        _vm.ExitFullScreen().Should().BeTrue("Esc leaves full screen first");
        _vm.ExitFullScreen().Should().BeFalse("then Esc pauses as usual");

        _vm.ToggleFullScreenCommand.Execute(null);
        _vm.BackCommand.Execute(null);
        _window.IsFullScreen.Should().BeFalse("leaving the Player returns to the normal window");
    }

    [Fact]
    public async Task Challenges_are_offered_only_for_games_with_a_memory_map()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.ShowChallenges.Should().BeFalse("without a memory map there's nothing to make or take (GV-017)");
        _vm.Stop();

        _emulator.HasMemoryMap = true;
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.ShowChallenges.Should().BeTrue();
    }

    [Fact]
    public async Task The_player_has_the_port_cards_with_remaps_for_the_running_game()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _devices.GameKey.Should().Be(_emulator.GameKey);
        _vm.Controllers.Ports.Should().NotBeEmpty();
        _vm.Controllers.Ports[0].Sources[0].RemapCommand.Execute(null);
        _vm.Controllers.Remap.HasGame.Should().BeTrue("remaps can be kept for this game (GV-003)");

        _vm.Stop();
        _devices.GameKey.Should().BeNull();
    }

    [Fact]
    public async Task The_stat_overlay_shows_the_games_values_and_is_remembered_per_game()
    {
        _emulator.HasMemoryMap = true;
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _vm.HasMemoryMap.Should().BeTrue();
        _vm.ShowStats.Should().BeFalse("off until the player turns it on");

        _emulator.SetStats(new StatValue("Health", "3 of 16"));
        _vm.StatsVisible.Should().BeFalse();

        _vm.ShowStats = true;
        _vm.Stats.Should().Equal(new StatValue("Health", "3 of 16"));
        _vm.StatsVisible.Should().BeTrue();

        _emulator.SetStats(new StatValue("Health", "2 of 16"), new StatValue("Boss", "12"));
        _vm.Stats.Select(s => s.Text).Should().Equal("2 of 16", "12");

        await Task.Delay(50);
        (await _settings.LoadAsync()).StatOverlayGames.Should().Be(_emulator.GameKey);
    }

    [Fact]
    public async Task Games_can_start_in_full_screen()
    {
        await _settings.SaveAsync(new AppSettings { PlayFullScreen = true });

        await _vm.StartCommand.ExecuteAsync("game.nes");

        _window.IsFullScreen.Should().BeTrue();
    }

    [Fact]
    public async Task Starting_a_game_turns_on_the_codes_left_on_for_it()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _cheats.Applied.Should().ContainSingle().Which.GameKey.Should().Be(_emulator.GameKey);
        _cheats.Applied[0].Codes.Select(c => c.ToString()).Should().Equal("SXIOPO");
        _vm.Codes.Items.Should().HaveCount(2);
        _vm.Codes.AnyEnabled.Should().BeTrue("the Player shows a Codes on badge");
    }

    [Fact]
    public async Task Personal_bests_are_not_recorded_while_codes_are_on()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");
        _emulator.CheatsActive = true;
        await _vm.Practice.SetPointCommand.ExecuteAsync(null);
        _emulator.FrameCount += 60;

        _vm.Practice.SplitCommand.Execute(null);

        _vm.Practice.Segments.Should().ContainSingle().Which.IsPersonalBest.Should().BeFalse();
        _records.Bests(_emulator.GameKey!).Should().BeEmpty();
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
    public async Task Disk_system_games_offer_their_sides()
    {
        _emulator.DiskSideCount = 2;
        _emulator.InsertedDiskSide = 0;

        await _vm.StartCommand.ExecuteAsync("disk.fds");

        _vm.IsDiskSystem.Should().BeTrue();
        _vm.DiskSlots.Should().Equal("No disk", "Disk 1, side A", "Disk 1, side B");
        _vm.DiskSlotIndex.Should().Be(1);

        _vm.DiskSlotIndex = 2;
        _emulator.DiskCommands.Should().Equal("insert 1");

        _vm.DiskSlotIndex = 0;
        _emulator.DiskCommands.Should().Equal("insert 1", "eject");
        _vm.DiskSlotIndex.Should().Be(0);
    }

    [Fact]
    public async Task Multi_disc_playstation_games_offer_their_discs()
    {
        _emulator.DiskSideCount = 2;
        _emulator.DiscNames = ["Saga (Disc 1)", "Saga (Disc 2)"];
        _emulator.InsertedDiskSide = 0;

        await _vm.StartCommand.ExecuteAsync("saga.m3u");

        _vm.IsDiskSystem.Should().BeTrue();
        _vm.DiskSlots.Should().Equal("No disc (shell open)", "Disc 1: Saga (Disc 1)", "Disc 2: Saga (Disc 2)");
        _vm.DiskSlotIndex = 2;
        _emulator.DiskCommands.Should().Equal("insert 1");
    }

    [Fact]
    public async Task Apple_ii_games_offer_a_disk_menu_for_both_drives()
    {
        _emulator.System = GameVault.Core.Systems.GameSystem.AppleII;
        _emulator.AppleDrives[0] = "Game";

        await _vm.StartCommand.ExecuteAsync("game.dsk");

        _vm.IsAppleGame.Should().BeTrue();
        _vm.IsDiskSystem.Should().BeFalse();
        _vm.Drive1Label.Should().Be("Drive 1: Game");
        _vm.Drive2Label.Should().Be("Drive 2: empty");
        _vm.EjectDrive2Command.CanExecute(null).Should().BeFalse();

        await _vm.InsertDrive2Command.ExecuteAsync(null);
        _vm.Drive2Label.Should().Be("Drive 2: Sample Disk 2");
        _vm.EjectDrive2Command.CanExecute(null).Should().BeTrue();

        _vm.EjectDrive1Command.Execute(null);
        _vm.Drive1Label.Should().Be("Drive 1: empty");
        _vm.Drive1HasDisk.Should().BeFalse();
    }

    [Fact]
    public async Task Cartridge_games_have_no_disk_menu()
    {
        await _vm.StartCommand.ExecuteAsync("game.nes");

        _vm.IsDiskSystem.Should().BeFalse();
        _vm.DiskSlots.Should().BeEmpty();
        PlayerViewModel.DiskSideName(3).Should().Be("Disk 2, side B");
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

    [Fact]
    public async Task The_daily_limit_warns_then_saves_pauses_and_needs_the_pin()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero));
        var family = new InMemoryFamilyService(clock);
        family.SwitchTo("sample-kid"); // 60 minutes a day
        var limit = new PlayLimitViewModel(_emulator, family, _timeline);
        await _vm.StartCommand.ExecuteAsync("game.nes");
        limit.Attach();

        _emulator.RaisePlaytime(TimeSpan.FromMinutes(54));
        limit.HasWarning.Should().BeFalse();
        _emulator.RaisePlaytime(TimeSpan.FromMinutes(1));
        limit.Warning.Should().Be("5 minutes of play left today.");

        _emulator.RaisePlaytime(TimeSpan.FromMinutes(5));
        limit.IsLimitReached.Should().BeTrue();
        limit.LimitMessage.Should().StartWith("That's 1 hour of play for today, Sam.");
        _emulator.Status.Should().Be(EmulatorStatus.Paused);
        (await _timeline.GetResumePointAsync("sample:2")).Should().NotBeNull("the resume point was saved");

        _emulator.Resume();
        _emulator.Status.Should().Be(EmulatorStatus.Paused, "unpausing can't get past the limit");

        limit.PinEntry = "9999";
        limit.ContinueCommand.Execute(null);
        limit.PinMessage.Should().Be("That PIN isn't right.");
        _emulator.Status.Should().Be(EmulatorStatus.Paused);

        limit.PinEntry = "1234";
        limit.ContinueCommand.Execute(null);
        limit.IsLimitReached.Should().BeFalse();
        _emulator.Status.Should().Be(EmulatorStatus.Running);
        _emulator.RaisePlaytime(TimeSpan.FromMinutes(30));
        limit.IsLimitReached.Should().BeFalse("a parent let them keep going today");

        clock.Now = clock.Now.AddDays(1);
        limit.Remaining.Should().Be(TimeSpan.FromMinutes(60), "a new day starts over");
        limit.Detach();
    }

    [Fact]
    public async Task Profiles_without_a_limit_are_never_stopped()
    {
        var limit = new PlayLimitViewModel(_emulator, new InMemoryFamilyService(), _timeline);
        await _vm.StartCommand.ExecuteAsync("game.nes");
        limit.Attach();

        _emulator.RaisePlaytime(TimeSpan.FromHours(5));
        limit.Remaining.Should().BeNull();
        limit.IsLimitReached.Should().BeFalse();
        _emulator.Status.Should().Be(EmulatorStatus.Running);
        limit.Detach();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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

        public bool CheatsActive { get; set; }

        public bool HasMemoryMap { get; set; }

        public IReadOnlyList<StatValue> StatValues { get; private set; } = [];

        public event EventHandler? StatValuesChanged;

        public void SetStats(params StatValue[] values)
        {
            StatValues = values;
            StatValuesChanged?.Invoke(this, EventArgs.Empty);
        }

        public int DiskSideCount { get; set; }

        public IReadOnlyList<string> DiscNames { get; set; } = [];

        public int? InsertedDiskSide { get; set; }

        public event EventHandler? DiskChanged;

        public List<string> DiskCommands { get; } = [];

        public void InsertDiskSide(int side)
        {
            DiskCommands.Add($"insert {side}");
            InsertedDiskSide = side;
            DiskChanged?.Invoke(this, EventArgs.Empty);
        }

        public GameVault.Core.Systems.GameSystem? System { get; set; }

        public string?[] AppleDrives { get; } = [null, null];

        public IReadOnlyList<string?> AppleDriveNames => AppleDrives;

        public Task InsertAppleDiskAsync(int drive, string path, CancellationToken cancellationToken = default)
        {
            AppleDrives[drive] = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            DiskChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void EjectAppleDisk(int drive)
        {
            AppleDrives[drive] = null;
            DiskChanged?.Invoke(this, EventArgs.Empty);
        }

        public void EjectDisk()
        {
            DiskCommands.Add("eject");
            InsertedDiskSide = null;
            DiskChanged?.Invoke(this, EventArgs.Empty);
        }

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
