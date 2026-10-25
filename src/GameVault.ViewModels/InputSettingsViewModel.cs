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

    public InputSettingsViewModel(IInputDevicesService devices, ISettingsStore settings)
    {
        _devices = devices;
        _settings = settings;
        _devices.Changed += (_, _) => Refresh();
        _devices.Notice += (_, notice) => LastNotice = notice;
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

    [RelayCommand]
    private void SwapPlayers() => _devices.SwapPorts(0, 1);

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
