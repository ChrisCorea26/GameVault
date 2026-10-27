using GameVault.Core.Input;

namespace GameVault.Platform.Input;

/// <summary>
/// Collects button presses from every input source, merges them per port and applies the
/// opposite-direction rule (docs/design/input.md §1, §6). Key events arrive on the UI thread; the emulation
/// thread calls <see cref="Sample"/> once per frame.
/// </summary>
public sealed class InputService
{
    /// <summary>Four ports: players 3 and 4 are used only with the Four Score.</summary>
    public const int PortCount = 4;
    public const int KeyboardSource = 0;

    private const int ButtonCount = 64; // every bit of ControllerState, not just the NES pad's eight
    /// <summary>The default turbo cycle: pressed for 2 frames, released for 2, 15 presses per second at 60 Hz (input.md §6).</summary>
    public const int DefaultTurboPeriodFrames = 4;

    /// <summary>The turbo speeds Settings → Input offers, as frames per press-and-release (30, 20, 15, 12, 10 and 7.5 per second).</summary>
    public static IReadOnlyList<int> TurboPeriods { get; } = [2, 3, 4, 5, 6, 8];

    private int _turboPeriod = DefaultTurboPeriodFrames;

    private readonly Lock _gate = new();
    private KeyboardMapping _base;
    private KeyboardMapping _keyboard;
    private Func<PadLayout, IReadOnlyList<(int Port, GameVault.ViewModels.Services.Input.ResolvedRemap Remap)>>? _remaps;
    private PadLayout _layout;

    // Per (source, port, button): how many keys/inputs hold it, and when it was last pressed.
    private readonly Dictionary<(int Source, int Port, int Button), int> _holdCount = [];
    private readonly Dictionary<(int Source, int Port, int Button), int> _turboCount = [];
    private readonly long[,] _lastPressed = new long[PortCount, ButtonCount];
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<int> _heldHotkeys = [];
    private readonly HashSet<(int Source, int Key)> _padHotkeys = [];
    private long _sequence;

    private readonly float[] _rumble = new float[PortCount];

    /// <summary>A port's rumble changed (port, 0–1). Raised on the emulator thread; the controllers pass it to the pad.</summary>
    public event Action<int, float>? RumbleChanged;

    /// <summary>The game set port <paramref name="port"/>'s motor (the N64's Rumble Pak).</summary>
    public void SetRumble(int port, float level)
    {
        if (_rumble[port] != level)
        {
            _rumble[port] = level;
            RumbleChanged?.Invoke(port, level);
        }
    }

    /// <summary>Stops every motor (the game stopped).</summary>
    public void StopRumble()
    {
        for (var port = 0; port < PortCount; port++)
        {
            SetRumble(port, 0);
        }
    }

    public InputService(KeyboardMapping keyboard)
    {
        _base = keyboard;
        _keyboard = keyboard.ForLayout(PadLayout.Nes);
    }

    /// <summary>Replaces the keyboard bindings (for example the two-player preset); releases held keys.</summary>
    public void SetKeyboardMapping(KeyboardMapping mapping)
    {
        lock (_gate)
        {
            _base = mapping;
            RebuildKeyboardLocked();
        }
    }

    /// <summary>
    /// The player's keyboard remaps for each pad layout (GV-001), as (port, remap) pairs; applied over the bindings
    /// whenever the layout or the remaps change. Releases held keys.
    /// </summary>
    public void SetKeyboardRemaps(Func<PadLayout, IReadOnlyList<(int Port, GameVault.ViewModels.Services.Input.ResolvedRemap Remap)>>? remaps)
    {
        lock (_gate)
        {
            _remaps = remaps;
            RebuildKeyboardLocked();
        }
    }

    /// <summary>The bindings in use now: the base mapping for the running pad, with the remaps.</summary>
    public KeyboardMapping KeyboardBindings
    {
        get
        {
            lock (_gate)
            {
                return _keyboard;
            }
        }
    }

    private void RebuildKeyboardLocked()
    {
        ReleaseSourceLocked(KeyboardSource);
        _heldKeys.Clear();
        _keyboard = _base.ForLayout(_layout, _remaps?.Invoke(_layout));
    }

    // ---------------------------------------------------------------- Power Pad and Arkanoid paddle (SPEC §4.7, GV-013)

    /// <summary>
    /// The Power Pad's 12 buttons on the keyboard, in the mat's rows of four: U I O P, J K L ;, M , . / (none of them
    /// is a controller key, so Player 1's pad still works for menus).
    /// </summary>
    public static IReadOnlyList<int> PowerPadKeys { get; } =
    [
        VirtualKeys.U, VirtualKeys.I, VirtualKeys.O, VirtualKeys.P,
        VirtualKeys.J, VirtualKeys.K, VirtualKeys.L, VirtualKeys.Semicolon,
        VirtualKeys.M, VirtualKeys.Comma, VirtualKeys.Period, VirtualKeys.Slash,
    ];

    private ushort _powerPad;
    private double _paddle = 0.5;
    private bool _paddleFire;

    /// <summary>True while a Power Pad game runs: <see cref="PowerPadKeys"/> press the mat instead of anything else.</summary>
    public bool PowerPadActive
    {
        get;
        set
        {
            lock (_gate)
            {
                field = value;
                _powerPad = 0;
            }
        }
    }

    /// <summary>The mat's buttons for the next frame: bit N - 1 for button N (emulation thread).</summary>
    public ushort SamplePowerPad()
    {
        lock (_gate)
        {
            return _powerPad;
        }
    }

    /// <summary>The Arkanoid paddle from the mouse (UI thread): the knob as a fraction of its travel, and the fire button.</summary>
    public void SetPaddle(double fraction, bool fire)
    {
        lock (_gate)
        {
            _paddle = Math.Clamp(fraction, 0, 1);
            _paddleFire = fire;
        }
    }

    /// <summary>The paddle for the next frame (emulation thread).</summary>
    public (double Fraction, bool Fire) SamplePaddle()
    {
        lock (_gate)
        {
            return (_paddle, _paddleFire);
        }
    }

    private bool PowerPadKey(int virtualKey, bool down)
    {
        if (!PowerPadActive)
        {
            return false;
        }

        for (var i = 0; i < PowerPadKeys.Count; i++)
        {
            if (PowerPadKeys[i] == virtualKey)
            {
                lock (_gate)
                {
                    _powerPad = (ushort)(down ? _powerPad | (1 << i) : _powerPad & ~(1 << i));
                }

                return true;
            }
        }

        return false;
    }

    private (int X, int Y)? _zapperAim;
    private bool _zapperTrigger;

    /// <summary>
    /// The Zapper, driven by the mouse over the game (SPEC §4.7): the aim point in NES pixels (null when the
    /// pointer is off the picture) and the left button as the trigger. Set on the UI thread.
    /// </summary>
    public void SetZapper((int X, int Y)? aim, bool trigger)
    {
        lock (_gate)
        {
            _zapperAim = aim;
            _zapperTrigger = trigger;
        }
    }

    /// <summary>The Zapper state for the next frame (emulation thread).</summary>
    public ((int X, int Y)? Aim, bool Trigger) SampleZapper()
    {
        lock (_gate)
        {
            return (_zapperAim, _zapperTrigger);
        }
    }

    /// <summary>When false (default), the most recently pressed direction on each axis wins.</summary>
    public bool AllowOppositeDirections { get; set; }

    /// <summary>
    /// A key went down. Returns true if the game view should mark it handled: game bindings and hotkeys the
    /// emulator reads itself (Tab, Backspace, F5, F8, F12). Esc and unbound keys return false so page-level
    /// accelerators still see them. Hotkeys never reach the game (docs/design/input.md §8).
    /// </summary>
    // ---------------------------------------------------------------- typing (computers such as the Apple II)

    private const int Control = 0x11;
    private readonly HashSet<int> _typingHeld = [];
    private readonly Queue<(int Key, bool Shift, bool Control)> _typed = new();

    /// <summary>
    /// While a computer is running, every key also types into it (SPEC §7.11, Phase 12). GameVault's hotkeys keep
    /// working, except that Backspace types (Shift+Backspace rewinds).
    /// </summary>
    public bool TypingMode
    {
        get;
        set
        {
            lock (_gate)
            {
                field = value;
                _typed.Clear();
                _typingHeld.Clear();
            }
        }
    }

    /// <summary>
    /// True while a Genesis game runs (Phase 13): controllers use the 6-button layout, with the bottom row A, B, C
    /// on West, South and East and the top row X, Y, Z on the left shoulder, North and the right shoulder.
    /// </summary>
    public bool SixButtonLayout
    {
        get => Layout == PadLayout.Genesis;
        set => Layout = value ? PadLayout.Genesis : PadLayout.Nes;
    }

    /// <summary>
    /// The running system's pad (Phase 14). For SNES games controllers map Y and X to West and North and L/R to the
    /// shoulders, and the keyboard's turbo B and turbo A keys (A and S) become Y and X.
    /// </summary>
    public PadLayout Layout
    {
        get => _layout;
        set
        {
            lock (_gate)
            {
                if (_layout != value)
                {
                    _layout = value;
                    RebuildKeyboardLocked();
                }
            }
        }
    }

    /// <summary>True while any typing key is held (the Apple IIe reads it).</summary>
    /// <summary>The keys held now while typing into a computer (the SC-3000 reads its whole keyboard matrix).</summary>
    public IReadOnlyCollection<int> TypingKeysHeld
    {
        get
        {
            lock (_gate)
            {
                return [.. _typingHeld];
            }
        }
    }

    public bool AnyTypingKeyHeld
    {
        get
        {
            lock (_gate)
            {
                return _typingHeld.Any(k => k is not (VirtualKeys.Shift or VirtualKeys.LeftShift or VirtualKeys.RightShift or Control));
            }
        }
    }

    /// <summary>The next key typed since the last call, with the Shift and Ctrl state when it was pressed.</summary>
    public bool TryDequeueTyped(out int virtualKey, out bool shift, out bool control)
    {
        lock (_gate)
        {
            if (_typed.TryDequeue(out var typed))
            {
                (virtualKey, shift, control) = typed;
                return true;
            }
        }

        virtualKey = 0;
        shift = control = false;
        return false;
    }

    /// <summary>True while either Shift key is held (Shift+Backspace rewinds while typing).</summary>
    public bool ShiftHeld
    {
        get
        {
            lock (_gate)
            {
                return _typingHeld.Contains(VirtualKeys.Shift) || _typingHeld.Contains(VirtualKeys.LeftShift) || _typingHeld.Contains(VirtualKeys.RightShift);
            }
        }
    }

    /// <summary>Typing mode: records the key; true when the key was a typing key (so the page doesn't act on it).</summary>
    private bool Type(int virtualKey)
    {
        lock (_gate)
        {
            var shift = _typingHeld.Contains(VirtualKeys.Shift) || _typingHeld.Contains(VirtualKeys.LeftShift) || _typingHeld.Contains(VirtualKeys.RightShift);
            var first = _typingHeld.Add(virtualKey);
            var isHotkey = KeyboardMapping.Hotkeys.Contains(virtualKey) && virtualKey != VirtualKeys.Backspace;
            if (isHotkey || virtualKey is VirtualKeys.Shift or VirtualKeys.LeftShift or VirtualKeys.RightShift or Control)
            {
                return false;
            }

            // Key repeat types again, as on the Apple's own keyboard.
            if (first || _typed.Count < 8)
            {
                _typed.Enqueue((virtualKey, shift, _typingHeld.Contains(Control)));
            }

            return virtualKey != VirtualKeys.Backspace || !shift;
        }
    }

    public bool KeyDown(int virtualKey)
    {
        if (PowerPadKey(virtualKey, down: true))
        {
            return true;
        }

        var typed = TypingMode && Type(virtualKey);
        if (KeyboardMapping.Hotkeys.Contains(virtualKey))
        {
            lock (_gate)
            {
                _heldHotkeys.Add(virtualKey);
            }

            return virtualKey is not (VirtualKeys.Escape or VirtualKeys.F11); // the page handles these
        }

        if (!KeyboardBindings.TryGetBinding(virtualKey, out var port, out var button, out var turbo, out var kind))
        {
            return typed;
        }

        lock (_gate)
        {
            // Ignore key-repeat: only the first KeyDown of a held key counts as a press.
            if (_heldKeys.Add(virtualKey))
            {
                if (turbo)
                {
                    PressTurbo(SourceOf(kind), port, button);
                }
                else
                {
                    Press(SourceOf(kind), port, button);
                }
            }
        }

        return true;
    }

    public bool KeyUp(int virtualKey)
    {
        if (PowerPadKey(virtualKey, down: false))
        {
            return true;
        }

        if (TypingMode)
        {
            lock (_gate)
            {
                _typingHeld.Remove(virtualKey);
            }
        }

        if (KeyboardMapping.Hotkeys.Contains(virtualKey))
        {
            lock (_gate)
            {
                _heldHotkeys.Remove(virtualKey);
            }

            return virtualKey is not (VirtualKeys.Escape or VirtualKeys.F11); // the page handles these
        }

        if (!KeyboardBindings.TryGetBinding(virtualKey, out var port, out var button, out var turbo, out var kind))
        {
            return TypingMode;
        }

        lock (_gate)
        {
            if (_heldKeys.Remove(virtualKey))
            {
                if (turbo)
                {
                    ReleaseTurbo(SourceOf(kind), port, button);
                }
                else
                {
                    Release(SourceOf(kind), port, button);
                }
            }
        }

        return true;
    }

    /// <summary>Presses a button from any source (keyboard, or a pad in Phase 3).</summary>
    public void Press(int source, int port, ControllerState button)
    {
        lock (_gate)
        {
            var bit = BitIndex(button);
            var key = (source, port, bit);
            _holdCount[key] = _holdCount.GetValueOrDefault(key) + 1;
            _lastPressed[port, bit] = ++_sequence;
        }
    }

    public void Release(int source, int port, ControllerState button)
    {
        lock (_gate)
        {
            var key = (source, port, BitIndex(button));
            if (_holdCount.TryGetValue(key, out var count))
            {
                if (count <= 1)
                {
                    _holdCount.Remove(key);
                }
                else
                {
                    _holdCount[key] = count - 1;
                }
            }
        }
    }

    /// <summary>
    /// Frames per turbo press-and-release (2-8). The button is pressed for the first half of each cycle, rounded
    /// up, so a 3-frame cycle presses for 2 frames and releases for 1.
    /// </summary>
    public int TurboPeriodFrames
    {
        get => _turboPeriod;
        set => _turboPeriod = Math.Clamp(value, 2, 8);
    }

    /// <summary>Holds a turbo button: it is pressed and released every <see cref="TurboPeriodFrames"/> frames while held.</summary>
    public void PressTurbo(int source, int port, ControllerState button)
    {
        lock (_gate)
        {
            var key = (source, port, BitIndex(button));
            _turboCount[key] = _turboCount.GetValueOrDefault(key) + 1;
        }
    }

    public void ReleaseTurbo(int source, int port, ControllerState button)
    {
        lock (_gate)
        {
            var key = (source, port, BitIndex(button));
            if (_turboCount.TryGetValue(key, out var count) && count > 1)
            {
                _turboCount[key] = count - 1;
            }
            else
            {
                _turboCount.Remove(key);
            }
        }
    }

    /// <summary>Releases everything one source holds (a pad that disconnected or moved to another port).</summary>
    public void ReleaseSource(int source)
    {
        lock (_gate)
        {
            ReleaseSourceLocked(source);
        }
    }

    private void ReleaseSourceLocked(int source)
    {
        _padHotkeys.RemoveWhere(h => h.Source == source);
        foreach (var key in _sticks.Keys.Where(k => k.Source == source).ToList())
        {
            _sticks.Remove(key);
        }

        foreach (var key in _triggers.Keys.Where(k => k.Source == source).ToList())
        {
            _triggers.Remove(key);
        }

        if (source == KeyboardSource)
        {
            ReleaseSourceLocked(KeyboardDpadSource);
            ReleaseSourceLocked(KeyboardRightStickSource);
        }

        foreach (var key in _holdCount.Keys.Where(k => k.Source == source).ToList())
        {
            _holdCount.Remove(key);
        }

        foreach (var key in _turboCount.Keys.Where(k => k.Source == source).ToList())
        {
            _turboCount.Remove(key);
        }
    }

    /// <summary>Releases everything, e.g. when the game view loses focus (input.md §4.4).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            _holdCount.Clear();
            _turboCount.Clear();
            _heldKeys.Clear();
            _heldHotkeys.Clear();
            _padHotkeys.Clear();
            _sticks.Clear();
            _triggers.Clear();
            _zapperTrigger = false;
            _powerPad = 0;
            _paddleFire = false;
        }
    }

    /// <summary>True while a hotkey such as Tab (fast-forward) is held on the keyboard or an assigned pad.</summary>
    public bool IsHotkeyHeld(int virtualKey)
    {
        lock (_gate)
        {
            return _heldHotkeys.Contains(virtualKey) || _padHotkeys.Any(h => h.Key == virtualKey);
        }
    }

    /// <summary>A pad's shoulder hotkey (input.md §8), expressed as the keyboard key it stands for.</summary>
    public void SetPadHotkey(int source, int virtualKey, bool held)
    {
        lock (_gate)
        {
            if (held)
            {
                _padHotkeys.Add((source, virtualKey));
            }
            else
            {
                _padHotkeys.Remove((source, virtualKey));
            }
        }
    }

    /// <summary>
    /// The merged buttons for a port, as the game should see them for frame <paramref name="frame"/>.
    /// Turbo phase comes from the frame number, never from wall-clock time, so runs stay deterministic.
    /// </summary>
    public ControllerState Sample(int port, long frame = 0)
    {
        lock (_gate)
        {
            var state = ControllerState.None;
            foreach (var ((_, p, bit), _) in _holdCount)
            {
                if (p == port)
                {
                    state |= (ControllerState)(1UL << bit);
                }
            }

            // Pads without turbo (SNES, PlayStation, N64, PS2) get the turbo keys as Y and X from the keyboard mapping
            // (KeyboardMapping.ForLayout); a turbo button the player remapped there repeats like any other.
            var period = _turboPeriod;
            if (frame % period < (period + 1) / 2)
            {
                foreach (var ((_, p, bit), _) in _turboCount)
                {
                    if (p == port)
                    {
                        state |= (ControllerState)(1UL << bit);
                    }
                }
            }

            if (Layout is PadLayout.Nintendo64 or PadLayout.PlayStation2)
            {
                state = WithSticks(state, port);
                return Layout == PadLayout.PlayStation2 ? WithPressure(state, port) : state;
            }

            if (!AllowOppositeDirections)
            {
                state = ResolveAxis(state, port, ControllerState.Left, ControllerState.Right);
                state = ResolveAxis(state, port, ControllerState.Up, ControllerState.Down);
            }

            return state;
        }
    }

    // ---------------------------------------------------------------- analog triggers (PlayStation 2 pressure)

    private readonly Dictionary<(int Source, int Port), (int L2, int R2)> _triggers = [];

    /// <summary>A pad's analog triggers, each 0–32767, for the DualShock 2's L2 and R2 pressure.</summary>
    public void SetTriggers(int source, int port, short l2, short r2)
    {
        lock (_gate)
        {
            if (l2 <= 0 && r2 <= 0)
            {
                _triggers.Remove((source, port));
            }
            else
            {
                _triggers[(source, port)] = (Math.Max((int)l2, 0) >> 8, Math.Max((int)r2, 0) >> 8);
            }
        }
    }

    /// <summary>The furthest-pulled pad trigger of each side, as pressure. Called with the lock held.</summary>
    private ControllerState WithPressure(ControllerState state, int port)
    {
        int l2 = 0, r2 = 0;
        foreach (var ((_, p), (l, r)) in _triggers)
        {
            if (p == port)
            {
                l2 = Math.Max(l2, l);
                r2 = Math.Max(r2, r);
            }
        }

        return state.WithTriggerPressure(l2, r2);
    }

    // ---------------------------------------------------------------- analog sticks (N64, Phase 16; PlayStation 2, Phase 17)

    /// <summary>Where a key's press is counted: the keyboard, or its D-pad or right-stick keys on the N64 and PS2.</summary>
    private static int SourceOf(GameVault.ViewModels.Services.Input.KeyKind kind) => kind switch
    {
        GameVault.ViewModels.Services.Input.KeyKind.DPad => KeyboardDpadSource,
        GameVault.ViewModels.Services.Input.KeyKind.RightStick => KeyboardRightStickSource,
        _ => KeyboardSource,
    };

    /// <summary>The keyboard's D-pad keys while an N64 or PS2 game runs, kept apart from the arrows, which push the stick.</summary>
    private const int KeyboardDpadSource = -1;

    /// <summary>The keyboard's right-stick keys (I/J/K/L) while a PS2 game runs: held as directions, read as the right stick.</summary>
    private const int KeyboardRightStickSource = -2;

    /// <summary>A real N64 stick reaches about ±80 at full tilt (n64brew: "Joybus Controller").</summary>
    public const int StickRange = 80;

    /// <summary>The DualShock 2's sticks use their whole byte (ps2tek: "SIO2 Controllers"), so full tilt is ±127.</summary>
    public const int DualShockStickRange = 127;

    // Each pad's sticks past the dead zone, as fractions of full tilt (+Y up); scaled to the running pad's range.
    private readonly Dictionary<(int Source, int Port, bool Right), (double X, double Y)> _sticks = [];

    /// <summary>
    /// A game controller's left analog stick (SDL's -32768..32767, +Y down), with a small round dead zone. Pads call
    /// this whatever the layout; only N64 and PS2 games read it, each scaled to its pad's range.
    /// </summary>
    public void SetStick(int source, int port, short x, short y, double deadZone = 0.15) =>
        SetStick(source, port, right: false, x, y, deadZone);

    /// <summary>A game controller's right analog stick, as <see cref="SetStick(int, int, short, short, double)"/>; PS2 games read it.</summary>
    public void SetRightStick(int source, int port, short x, short y, double deadZone = 0.15) =>
        SetStick(source, port, right: true, x, y, deadZone);

    private void SetStick(int source, int port, bool right, short x, short y, double deadZone)
    {
        var fx = x / 32768.0;
        var fy = -y / 32768.0;
        var length = Math.Sqrt(fx * fx + fy * fy);
        lock (_gate)
        {
            if (length > deadZone)
            {
                var scale = Math.Min(1.0, (length - deadZone) / (1 - deadZone)) / length;
                _sticks[(source, port, right)] = (fx * scale, fy * scale);
            }
            else
            {
                _sticks.Remove((source, port, right));
            }
        }
    }

    /// <summary>
    /// The sticks for a port: the keyboard's arrows push the left stick all the way (their direction bits leave the
    /// D-pad), and on the PS2 the right-stick keys push the right one; the strongest pad stick wins over them. Called
    /// with the lock held.
    /// </summary>
    private ControllerState WithSticks(ControllerState state, int port)
    {
        var range = Layout == PadLayout.PlayStation2 ? DualShockStickRange : StickRange;
        ControllerState leftKeys = ControllerState.None, rightKeys = ControllerState.None;
        foreach (var ((source, p, bit), _) in _holdCount)
        {
            if (p == port && bit is >= 4 and <= 7)
            {
                if (source == KeyboardSource)
                {
                    leftKeys |= (ControllerState)(1UL << bit);
                }
                else if (source == KeyboardRightStickSource)
                {
                    rightKeys |= (ControllerState)(1UL << bit);
                }
            }
        }

        // The D-pad is what pads and the keyboard's D-pad keys hold, not the arrows.
        state &= ~(ControllerState.Up | ControllerState.Down | ControllerState.Left | ControllerState.Right);
        foreach (var ((source, p, bit), _) in _holdCount)
        {
            if (p == port && source is not (KeyboardSource or KeyboardRightStickSource) && bit is >= 4 and <= 7)
            {
                state |= (ControllerState)(1UL << bit);
            }
        }

        var (lx, ly) = Strongest(leftKeys, port, right: false, range);
        state = state.WithStick(lx, ly);
        if (Layout == PadLayout.PlayStation2)
        {
            var (rx, ry) = Strongest(rightKeys, port, right: true, range);
            state = state.WithRightStick(rx, ry);
        }

        return state;
    }

    /// <summary>One stick: the keys held for it push it all the way, and a pad's stick pushed further wins.</summary>
    private (sbyte X, sbyte Y) Strongest(ControllerState keys, int port, bool right, int range)
    {
        double x = ((keys & ControllerState.Right) != 0 ? 1 : 0) - ((keys & ControllerState.Left) != 0 ? 1 : 0);
        double y = ((keys & ControllerState.Up) != 0 ? 1 : 0) - ((keys & ControllerState.Down) != 0 ? 1 : 0);
        var strongest = x * x + y * y;
        foreach (var ((_, p, r), stick) in _sticks)
        {
            var magnitude = stick.X * stick.X + stick.Y * stick.Y;
            if (p == port && r == right && magnitude > strongest)
            {
                (x, y, strongest) = (stick.X, stick.Y, magnitude);
            }
        }

        return ((sbyte)Math.Round(x * range), (sbyte)Math.Round(y * range));
    }

    private ControllerState ResolveAxis(ControllerState state, int port, ControllerState a, ControllerState b)
    {
        if ((state & a) == 0 || (state & b) == 0)
        {
            return state;
        }

        var aWins = _lastPressed[port, BitIndex(a)] > _lastPressed[port, BitIndex(b)];
        return state & ~(aWins ? b : a);
    }

    private static int BitIndex(ControllerState button) => System.Numerics.BitOperations.TrailingZeroCount((ulong)button);
}

/// <summary>Which console's pad the controllers and keyboard stand in for.</summary>
public enum PadLayout
{
    Nes,

    /// <summary>The SG-1000 and Master System: the NES's bindings, under their own names and remaps.</summary>
    Sega,
    Genesis,
    Snes,

    /// <summary>The SNES's positions (Cross south, Circle east, Square west, Triangle north) plus the triggers as L2 and R2.</summary>
    PlayStation,

    /// <summary>
    /// The N64 pad (Phase 16): the PlayStation's buttons (South A, West B, East and North C-down and C-left, either
    /// trigger Z), the left stick as the analog stick, the right stick as the C buttons; on the keyboard the arrows
    /// push the stick, I/J/K/L are the C buttons and T/F/G/H the D-pad.
    /// </summary>
    Nintendo64,

    /// <summary>
    /// The DualShock 2 (Phase 17): the PlayStation's buttons by position, L3 and R3 on the stick clicks, and both
    /// sticks as the analog sticks (the left stick no longer presses the D-pad); on the keyboard the arrows push the
    /// left stick, I/J/K/L the right stick, T/F/G/H are the D-pad and 5/6 are L3/R3.
    /// </summary>
    PlayStation2,
}
