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

    public int TurboPeriodFrames
    {
        get => _input.TurboPeriodFrames;
        set => _input.TurboPeriodFrames = value;
    }

    // ------------------------------------------------------------ remaps (GV-001)

    private InputRemapSet _remaps = InputRemapSet.Empty;
    private string? _gameKey;

    // Each pad model's buttons per port and layout, when the player changed any; null means the defaults.
    private readonly Dictionary<(string Model, int Port, PadLayout Layout), Dictionary<PadButton, (ControllerState State, bool Turbo)>?> _padTables = [];

    public InputRemapSet Remaps
    {
        get => _remaps;
        set
        {
            lock (_gate)
            {
                _remaps = value;
                _padTables.Clear();
                ReleaseAllPads();
            }

            _input.SetKeyboardRemaps(KeyboardRemaps);
        }
    }

    public string? GameKey
    {
        get => _gameKey;
        set
        {
            if (_gameKey == value)
            {
                return;
            }

            lock (_gate)
            {
                _gameKey = value;
                _padTables.Clear();
                ReleaseAllPads();
            }

            _input.SetKeyboardRemaps(KeyboardRemaps);
        }
    }

    /// <summary>While on, pad buttons are reported through <see cref="InputCaptured"/> and go nowhere else.</summary>
    public bool CaptureMode { get; set; }

    public event EventHandler<CapturedInput>? InputCaptured;

    /// <summary>The system whose remaps a pad layout uses (the names match).</summary>
    public static InputSystem SystemOf(PadLayout layout) => Enum.Parse<InputSystem>(layout.ToString());

    private IReadOnlyList<(int Port, ResolvedRemap Remap)> KeyboardRemaps(PadLayout layout)
    {
        var system = SystemOf(layout);
        var result = new List<(int, ResolvedRemap)>();
        for (var player = 0; player < PortAssignmentPolicy.MaxPorts; player++)
        {
            foreach (var remap in _remaps.Resolve(IInputDevicesService.KeyboardId, player, system, _gameKey))
            {
                result.Add((player, remap));
            }
        }

        return result;
    }

    /// <summary>A pad's buttons on a port for the running layout, with its model's remaps; null when it has none.</summary>
    private Dictionary<PadButton, (ControllerState State, bool Turbo)>? PadTable(int instanceId, int port, PadLayout layout)
    {
        lock (_gate)
        {
            if (!_model.TryGetController($"pad:{instanceId}", out var controller))
            {
                return null;
            }

            var key = (InputDevicesModel.ModelOf(controller), port, layout);
            if (!_padTables.TryGetValue(key, out var table))
            {
                var remaps = _remaps.Resolve(key.Item1, port, SystemOf(layout), _gameKey);
                if (remaps.Count > 0)
                {
                    table = Enum.GetValues<PadButton>().ToDictionary(b => b, b => DefaultButton(layout, b));
                    foreach (var remap in remaps)
                    {
                        foreach (var (button, target) in table.ToList())
                        {
                            if (target.State == remap.Button && target.Turbo == remap.Turbo)
                            {
                                table[button] = (ControllerState.None, false);
                            }
                        }

                        if (InputCodes.TryPad(remap.Input, out var name) && Enum.TryParse<PadButton>(name, out var padButton))
                        {
                            table[padButton] = (remap.Button, remap.Turbo);
                        }
                    }
                }

                _padTables[key] = table;
            }

            return table;
        }
    }

    /// <summary>Loads pins, the keyboard preset and the turbo speed, then starts the backend. Call once the window is up.</summary>
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

            Remaps = InputRemapSet.Parse(settings.InputRemaps);

            TurboPeriodFrames = settings.TurboPeriodFrames;
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
            if (CaptureMode)
            {
                // Remap… is waiting for a button: report it, and keep it from the game and the menus until released.
                if (down)
                {
                    var id = $"pad:{instanceId}";
                    Raise(() => InputCaptured?.Invoke(this, new CapturedInput(id, InputCodes.Pad(button.ToString()))));
                }

                pad.SuppressUntilRelease = true;
                return;
            }

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
        if (axis is PadAxis.TriggerLeft or PadAxis.TriggerRight)
        {
            OnTrigger(instanceId, axis == PadAxis.TriggerLeft ? ControllerState.L2 : ControllerState.R2, value);
            return;
        }

        if (axis is PadAxis.RightX or PadAxis.RightY)
        {
            OnRightStick(instanceId, axis, value);
            return;
        }

        ControllerState previous, current;
        int? port;
        short x, y;
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
            (x, y) = AppActive ? (pad.StickX, pad.StickY) : ((short)0, (short)0);
        }

        // N64 and PS2 games read the stick itself (Phases 16 and 17); it doesn't press the D-pad.
        var analog = _input.Layout is PadLayout.Nintendo64 or PadLayout.PlayStation2;
        if (port is { } stickPort)
        {
            _input.SetStick(SourceOf(instanceId), stickPort, GameHasFocus ? x : (short)0, GameHasFocus ? y : (short)0);
        }

        if (current == previous)
        {
            return;
        }

        if (GameHasFocus && !analog && port is { } p)
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

    /// <summary>The SNES pad's own layout: B south, A east, Y west, X north, L and R on the shoulders.</summary>
    private static (ControllerState State, bool Turbo) SnesButton(PadButton button) => button switch
    {
        PadButton.South => (ControllerState.B, false),
        PadButton.East => (ControllerState.A, false),
        PadButton.West => (ControllerState.Y, false),
        PadButton.North => (ControllerState.X, false),
        PadButton.LeftShoulder => (ControllerState.L, false),
        PadButton.RightShoulder => (ControllerState.R, false),
        PadButton.Back => (ControllerState.Select, false),
        PadButton.Start => (ControllerState.Start, false),
        PadButton.DPadUp => (ControllerState.Up, false),
        PadButton.DPadDown => (ControllerState.Down, false),
        PadButton.DPadLeft => (ControllerState.Left, false),
        PadButton.DPadRight => (ControllerState.Right, false),
        _ => (ControllerState.None, false),
    };

    /// <summary>The DualShock 2: the PlayStation's buttons by position (as the SNES pad's), with the stick clicks as L3 and R3.</summary>
    private static (ControllerState State, bool Turbo) DualShock2Button(PadButton button) => button switch
    {
        PadButton.LeftStick => (ControllerState.L3, false),
        PadButton.RightStick => (ControllerState.R3, false),
        _ => SnesButton(button),
    };

    private static (ControllerState State, bool Turbo) SixButton(PadButton button) => button switch
    {
        PadButton.West => (ControllerState.B, false),
        PadButton.South => (ControllerState.A, false),
        PadButton.East => (ControllerState.C, false),
        PadButton.LeftShoulder => (ControllerState.X, false),
        PadButton.North => (ControllerState.Y, false),
        PadButton.RightShoulder => (ControllerState.Z, false),
        PadButton.Back => (ControllerState.Select, false),
        PadButton.Start => (ControllerState.Start, false),
        PadButton.DPadUp => (ControllerState.Up, false),
        PadButton.DPadDown => (ControllerState.Down, false),
        PadButton.DPadLeft => (ControllerState.Left, false),
        PadButton.DPadRight => (ControllerState.Right, false),
        _ => (ControllerState.None, false),
    };

    /// <summary>
    /// Positional defaults (input.md §6): South = B, East = A, West/North = turbo B/A. Genesis games use the
    /// 6-button layout instead (see <see cref="InputService.SixButtonLayout"/>; the pad's A is B here, its B is A).
    /// </summary>
    private void ApplyToGame(int instanceId, int port, PadButton button, bool down)
    {
        var source = SourceOf(instanceId);
        var layout = _input.Layout;
        var (state, turbo) = PadTable(instanceId, port, layout) is { } table ? table[button] : DefaultButton(layout, button);
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

        // Shoulder hotkeys (input.md §8): right = fast-forward, left = rewind (Phase 5), unless remapped to a button.
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

    /// <summary>A pad button's default meaning for a layout (input.md §6), before any remap.</summary>
    private static (ControllerState State, bool Turbo) DefaultButton(PadLayout layout, PadButton button) =>
        layout == PadLayout.Genesis ? SixButton(button) : layout == PadLayout.PlayStation2 ? DualShock2Button(button) : layout is PadLayout.Snes or PadLayout.PlayStation or PadLayout.Nintendo64 ? SnesButton(button) : button switch
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

    /// <summary>
    /// The right stick is the N64's C buttons (Phase 16), pushed past half way like the left stick's directions, and
    /// the DualShock 2's right stick (Phase 17).
    /// </summary>
    private void OnRightStick(int instanceId, PadAxis axis, short value)
    {
        ControllerState previous, current;
        int? port;
        short x, y;
        lock (_gate)
        {
            if (!_pads.TryGetValue(instanceId, out var pad))
            {
                return;
            }

            if (axis == PadAxis.RightX)
            {
                pad.RightX = value;
            }
            else
            {
                pad.RightY = value;
            }

            previous = pad.CButtons;
            current = AppActive && _input.Layout == PadLayout.Nintendo64 ? CButtons(StickInput.ToDirections(pad.RightX, pad.RightY)) : ControllerState.None;
            pad.CButtons = current;
            port = _model.Policy.PortOf(instanceId);
            (x, y) = AppActive && GameHasFocus ? (pad.RightX, pad.RightY) : ((short)0, (short)0);
        }

        if (port is { } stickPort)
        {
            _input.SetRightStick(SourceOf(instanceId), stickPort, x, y);
        }

        if (current == previous || port is not { } p)
        {
            return;
        }

        foreach (var button in (ReadOnlySpan<ControllerState>)[ControllerState.C, ControllerState.A, ControllerState.X, ControllerState.Z])
        {
            if ((previous & button) != 0 && (current & button) == 0)
            {
                _input.Release(SourceOf(instanceId), p, button);
            }
            else if ((previous & button) == 0 && (current & button) != 0 && GameHasFocus)
            {
                _input.Press(SourceOf(instanceId), p, button);
            }
        }
    }

    /// <summary>Stick directions as the N64's C buttons: up C-up (C), down C-down (A), left C-left (X), right C-right (Z).</summary>
    private static ControllerState CButtons(ControllerState directions) =>
        ((directions & ControllerState.Up) != 0 ? ControllerState.C : 0)
        | ((directions & ControllerState.Down) != 0 ? ControllerState.A : 0)
        | ((directions & ControllerState.Left) != 0 ? ControllerState.X : 0)
        | ((directions & ControllerState.Right) != 0 ? ControllerState.Z : 0);

    /// <summary>
    /// The analog triggers are the PlayStation's L2 and R2 while a PlayStation (or PS2) game runs: pressed past half way,
    /// released below a quarter, so a resting trigger never flickers.
    /// </summary>
    private void OnTrigger(int instanceId, ControllerState button, short value)
    {
        bool changed, down;
        bool uiPressed = false;
        int? port;
        lock (_gate)
        {
            if (!_pads.TryGetValue(instanceId, out var pad))
            {
                return;
            }

            // Outside the game the triggers scroll the page, with the same thresholds as L2/R2 below.
            var left = button == ControllerState.L2;
            var uiHeld = left ? pad.UiTriggerLeft : pad.UiTriggerRight;
            var uiDown = AppActive && !GameHasFocus && !CaptureMode && (uiHeld ? value > 8000 : value > 16000);
            uiPressed = uiDown && !uiHeld;
            if (left)
            {
                pad.UiTriggerLeft = uiDown;
            }
            else
            {
                pad.UiTriggerRight = uiDown;
            }

            var held = (pad.Triggers & button) != 0;
            down = _input.Layout is PadLayout.PlayStation or PadLayout.Nintendo64 or PadLayout.PlayStation2 && AppActive && (held ? value > 8000 : value > 16000);
            changed = held != down;
            pad.Triggers = down ? pad.Triggers | button : pad.Triggers & ~button;
            if (left)
            {
                pad.TriggerLeft = value;
            }
            else
            {
                pad.TriggerRight = value;
            }

            port = _model.Policy.PortOf(instanceId);
            if (port is { } pressurePort && _input.Layout == PadLayout.PlayStation2)
            {
                // How far each trigger is pulled, for the DualShock 2's pressure-sensitive L2 and R2.
                var inGame = GameHasFocus && AppActive;
                _input.SetTriggers(SourceOf(instanceId), pressurePort, inGame ? pad.TriggerLeft : (short)0, inGame ? pad.TriggerRight : (short)0);
            }
        }

        if (uiPressed)
        {
            StartRepeat(instanceId, button == ControllerState.L2 ? NavigationAction.PageUp : NavigationAction.PageDown);
        }

        if (changed && port is { } p)
        {
            if (down && GameHasFocus)
            {
                _input.Press(SourceOf(instanceId), p, button);
            }
            else if (!down)
            {
                _input.Release(SourceOf(instanceId), p, button);
            }
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
            PadButton.LeftShoulder => NavigationAction.PreviousPage,
            PadButton.RightShoulder => NavigationAction.NextPage,
            PadButton.North => NavigationAction.ContextMenu,
            PadButton.Start => NavigationAction.Menu,
            _ => null,
        };

        if (action is { } a)
        {
            if (IsRepeating(a))
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

    /// <summary>Directions and the triggers' page scrolling repeat while held; the other actions fire once.</summary>
    private static bool IsRepeating(NavigationAction action) =>
        action is NavigationAction.Up or NavigationAction.Down or NavigationAction.Left or NavigationAction.Right
            or NavigationAction.PageUp or NavigationAction.PageDown;

    private static NavigationAction? DirectionToNavigation(ControllerState directions) =>
        (directions & ControllerState.Up) != 0 ? NavigationAction.Up
        : (directions & ControllerState.Down) != 0 ? NavigationAction.Down
        : (directions & ControllerState.Left) != 0 ? NavigationAction.Left
        : (directions & ControllerState.Right) != 0 ? NavigationAction.Right
        : null;

    private static bool IsNavigationHeld(PadState pad) =>
        pad.Buttons[(int)PadButton.DPadUp] || pad.Buttons[(int)PadButton.DPadDown]
        || pad.Buttons[(int)PadButton.DPadLeft] || pad.Buttons[(int)PadButton.DPadRight]
        || pad.StickDirections != ControllerState.None
        || pad.UiTriggerLeft || pad.UiTriggerRight;

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

        public short RightX { get; set; }

        public short RightY { get; set; }

        /// <summary>The N64 C buttons the right stick holds.</summary>
        public ControllerState CButtons { get; set; }

        /// <summary>The triggers held as L2 and R2.</summary>
        public ControllerState Triggers { get; set; }

        /// <summary>The triggers held past the menus' threshold, which scroll a page (input.md §7.1).</summary>
        public bool UiTriggerLeft { get; set; }

        public short TriggerLeft { get; set; }

        public short TriggerRight { get; set; }

        public bool UiTriggerRight { get; set; }

        public bool SuppressUntilRelease { get; set; }

        public long? BackStartSince { get; set; }

        public bool BackStartFired { get; set; }

        public NavigationAction? RepeatAction { get; set; }

        public long? RepeatStartedMs { get; set; }

        public long LastRepeatMs { get; set; }
    }
}
