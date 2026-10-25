using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels;

/// <summary>The Player screen: runs one game and offers pause, reset and back.</summary>
public sealed partial class PlayerViewModel(
    IEmulatorService emulator,
    INavigationService navigation,
    ISettingsStore settingsStore,
    IInputDevicesService devices,
    TimelineViewModel timeline,
    ILibraryService library,
    IPasswordService passwords,
    IJournalService journal,
    AssistViewModel assist,
    PracticeViewModel practice,
    CodesViewModel codes,
    ICheatService cheats,
    NetplayViewModel netplay) : ObservableObject
{
    private byte[]? _passwordScreenshot;
    private bool _updatingDisk;

    /// <summary>True for Famicom Disk System games: the Player shows the disk side menu.</summary>
    [ObservableProperty]
    public partial bool IsDiskSystem { get; private set; }

    /// <summary>"No disk", then every side in the image ("Disk 1, side A", …).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> DiskSlots { get; } = [];

    /// <summary>The selected entry of <see cref="DiskSlots"/>: 0 for no disk, otherwise side + 1.</summary>
    [ObservableProperty]
    public partial int DiskSlotIndex { get; set; }

    /// <summary>"Disk 1, side A", "Disk 1, side B", "Disk 2, side A", … (FDS images store sides in that order).</summary>
    public static string DiskSideName(int side) => $"Disk {side / 2 + 1}, side {(char)('A' + side % 2)}";

    partial void OnDiskSlotIndexChanged(int value)
    {
        if (_updatingDisk || !IsDiskSystem || value < 0)
        {
            return;
        }

        if (value == 0)
        {
            emulator.EjectDisk();
        }
        else
        {
            emulator.InsertDiskSide(value - 1);
        }
    }

    private void OnDiskChanged(object? sender, EventArgs e) => RefreshDisk();

    private void RefreshDisk()
    {
        _updatingDisk = true;
        try
        {
            var count = emulator.DiskSideCount;
            IsDiskSystem = count > 0;
            if (DiskSlots.Count != (count > 0 ? count + 1 : 0))
            {
                DiskSlots.Clear();
                if (count > 0)
                {
                    DiskSlots.Add("No disk");
                    for (var side = 0; side < count; side++)
                    {
                        DiskSlots.Add(DiskSideName(side));
                    }
                }
            }

            DiskSlotIndex = count > 0 ? (emulator.InsertedDiskSide ?? -1) + 1 : -1;
        }
        finally
        {
            _updatingDisk = false;
        }
    }

    /// <summary>Assist mode for this session (SPEC §7.6).</summary>
    public AssistViewModel Assist => assist;

    /// <summary>Game Genie and RAM codes for the running game (SPEC §7.8).</summary>
    public CodesViewModel Codes => codes;

    /// <summary>Two-player netplay on the local network (SPEC §7.7).</summary>
    public NetplayViewModel Netplay => netplay;

    /// <summary>Practice mode: practice point, retry, splits (SPEC §7.6).</summary>
    public PracticeViewModel Practice => practice;

    /// <summary>The "Write down a password" panel is open (F9, SPEC §7.3 manual fallback).</summary>
    [ObservableProperty]
    public partial bool IsEnteringPassword { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveManualPasswordCommand))]
    public partial string ManualPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ManualPasswordNote { get; set; } = string.Empty;

    /// <summary>The save-state timeline flyout (SPEC §7.1).</summary>
    public TimelineViewModel Timeline => timeline;

    /// <summary>"Resume where you left off" (SPEC §7.1): the automatic exit save of the last session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResumeOffer))]
    public partial TimelineItem? ResumeOffer { get; private set; }

    public bool HasResumeOffer => ResumeOffer is not null;

    [ObservableProperty]
    public partial bool IsRewinding { get; private set; }

    /// <summary>The scrub bar: 0–1 of the available history, shown while rewinding.</summary>
    [ObservableProperty]
    public partial double RewindSeconds { get; private set; }

    /// <summary>The game is set up for the Zapper, so the mouse aims (SPEC §7.5).</summary>
    [ObservableProperty]
    public partial bool UsesZapper { get; private set; }

    private bool _attached;
    private bool _pauseWhenInactive = true;
    private bool _pausedForInactive;

    [ObservableProperty]
    public partial string Title { get; private set; } = "Player";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPaused), nameof(PauseButtonLabel), nameof(StatusText))]
    public partial EmulatorStatus Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Player 1's controller was disconnected…" (docs/design/input.md §5); null when nothing waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasControllerMessage))]
    public partial string? ControllerMessage { get; private set; }

    public bool HasControllerMessage => ControllerMessage is not null;

    /// <summary>Short notices such as "8BitDo SN30 Pro is now Player 2".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    public bool IsRunning => Status == EmulatorStatus.Running;

    public bool IsPaused => Status == EmulatorStatus.Paused;

    public bool HasError => ErrorMessage is not null;

    public string PauseButtonLabel => IsPaused ? "Resume" : "Pause";

    public string StatusText => Status switch
    {
        EmulatorStatus.Paused => "Paused",
        EmulatorStatus.Faulted => "Stopped",
        _ => string.Empty,
    };

    /// <summary>
    /// Called when the Player page loads, with a <see cref="GameLaunch"/> or a plain file path.
    /// </summary>
    [RelayCommand]
    private async Task StartAsync(object? parameter)
    {
        var launch = parameter switch
        {
            GameLaunch l => l,
            string p => new GameLaunch(p),
            _ => null,
        };
        var path = launch?.Path;
        Attach();
        ErrorMessage = null;
        if (string.IsNullOrEmpty(path))
        {
            ErrorMessage = "No game was selected.";
            return;
        }

        IsLoading = true;
        try
        {
            _pauseWhenInactive = (await settingsStore.LoadAsync()).PauseWhenInactive;
            await emulator.LoadAsync(path);
        }
        catch (GameLoadException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
            Refresh();
            RefreshDisk();
        }

        if (emulator.GameKey is { } key && emulator.Status == EmulatorStatus.Running)
        {
            emulator.SetAssist(assist.Options); // a new session starts with Assist off
            devices.SetPlayerCount(emulator.UsesFourScore ? 4 : 2);
            timeline.CanSave = true;
            await timeline.ShowAsync(key);
            await cheats.ApplyAsync(key); // the codes the player left on for this game
            await codes.ShowAsync(key, (await library.GetGameAsync(key))?.Title ?? Title);
            netplay.Attach();
            if (launch?.StateId is { } stateId)
            {
                await timeline.Service.LoadAsync(key, stateId);
            }
            else
            {
                ResumeOffer = await timeline.Service.GetResumePointAsync(key);
            }
        }
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (ResumeOffer is { } offer && emulator.GameKey is { } key)
        {
            ResumeOffer = null;
            await timeline.Service.LoadAsync(key, offer.Id);
        }
    }

    [RelayCommand]
    private void DismissResume() => ResumeOffer = null;

    /// <summary>Opens the manual password panel with a screenshot of the password screen (F9).</summary>
    [RelayCommand]
    private async Task CapturePasswordAsync()
    {
        if (emulator.GameKey is null)
        {
            return;
        }

        if (emulator.Status == EmulatorStatus.Running)
        {
            emulator.Pause();
        }

        _passwordScreenshot = await emulator.CaptureScreenshotAsync();
        ManualPassword = string.Empty;
        ManualPasswordNote = string.Empty;
        IsEnteringPassword = true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveManualPassword))]
    private async Task SaveManualPasswordAsync()
    {
        if (emulator.GameKey is { } key)
        {
            await passwords.AddAsync(key, ManualPassword, _passwordScreenshot, string.IsNullOrWhiteSpace(ManualPasswordNote) ? null : ManualPasswordNote.Trim());
            OnNotice(this, $"Password saved: {ManualPassword.Trim()}");
        }

        CloseManualPassword();
    }

    private bool CanSaveManualPassword() => !string.IsNullOrWhiteSpace(ManualPassword);

    [RelayCommand]
    private void CancelManualPassword() => CloseManualPassword();

    private void CloseManualPassword()
    {
        IsEnteringPassword = false;
        _passwordScreenshot = null;
        if (emulator.Status == EmulatorStatus.Paused)
        {
            emulator.Resume();
        }
    }

    /// <summary>Saves the current screen to the game's journal (F12, SPEC §7.4).</summary>
    [RelayCommand]
    private async Task ScreenshotAsync()
    {
        if (emulator.GameKey is { } key && await emulator.CaptureScreenshotAsync() is { } png)
        {
            await journal.AddScreenshotAsync(key, png);
            OnNotice(this, "Screenshot saved to the journal");
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        _pausedForInactive = false;
        if (Status == EmulatorStatus.Running)
        {
            emulator.Pause();
        }
        else if (Status == EmulatorStatus.Paused)
        {
            emulator.Resume();
        }
    }

    [RelayCommand]
    private void Reset() => emulator.Reset();

    /// <summary>Pauses when the window loses focus, if the user's settings ask for it.</summary>
    public void OnWindowDeactivated()
    {
        if (_pauseWhenInactive && Status == EmulatorStatus.Running)
        {
            emulator.Pause();
            _pausedForInactive = true;
        }
    }

    /// <summary>
    /// Resumes when the window is active again, but only if <see cref="OnWindowDeactivated"/> paused the
    /// game: a game the player paused stays paused.
    /// </summary>
    public void OnWindowActivated()
    {
        if (_pausedForInactive && Status == EmulatorStatus.Paused)
        {
            emulator.Resume();
        }

        _pausedForInactive = false;
    }

    [RelayCommand]
    private void Back()
    {
        Stop();
        if (navigation.CanGoBack)
        {
            navigation.GoBack();
        }
        else
        {
            navigation.NavigateTo(AppPage.Library);
        }
    }

    /// <summary>Called when the Player page is left: stops the game and detaches from the service.</summary>
    public void Stop()
    {
        if (emulator.Status is EmulatorStatus.Running or EmulatorStatus.Paused && emulator.GameKey is not null)
        {
            // Queued before the stop, so the emulation thread captures the state first (SPEC §7.1).
            _ = timeline.Service.SaveOnExitAsync();
        }

        emulator.Stop();
        timeline.Detach();
        codes.Detach();
        netplay.Detach();
        Detach();
    }

    private void Attach()
    {
        if (!_attached)
        {
            emulator.StatusChanged += OnStatusChanged;
            emulator.Notice += OnNotice;
            emulator.RewindChanged += OnRewindChanged;
            emulator.DiskChanged += OnDiskChanged;
            emulator.PlaytimeElapsed += OnPlaytimeElapsed;
            emulator.ManualPasswordRequested += OnManualPasswordRequested;
            emulator.ScreenshotRequested += OnScreenshotRequested;
            emulator.PracticeRetryRequested += OnPracticeRetry;
            emulator.PracticeSplitRequested += OnPracticeSplit;
            passwords.Captured += OnPasswordCaptured;
            timeline.Service.Notice += OnNotice;
            devices.ControllerDisconnected += OnControllerDisconnected;
            devices.Changed += OnDevicesChanged;
            devices.Notice += OnNotice;
            devices.SetGameRunning(true);
            _attached = true;
        }
    }

    private void Detach()
    {
        if (_attached)
        {
            emulator.StatusChanged -= OnStatusChanged;
            emulator.Notice -= OnNotice;
            emulator.RewindChanged -= OnRewindChanged;
            emulator.DiskChanged -= OnDiskChanged;
            emulator.PlaytimeElapsed -= OnPlaytimeElapsed;
            emulator.ManualPasswordRequested -= OnManualPasswordRequested;
            emulator.ScreenshotRequested -= OnScreenshotRequested;
            emulator.PracticeRetryRequested -= OnPracticeRetry;
            emulator.PracticeSplitRequested -= OnPracticeSplit;
            passwords.Captured -= OnPasswordCaptured;
            timeline.Service.Notice -= OnNotice;
            devices.ControllerDisconnected -= OnControllerDisconnected;
            devices.Changed -= OnDevicesChanged;
            devices.Notice -= OnNotice;
            devices.SetGameRunning(false);
            devices.SetPlayerCount(2);
            _attached = false;
        }
    }

    // input.md §5: pause, explain, and don't resume on our own after the pad comes back.
    private void OnControllerDisconnected(object? sender, ControllerDisconnectedEventArgs e)
    {
        emulator.Pause();
        ControllerMessage =
            $"Player {e.Port + 1}'s controller was disconnected. Reconnect it, or press a button on another controller to take over.";
        Refresh();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (ControllerMessage is not null && devices.Ports.All(p => p.WaitingFor is null))
        {
            ControllerMessage = null;
        }
    }

    private void OnNotice(object? sender, string notice)
    {
        Notice = null; // re-opens the notice bar if the player closed the previous one
        Notice = notice;
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    private void OnManualPasswordRequested(object? sender, EventArgs e) => CapturePasswordCommand.Execute(null);

    private void OnScreenshotRequested(object? sender, EventArgs e) => ScreenshotCommand.Execute(null);

    private void OnPracticeRetry(object? sender, EventArgs e)
    {
        if (practice.RetryCommand.CanExecute(null))
        {
            practice.RetryCommand.Execute(null);
        }
    }

    private void OnPracticeSplit(object? sender, EventArgs e)
    {
        if (practice.SplitCommand.CanExecute(null))
        {
            practice.SplitCommand.Execute(null);
        }
    }

    private void OnPasswordCaptured(object? sender, string password) => OnNotice(this, $"Password saved: {password}");

    private void OnRewindChanged(object? sender, EventArgs e)
    {
        IsRewinding = emulator.IsRewinding;
        RewindSeconds = emulator.RewindSeconds;
        if (IsRewinding)
        {
            ResumeOffer = null; // the player has moved on
        }
    }

    private async void OnPlaytimeElapsed(object? sender, TimeSpan played)
    {
        if (emulator.GameKey is { } key)
        {
            await library.AddPlaytimeAsync(key, played);
        }
    }

    private void Refresh()
    {
        Status = emulator.Status;
        Title = emulator.GameTitle ?? "Player";
        UsesZapper = emulator.UsesZapper;
        if (emulator.Status == EmulatorStatus.Faulted)
        {
            ErrorMessage = emulator.FaultMessage;
        }
    }
}
