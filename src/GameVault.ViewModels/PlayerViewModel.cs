using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels;

/// <summary>The Player screen: runs one game and offers pause, reset and back.</summary>
public sealed partial class PlayerViewModel(
    IEmulatorService emulator,
    INavigationService navigation,
    ISettingsStore settingsStore) : ObservableObject
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
            _attached = true;
        }
    }

    private void Detach()
    {
        if (_attached)
        {
            emulator.StatusChanged -= OnStatusChanged;
            _attached = false;
        }
    }

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
