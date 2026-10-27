using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels;

/// <summary>Settings → Input (docs/design/input.md §9): one card per port, plus unassigned controllers.</summary>
public sealed partial class InputSettingsViewModel : ObservableObject
{
    private readonly IInputDevicesService _devices;
    private readonly ISettingsStore _settings;
    private bool _attached;

    public InputSettingsViewModel(IInputDevicesService devices, ISettingsStore settings)
    {
        _devices = devices;
        _settings = settings;
        Refresh();
    }

    public ObservableCollection<InputPortViewModel> Ports { get; } = [];

    public ObservableCollection<UnassignedControllerViewModel> Unassigned { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnassigned))]
    public partial int UnassignedCount { get; private set; }

    public bool HasUnassigned => UnassignedCount > 0;

    [ObservableProperty]
    public partial string? LastNotice { get; private set; }

    public bool TwoPlayerKeyboard
    {
        get => _devices.TwoPlayerKeyboard;
        set
        {
            if (_devices.TwoPlayerKeyboard == value)
            {
                return;
            }

            _devices.TwoPlayerKeyboard = value;
            OnPropertyChanged();
            _ = SaveTwoPlayerKeyboardAsync(value);
        }
    }

    /// <summary>Turbo frames per press-and-release, matching <see cref="TurboSpeedOptions"/>.</summary>
    private static readonly int[] TurboPeriods = [2, 3, 4, 5, 6, 8];

    /// <summary>How fast turbo buttons repeat (input.md §6).</summary>
    public IReadOnlyList<string> TurboSpeedOptions { get; } =
    [
        "30 presses per second",
        "20 presses per second",
        "15 presses per second (default)",
        "12 presses per second",
        "10 presses per second",
        "7.5 presses per second",
    ];

    public int TurboSpeedIndex
    {
        get => Math.Max(0, Array.IndexOf(TurboPeriods, _devices.TurboPeriodFrames));
        set
        {
            if (value < 0 || value >= TurboPeriods.Length || TurboPeriods[value] == _devices.TurboPeriodFrames)
            {
                return;
            }

            _devices.TurboPeriodFrames = TurboPeriods[value];
            OnPropertyChanged();
            _ = SaveTurboAsync(TurboPeriods[value]);
        }
    }

    private async Task SaveTurboAsync(int period)
    {
        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { TurboPeriodFrames = period });
    }

    [RelayCommand]
    private void SwapPlayers() => _devices.SwapPorts(0, 1);

    /// <summary>
    /// Starts listening for device changes (when the page loads). Nothing is subscribed before this: the device
    /// events arrive on the UI thread, and <see cref="Refresh"/> rebuilds <see cref="Ports"/> and
    /// <see cref="Unassigned"/>, so a view model left subscribed after its page is gone would push collection
    /// changes into the torn-down ItemsRepeaters and fail with E_FAIL (SPEC §9.1).
    /// </summary>
    public void Attach()
    {
        if (!_attached)
        {
            _devices.Changed += OnDevicesChanged;
            _devices.Notice += OnNotice;
            _attached = true;
            Refresh(); // devices may have come and gone while the page was away
        }
    }

    /// <summary>Stops listening for device changes (when the page is left).</summary>
    public void Detach()
    {
        if (_attached)
        {
            _devices.Changed -= OnDevicesChanged;
            _devices.Notice -= OnNotice;
            _attached = false;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => Refresh();

    private void OnNotice(object? sender, string notice) => LastNotice = notice;

    internal void UseForPlayer(string sourceId, int port) => _devices.AssignToPort(sourceId, port);

    internal void SetPinned(string sourceId, bool pinned) => _devices.SetPinned(sourceId, pinned);

    private async Task SaveTwoPlayerKeyboardAsync(bool value)
    {
        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { TwoPlayerKeyboard = value });
    }

    private void Refresh()
    {
        Ports.Clear();
        foreach (var port in _devices.Ports)
        {
            Ports.Add(new InputPortViewModel(this, port));
        }

        Unassigned.Clear();
        foreach (var source in _devices.Unassigned)
        {
            Unassigned.Add(new UnassignedControllerViewModel(this, source));
        }

        UnassignedCount = Unassigned.Count;
        OnPropertyChanged(nameof(TwoPlayerKeyboard));
    }
}

/// <summary>A port card: "Player 1" and its sources.</summary>
public sealed class InputPortViewModel(InputSettingsViewModel owner, InputPortInfo port)
{
    public string Title => $"Player {port.Port + 1}";

    public IReadOnlyList<InputSourceViewModel> Sources { get; } =
        port.Sources.Select(s => new InputSourceViewModel(owner, s)).ToList();

    public bool HasNoSources => port.Sources.Count == 0 && port.WaitingFor is null;

    public string WaitingText => port.WaitingFor is { } name ? $"Waiting for {name} to reconnect" : string.Empty;

    public bool IsWaiting => port.WaitingFor is not null;
}

/// <summary>A source on a port card, with "Pin to this player" for controllers.</summary>
public sealed partial class InputSourceViewModel(InputSettingsViewModel owner, InputSourceInfo source)
{
    public string Name => source.Name;

    public bool IsKeyboard => source.IsKeyboard;

    public bool CanPin => !source.IsKeyboard;

    public bool IsPinned => source.IsPinned;

    public string PinLabel => source.IsPinned ? "Unpin" : "Pin to this player";

    /// <summary>Segoe Fluent Icons: keyboard or game controller.</summary>
    public string Glyph => source.IsKeyboard ? "\uE765" : "\uE7FC";

    [RelayCommand]
    private void TogglePin() => owner.SetPinned(source.Id, !source.IsPinned);
}

/// <summary>A connected controller not assigned to any port, with "Use for Player N".</summary>
public sealed partial class UnassignedControllerViewModel(InputSettingsViewModel owner, InputSourceInfo source)
{
    public string Name => source.Name;

    [RelayCommand]
    private void UseForPlayer1() => owner.UseForPlayer(source.Id, 0);

    [RelayCommand]
    private void UseForPlayer2() => owner.UseForPlayer(source.Id, 1);
}
