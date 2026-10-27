using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameVault.Core.Systems;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels;

/// <summary>
/// Settings → Input (docs/design/input.md §9): one card per port, plus unassigned controllers, and Remap…. The Player
/// shows the same cards (GV-003), where remaps can also be made for the running game only.
/// </summary>
public sealed partial class InputSettingsViewModel : ObservableObject
{
    private readonly IInputDevicesService _devices;
    private readonly ISettingsStore _settings;
    private bool _attached;

    public InputSettingsViewModel(IInputDevicesService devices, ISettingsStore settings)
    {
        _devices = devices;
        _settings = settings;
        Remap = new RemapViewModel(devices, settings);
        Refresh();
    }

    /// <summary>
    /// Starts listening for device changes (when the page loads). Nothing is subscribed before this: the device
    /// events arrive on the UI thread, and <see cref="Refresh"/> rebuilds <see cref="Ports"/> and
    /// <see cref="Unassigned"/>, so a view model left subscribed after its page is gone would push collection
    /// changes into the torn-down ItemsRepeaters and fail with E_FAIL (SPEC §9.1, GV-021).
    /// </summary>
    public void Attach()
    {
        if (!_attached)
        {
            _devices.Changed += OnDevicesChanged;
            _devices.Notice += OnNotice;
            Remap.Attach();
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
            Remap.Detach();
            _attached = false;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => Refresh();

    private void OnNotice(object? sender, string notice) => LastNotice = notice;

    public ObservableCollection<InputPortViewModel> Ports { get; } = [];

    public ObservableCollection<UnassignedControllerViewModel> Unassigned { get; } = [];

    /// <summary>The Remap… editor (GV-001), open for one source at a time.</summary>
    public RemapViewModel Remap { get; }

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

    /// <summary>The running game, in the Player: its remaps can be kept for it alone, and its system is shown first.</summary>
    public void SetGame(string? gameKey, string? title, GameSystem? system) => Remap.SetGame(gameKey, title, system);

    private async Task SaveTurboAsync(int period)
    {
        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { TurboPeriodFrames = period });
    }

    [RelayCommand]
    private void SwapPlayers() => _devices.SwapPorts(0, 1);

    internal void Swap(int a, int b) => _devices.SwapPorts(a, b);

    internal void UseForPlayer(string sourceId, int port) => _devices.AssignToPort(sourceId, port);

    internal void SetPinned(string sourceId, bool pinned) => _devices.SetPinned(sourceId, pinned);

    internal void OpenRemap(InputSourceInfo source, int port) => Remap.Open(source, port);

    private async Task SaveTwoPlayerKeyboardAsync(bool value)
    {
        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { TwoPlayerKeyboard = value });
    }

    private void Refresh()
    {
        var ports = _devices.Ports;
        Ports.Clear();
        foreach (var port in ports)
        {
            Ports.Add(new InputPortViewModel(this, port, ports.Count));
        }

        Unassigned.Clear();
        foreach (var source in _devices.Unassigned)
        {
            Unassigned.Add(new UnassignedControllerViewModel(this, source, ports.Count));
        }

        UnassignedCount = Unassigned.Count;
        OnPropertyChanged(nameof(TwoPlayerKeyboard));
    }
}

/// <summary>A port card: "Player 1" and its sources, with "Swap with Player N" for the other ports.</summary>
public sealed class InputPortViewModel(InputSettingsViewModel owner, InputPortInfo port, int portCount)
{
    public string Title => $"Player {port.Port + 1}";

    public IReadOnlyList<InputSourceViewModel> Sources { get; } =
        port.Sources.Select(s => new InputSourceViewModel(owner, s, port.Port)).ToList();

    public bool HasNoSources => port.Sources.Count == 0 && port.WaitingFor is null;

    public string WaitingText => port.WaitingFor is { } name ? $"Waiting for {name} to reconnect" : string.Empty;

    public bool IsWaiting => port.WaitingFor is not null;

    /// <summary>Swapping with every other port (players 3 and 4 too while a Four Score game runs; GV-002).</summary>
    public IReadOnlyList<PlayerChoiceViewModel> SwapChoices { get; } =
        Enumerable.Range(0, portCount).Where(p => p != port.Port)
            .Select(p => new PlayerChoiceViewModel($"Swap with Player {p + 1}", () => owner.Swap(port.Port, p))).ToList();
}

/// <summary>A source on a port card, with "Pin to this player" for controllers and "Remap…" for every source.</summary>
public sealed partial class InputSourceViewModel(InputSettingsViewModel owner, InputSourceInfo source, int port)
{
    public string Name => source.Name;

    public bool IsKeyboard => source.IsKeyboard;

    public bool CanPin => !source.IsKeyboard;

    public bool IsPinned => source.IsPinned;

    public string PinLabel => source.IsPinned ? "Unpin" : "Pin to this player";

    /// <summary>Segoe Fluent Icons: keyboard or game controller.</summary>
    public string Glyph => source.IsKeyboard ? "" : "";

    [RelayCommand]
    private void TogglePin() => owner.SetPinned(source.Id, !source.IsPinned);

    [RelayCommand]
    private void Remap() => owner.OpenRemap(source, port);
}

/// <summary>A connected controller not assigned to any port, with "Use for Player N" for every port (GV-002).</summary>
public sealed partial class UnassignedControllerViewModel(InputSettingsViewModel owner, InputSourceInfo source, int portCount)
{
    public string Name => source.Name;

    public IReadOnlyList<PlayerChoiceViewModel> Players { get; } =
        Enumerable.Range(0, portCount).Select(p => new PlayerChoiceViewModel($"Use for Player {p + 1}", () => owner.UseForPlayer(source.Id, p))).ToList();

    [RelayCommand]
    private void UseForPlayer1() => owner.UseForPlayer(source.Id, 0);

    [RelayCommand]
    private void UseForPlayer2() => owner.UseForPlayer(source.Id, 1);
}

/// <summary>A button such as "Use for Player 3" or "Swap with Player 4".</summary>
public sealed partial class PlayerChoiceViewModel(string label, Action choose)
{
    public string Label => label;

    [RelayCommand]
    private void Choose() => choose();
}

/// <summary>
/// Remap… (SPEC §5.4, input.md §6; GV-001, GV-018): one row per button of the chosen system's pad, named as that pad
/// names it. "Change" waits for the key or pad button to use; one input does one thing, so whatever it did before is
/// left unbound. Remaps are per device (the keyboard, or a controller model), per player, per system, and optionally
/// per game. Every change is saved and applied immediately.
/// </summary>
public sealed partial class RemapViewModel : ObservableObject
{
    /// <summary>Keys kept for GameVault's hotkeys (input.md §8; <c>KeyboardMapping.Hotkeys</c>), which can't be remapped.</summary>
    public static IReadOnlySet<int> ReservedKeys { get; } = new HashSet<int>
    {
        0x1B, 0x08, 0x09, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x7A, 0x7B,
    };

    private readonly IInputDevicesService _devices;
    private readonly ISettingsStore _settings;
    private string _device = IInputDevicesService.KeyboardId;
    private string _sourceId = IInputDevicesService.KeyboardId;
    private int _player;
    private string? _gameKey;
    private bool _loading;

    public RemapViewModel(IInputDevicesService devices, ISettingsStore settings)
    {
        _devices = devices;
        _settings = settings;
    }

    private bool _attached;

    /// <summary>Listens for captured pad buttons while the page is up (see <see cref="InputSettingsViewModel.Attach"/>).</summary>
    internal void Attach()
    {
        if (!_attached)
        {
            _devices.InputCaptured += OnCaptured;
            _attached = true;
        }
    }

    internal void Detach()
    {
        if (_attached)
        {
            CancelListening();
            _devices.InputCaptured -= OnCaptured;
            _attached = false;
        }
    }

    public IReadOnlyList<string> SystemOptions { get; } = InputActions.Systems.Select(InputActions.SystemName).ToList();

    public IReadOnlyList<string> PresetOptions => InputActions.KeyboardPresets;

    public ObservableCollection<RemapRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsKeyboard { get; private set; }

    [ObservableProperty]
    public partial int SystemIndex { get; set; }

    /// <summary>True in the Player: the remap can be kept for the running game only.</summary>
    [ObservableProperty]
    public partial bool HasGame { get; private set; }

    [ObservableProperty]
    public partial string GameScopeLabel { get; private set; } = string.Empty;

    /// <summary>Change the running game's own remap, rather than the one for every game.</summary>
    [ObservableProperty]
    public partial bool ThisGameOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening))]
    public partial RemapRowViewModel? Listening { get; private set; }

    public bool IsListening => Listening is not null;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial int PresetIndex { get; set; }

    private InputSystem System => InputActions.Systems[Math.Clamp(SystemIndex, 0, InputActions.Systems.Count - 1)];

    private string? Scope => HasGame && ThisGameOnly ? _gameKey : null;

    private GameSystem? _gameSystem;

    internal void SetGame(string? gameKey, string? title, GameSystem? system)
    {
        _gameKey = gameKey;
        _gameSystem = system;
        HasGame = gameKey is not null;
        GameScopeLabel = title is null ? "This game only" : $"Only for {title}";
        if (!HasGame)
        {
            ThisGameOnly = false;
        }
    }

    /// <summary>Opens the editor for a source on a port, showing the running game's system first (or the NES).</summary>
    public void Open(InputSourceInfo source, int player)
    {
        _loading = true;
        _device = source.IsKeyboard ? IInputDevicesService.KeyboardId : source.Model ?? source.Id;
        _sourceId = source.Id;
        _player = player;
        IsKeyboard = source.IsKeyboard;
        Title = $"Remap {source.Name} for Player {player + 1}";
        SystemIndex = Math.Max(0, InputActions.Systems.ToList().IndexOf(SystemFor(_gameSystem)));
        ThisGameOnly = false;
        PresetIndex = 0;
        Message = null;
        _loading = false;
        IsOpen = true;
        Rebuild();
    }

    [RelayCommand]
    private void Close()
    {
        CancelListening();
        IsOpen = false;
    }

    partial void OnSystemIndexChanged(int value)
    {
        if (!_loading)
        {
            CancelListening();
            Rebuild();
        }
    }

    partial void OnThisGameOnlyChanged(bool value)
    {
        if (!_loading)
        {
            CancelListening();
            Rebuild();
        }
    }

    internal void StartListening(RemapRowViewModel row)
    {
        Listening = row;
        Message = IsKeyboard ? $"Press the key for {row.Label}. Esc cancels." : $"Press the button for {row.Label} on the controller.";
        _devices.CaptureMode = !IsKeyboard;
    }

    /// <summary>Stops waiting for a key or button.</summary>
    public void CancelListening()
    {
        if (Listening is not null)
        {
            Listening = null;
            Message = null;
        }

        _devices.CaptureMode = false;
    }

    /// <summary>A key pressed while the editor waits for one (the page forwards it). True when it was used.</summary>
    public bool CaptureKey(int virtualKey)
    {
        if (Listening is not { } row || !IsKeyboard)
        {
            return false;
        }

        if (virtualKey == 0x1B)
        {
            CancelListening();
            return true;
        }

        if (ReservedKeys.Contains(virtualKey))
        {
            Message = $"{InputCodes.KeyName(virtualKey)} is one of GameVault's own keys (save, load, rewind…). Choose another.";
            return true;
        }

        Bind(row, InputCodes.Key(virtualKey));
        return true;
    }

    private void OnCaptured(object? sender, CapturedInput captured)
    {
        if (Listening is { } row && !IsKeyboard && captured.SourceId == _sourceId)
        {
            if (captured.Input == InputCodes.Pad("Guide"))
            {
                Message = "The Guide button pauses the game and can't be remapped. Press another button.";
                return;
            }

            Bind(row, captured.Input);
        }
    }

    internal void Clear(RemapRowViewModel row) => Bind(row, InputCodes.None);

    private void Bind(RemapRowViewModel row, string input)
    {
        var set = _devices.Remaps.Bind(_device, _player, System, Scope, row.Id, input);
        var unbound = Rows.Where(r => r != row && r.Input == input && input != InputCodes.None).Select(r => r.Label).ToList();
        CancelListening();
        Apply(set);
        Message = unbound.Count > 0 ? $"{InputCodes.Describe(input)} was {string.Join(", ", unbound)}; that's unassigned now." : null;
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        CancelListening();
        Apply(_devices.Remaps.Reset(_device, _player, System, Scope));
        Message = Scope is null ? "Back to the standard layout for this system." : "This game uses the layout for every game again.";
    }

    /// <summary>Keyboard presets (SPEC §5.4): the standard keys, or one-handed layouts for every system.</summary>
    [RelayCommand]
    private void ApplyPreset()
    {
        if (!IsKeyboard)
        {
            return;
        }

        CancelListening();
        Apply(_devices.Remaps.ApplyKeyboardPreset(_player, PresetIndex));
        Message = PresetIndex == 0 ? "Standard keys on every system." : $"{PresetOptions[PresetIndex]} on every system.";
    }

    private void Apply(InputRemapSet set)
    {
        _devices.Remaps = set;
        Rebuild();
        _ = SaveAsync(set);
    }

    private async Task SaveAsync(InputRemapSet set)
    {
        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { InputRemaps = set.ToJson() });
    }

    private void Rebuild()
    {
        var system = System;
        var effective = _devices.Remaps.Effective(_device, _player, system, Scope);
        var defaults = InputRemapSet.Empty.Effective(_device, _player, system, null);
        Rows.Clear();
        foreach (var action in InputActions.For(system))
        {
            if (!IsKeyboard && action.KeyboardOnly)
            {
                continue; // the pad does these with its sticks
            }

            var input = !IsKeyboard && action.PadFixed ? action.DefaultPad : effective.GetValueOrDefault(action.Id);
            var changeable = IsKeyboard || !action.PadFixed;
            Rows.Add(new RemapRowViewModel(this, action, input ?? InputCodes.None, changeable, changeable && input != defaults.GetValueOrDefault(action.Id)));
        }
    }

    private static InputSystem SystemFor(GameSystem? system) => system switch
    {
        GameSystem.Sg1000 or GameSystem.MasterSystem => InputSystem.Sega,
        GameSystem.Genesis => InputSystem.Genesis,
        GameSystem.Snes => InputSystem.Snes,
        GameSystem.PlayStation => InputSystem.PlayStation,
        GameSystem.Nintendo64 => InputSystem.Nintendo64,
        GameSystem.PlayStation2 => InputSystem.PlayStation2,
        _ => InputSystem.Nes,
    };
}

/// <summary>One button in Remap…: its name on the system's pad, what presses it, and Change / Clear.</summary>
public sealed partial class RemapRowViewModel(RemapViewModel owner, InputAction action, string input, bool isChangeable, bool isChanged)
{
    public string Id => action.Id;

    public string Label => action.Label;

    public string Input => input;

    public string BindingText => InputCodes.Describe(input);

    /// <summary>"Change B", for screen readers.</summary>
    public string ChangeLabel => $"Change {action.Label}";

    /// <summary>False for what a pad does with a trigger or stick (shown, but fixed).</summary>
    public bool IsChangeable => isChangeable;

    /// <summary>Differs from the system's standard layout.</summary>
    public bool IsChanged => isChanged;

    [RelayCommand]
    private void Change() => owner.StartListening(this);

    [RelayCommand]
    private void Clear() => owner.Clear(this);
}
