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
    IInputDevicesService devices) : ObservableObject
{
    private bool _attached;
    private bool _pauseWhenInactive = true;

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

    /// <summary>Called when the Player page loads, with the game file chosen by the user.</summary>
    [RelayCommand]
    private async Task StartAsync(string? path)
    {
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
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
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
        }
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
        emulator.Stop();
        Detach();
    }

    private void Attach()
    {
        if (!_attached)
        {
            emulator.StatusChanged += OnStatusChanged;
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
            devices.ControllerDisconnected -= OnControllerDisconnected;
            devices.Changed -= OnDevicesChanged;
            devices.Notice -= OnNotice;
            devices.SetGameRunning(false);
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

    private void OnNotice(object? sender, string notice) => Notice = notice;

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        Status = emulator.Status;
        Title = emulator.GameTitle ?? "Player";
        if (emulator.Status == EmulatorStatus.Faulted)
        {
            ErrorMessage = emulator.FaultMessage;
        }
    }
}
