using GameVault.Core.Input;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;

namespace GameVault.Platform.Input.Controllers;

/// <summary>
/// Game controllers end to end (docs/design/input.md §3–§9): port assignment and hot-plug through
/// <see cref="PortAssignmentPolicy"/>, the positional default mapping with turbo and the left stick, UI
/// navigation when the game doesn't have focus, and the pause hotkeys. Backend events arrive on the
/// backend's thread; this class's own events are raised on the thread that created it (the UI thread).
/// </summary>
public sealed class ControllerManager : IInputDevicesService, IControllerEvents, IDisposable
{
    /// <summary>Hold Back + Start this long to pause when Guide is unavailable (input.md §7).</summary>
    public const int BackStartHoldMs = 1000;

    private const int RepeatDelayMs = 400;
    private const int RepeatIntervalMs = 120;
    private const int SourceOffset = 1000;

    private readonly Lock _gate = new();
    private readonly InputService _input;
    private readonly IControllerBackend _backend;
    private readonly ISettingsStore? _settings;
    private readonly SynchronizationContext? _context;
    private readonly InputDevicesModel _model = new(new PortAssignmentPolicy());
    private readonly Dictionary<int, PadState> _pads = [];
    private bool _started;

    public ControllerManager(InputService input, IControllerBackend backend, ISettingsStore? settings = null)
    {
        _input = input;
        _backend = backend;
        _settings = settings;
        _context = SynchronizationContext.Current;
    }

    /// <summary>Set by the app: pads are ignored while the window is in the background (input.md §7).</summary>
    public bool AppActive { get; set; } = true;

    /// <summary>Set by the game view: assigned pads drive the game only while it has focus (input.md §7).</summary>
    public bool GameHasFocus { get; set; }

    public event EventHandler? Changed;

    public event EventHandler<string>? Notice;

    public event EventHandler<ControllerDisconnectedEventArgs>? ControllerDisconnected;

    /// <summary>A pad asked for UI navigation.</summary>
    public event EventHandler<NavigationAction>? Navigate;

    /// <summary>Guide, or Back + Start held, on an assigned pad.</summary>
    public event EventHandler? PauseRequested;

    public IReadOnlyList<InputPortInfo> Ports
    {
        get
        {
            lock (_gate)
            {
                return _model.BuildPorts();
            }
        }
    }

    public IReadOnlyList<InputSourceInfo> Unassigned
    {
        get
        {
            lock (_gate)
            {
                return _model.BuildUnassigned();
            }
        }
    }

    public bool TwoPlayerKeyboard
    {
        get => _model.TwoPlayerKeyboard;
        set
        {
            lock (_gate)
            {
                _model.TwoPlayerKeyboard = value;
            }

            _input.SetKeyboardMapping(value ? KeyboardMapping.CreateTwoPlayer() : KeyboardMapping.CreateDefault());
            Raise(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <summary>Loads pins and the keyboard preset, then starts the backend. Call once the window is up.</summary>
    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (_settings is not null)
        {
            var settings = await _settings.LoadAsync();
            lock (_gate)
            {
                _model.Policy.LoadPins(InputDevicesModel.ParsePins(settings.ControllerPins));
            }

            if (settings.TwoPlayerKeyboard)
            {
                TwoPlayerKeyboard = true;
            }
        }

        _backend.Start(this);
    }

    public void SetGameRunning(bool running)
    {
        lock (_gate)
        {
            _model.Policy.GameRunning = running;
            if (!running)
            {
                _model.Policy.ClearReservations();
            }
        }

        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void SetPlayerCount(int count)
    {
        IReadOnlyList<string> notices;
        lock (_gate)
        {
            notices = _model.Policy.SetPortCount(count);
            ReleaseAllPads();
        }

        foreach (var notice in notices)
        {
            RaiseNotice(notice);
        }

        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void SwapPorts(int a, int b)
    {
        lock (_gate)
        {
            _model.Policy.Swap(a, b);
            ReleaseAllPads();
        }

        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void AssignToPort(string sourceId, int port)
    {
        string? notice;
        lock (_gate)
        {
            notice = _model.AssignToPort(sourceId, port);
            ReleaseAllPads();
        }

        RaiseNotice(notice);
        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void SetPinned(string sourceId, bool pinned)
    {
        string pins;
        lock (_gate)
        {
            _model.SetPinned(sourceId, pinned);
            pins = InputDevicesModel.SerializePins(_model.Policy.Pins);
        }

        _ = SavePinsAsync(pins);
        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    // ------------------------------------------------------------ backend events (backend thread)

    public void OnConnected(ControllerInfo controller)
    {
        string? notice;
        lock (_gate)
        {
            _pads[controller.InstanceId] = new PadState();
            notice = _model.Connect(controller);
        }

        RaiseNotice(notice);
        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void OnDisconnected(int instanceId)
    {
        int? port;
        string name;
        bool running;
        lock (_gate)
        {
            _input.ReleaseSource(SourceOf(instanceId));
            _pads.Remove(instanceId);
            name = _model.TryGetController($"pad:{instanceId}", out var c) ? c.Name : "A controller";
            running = _model.Policy.GameRunning;
            port = _model.Disconnect(instanceId);
        }

        if (port is { } p && running)
        {
            Raise(() => ControllerDisconnected?.Invoke(this, new ControllerDisconnectedEventArgs(p, name)));
        }

        Raise(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void OnButton(int instanceId, PadButton button, bool down)
    {
        if (!AppActive && down)
        {
            return;
        }

        string? notice = null;
        var toGame = false;
        int port = -1;
        lock (_gate)
        {
            if (!_pads.TryGetValue(instanceId, out var pad))
            {
                return;
            }

            pad.Buttons[(int)button] = down;
            var assigned = _model.Policy.PortOf(instanceId);

            // Take over a port that is waiting for a disconnected pad (input.md §5).
            if (assigned is null && down && button is PadButton.South or PadButton.Start)
            {
                notice = _model.Policy.TryTakeOver(instanceId);
                assigned = _model.Policy.PortOf(instanceId);
                if (notice is not null)
                {
                    pad.SuppressUntilRelease = true; // the take-over press doesn't reach the game
                }
            }

            if (assigned is { } p)
            {
                port = p;
                toGame = GameHasFocus;
                if (button == PadButton.Guide && down)
                {
                    Raise(() => PauseRequested?.Invoke(this, EventArgs.Empty));
                }
            }

            if (pad.SuppressUntilRelease)
            {
                if (!pad.Buttons.Any(b => b))
                {
                    pad.SuppressUntilRelease = false;
                }

                toGame = false;
            }
        }

        if (notice is not null)
        {
            RaiseNotice(notice);
            Raise(() => Changed?.Invoke(this, EventArgs.Empty));
            return;
        }

        if (toGame)
        {
            ApplyToGame(instanceId, port, button, down);
        }
        else if (down && !GameHasFocus)
        {
            NavigateFromButton(instanceId, button);
        }
    }

    public void OnAxis(int instanceId, PadAxis axis, short value)
    {
        if (axis is not (PadAxis.LeftX or PadAxis.LeftY))
        {
            return;
        }

        ControllerState previous, current;
        int? port;
        lock (_gate)
        {
            if (!_pads.TryGetValue(instanceId, out var pad))
            {
                return;
            }

            if (axis == PadAxis.LeftX)
            {
                pad.StickX = value;
            }
            else
            {
                pad.StickY = value;
            }

            previous = pad.StickDirections;
            current = AppActive ? StickInput.ToDirections(pad.StickX, pad.StickY) : ControllerState.None;
            pad.StickDirections = current;
            port = _model.Policy.PortOf(instanceId);
        }

        if (current == previous)
        {
            return;
        }

        if (GameHasFocus && port is { } p)
        {
            foreach (var direction in Directions)
            {
                if ((previous & direction) != 0 && (current & direction) == 0)
                {
                    _input.Release(SourceOf(instanceId), p, direction);
                }
                else if ((previous & direction) == 0 && (current & direction) != 0)
                {
                    _input.Press(SourceOf(instanceId), p, direction);
                }
            }
        }
        else if (!GameHasFocus && DirectionToNavigation(current) is { } nav)
        {
            StartRepeat(instanceId, nav);
        }
    }

    public void OnTick(long nowMs)
    {
        lock (_gate)
        {
            foreach (var (id, pad) in _pads)
            {
                // Back + Start held for a second also pauses (some pads don't expose Guide).
                if (pad.Buttons[(int)PadButton.Back] && pad.Buttons[(int)PadButton.Start] && _model.Policy.PortOf(id) is not null)
                {
                    pad.BackStartSince ??= nowMs;
                    if (!pad.BackStartFired && nowMs - pad.BackStartSince >= BackStartHoldMs)
                    {
                        pad.BackStartFired = true;
                        Raise(() => PauseRequested?.Invoke(this, EventArgs.Empty));
                    }
                }
                else
                {
                    pad.BackStartSince = null;
                    pad.BackStartFired = false;
                }

                // Key repeat for held navigation; stop as soon as the direction is released.
                if (!IsNavigationHeld(pad))
                {
                    pad.RepeatAction = null;
                    pad.RepeatStartedMs = null;
                }
                else if (pad.RepeatAction is { } action && !GameHasFocus)
                {
                    pad.RepeatStartedMs ??= nowMs;
                    var held = nowMs - pad.RepeatStartedMs.Value;
                    if (held >= RepeatDelayMs && nowMs - pad.LastRepeatMs >= RepeatIntervalMs)
                    {
                        pad.LastRepeatMs = nowMs;
                        Raise(() => Navigate?.Invoke(this, action));
                    }
                }
            }
        }
    }

    public void Dispose() => _backend.Dispose();

    // ------------------------------------------------------------ helpers

    private static readonly ControllerState[] Directions =
        [ControllerState.Up, ControllerState.Down, ControllerState.Left, ControllerState.Right];

    private static int SourceOf(int instanceId) => SourceOffset + instanceId;

    /// <summary>Positional defaults (input.md §6): South = B, East = A, West/North = turbo B/A.</summary>
    private void ApplyToGame(int instanceId, int port, PadButton button, bool down)
    {
        var source = SourceOf(instanceId);
        var (state, turbo) = button switch
        {
            PadButton.South => (ControllerState.B, false),
            PadButton.East => (ControllerState.A, false),
            PadButton.West => (ControllerState.B, true),
            PadButton.North => (ControllerState.A, true),
            PadButton.Back => (ControllerState.Select, false),
            PadButton.Start => (ControllerState.Start, false),
            PadButton.DPadUp => (ControllerState.Up, false),
            PadButton.DPadDown => (ControllerState.Down, false),
            PadButton.DPadLeft => (ControllerState.Left, false),
            PadButton.DPadRight => (ControllerState.Right, false),
            _ => (ControllerState.None, false),
        };

        if (state != ControllerState.None)
        {
            switch (turbo, down)
            {
                case (true, true): _input.PressTurbo(source, port, state); break;
                case (true, false): _input.ReleaseTurbo(source, port, state); break;
                case (false, true): _input.Press(source, port, state); break;
                default: _input.Release(source, port, state); break;
            }

            return;
        }

        // Shoulder hotkeys (input.md §8): right = fast-forward, left = rewind (Phase 5).
        var hotkey = button switch
        {
            PadButton.RightShoulder => VirtualKeys.Tab,
            PadButton.LeftShoulder => VirtualKeys.Backspace,
            _ => 0,
        };
        if (hotkey != 0)
        {
            _input.SetPadHotkey(source, hotkey, down);
        }
    }

    private void NavigateFromButton(int instanceId, PadButton button)
    {
        NavigationAction? action = button switch
        {
            PadButton.DPadUp => NavigationAction.Up,
            PadButton.DPadDown => NavigationAction.Down,
            PadButton.DPadLeft => NavigationAction.Left,
            PadButton.DPadRight => NavigationAction.Right,
            PadButton.South => NavigationAction.Activate,
            PadButton.East => NavigationAction.Back,
            _ => null,
        };

        if (action is { } a)
        {
            if (a is NavigationAction.Up or NavigationAction.Down or NavigationAction.Left or NavigationAction.Right)
            {
                StartRepeat(instanceId, a);
            }
            else
            {
                Raise(() => Navigate?.Invoke(this, a));
            }
        }
    }

    private void StartRepeat(int instanceId, NavigationAction action)
    {
        lock (_gate)
        {
            if (_pads.TryGetValue(instanceId, out var pad))
            {
                pad.RepeatAction = action;
                pad.RepeatStartedMs = null;
                pad.LastRepeatMs = long.MinValue / 2;
            }
        }

        Raise(() => Navigate?.Invoke(this, action));
    }

    private static NavigationAction? DirectionToNavigation(ControllerState directions) =>
        (directions & ControllerState.Up) != 0 ? NavigationAction.Up
        : (directions & ControllerState.Down) != 0 ? NavigationAction.Down
        : (directions & ControllerState.Left) != 0 ? NavigationAction.Left
        : (directions & ControllerState.Right) != 0 ? NavigationAction.Right
        : null;

    private static bool IsNavigationHeld(PadState pad) =>
        pad.Buttons[(int)PadButton.DPadUp] || pad.Buttons[(int)PadButton.DPadDown]
        || pad.Buttons[(int)PadButton.DPadLeft] || pad.Buttons[(int)PadButton.DPadRight]
        || pad.StickDirections != ControllerState.None;

    private void ReleaseAllPads()
    {
        foreach (var id in _pads.Keys)
        {
            _input.ReleaseSource(SourceOf(id));
        }
    }

    private async Task SavePinsAsync(string pins)
    {
        if (_settings is null)
        {
            return;
        }

        var current = await _settings.LoadAsync();
        await _settings.SaveAsync(current with { ControllerPins = pins });
    }

    private void RaiseNotice(string? notice)
    {
        if (notice is not null)
        {
            Raise(() => Notice?.Invoke(this, notice));
        }
    }

    private void Raise(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    private sealed class PadState
    {
        public bool[] Buttons { get; } = new bool[15];

        public short StickX { get; set; }

        public short StickY { get; set; }

        public ControllerState StickDirections { get; set; }

        public bool SuppressUntilRelease { get; set; }

        public long? BackStartSince { get; set; }

        public bool BackStartFired { get; set; }

        public NavigationAction? RepeatAction { get; set; }

        public long? RepeatStartedMs { get; set; }

        public long LastRepeatMs { get; set; }
    }
}
