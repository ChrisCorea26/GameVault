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

    private const int ButtonCount = 16; // every bit of ControllerState, not just the NES pad's eight
    /// <summary>The default turbo cycle: pressed for 2 frames, released for 2, 15 presses per second at 60 Hz (input.md §6).</summary>
    public const int DefaultTurboPeriodFrames = 4;

    /// <summary>The turbo speeds Settings → Input offers, as frames per press-and-release (30, 20, 15, 12, 10 and 7.5 per second).</summary>
    public static IReadOnlyList<int> TurboPeriods { get; } = [2, 3, 4, 5, 6, 8];

    private int _turboPeriod = DefaultTurboPeriodFrames;

    private readonly Lock _gate = new();
    private KeyboardMapping _keyboard;

    // Per (source, port, button): how many keys/inputs hold it, and when it was last pressed.
    private readonly Dictionary<(int Source, int Port, int Button), int> _holdCount = [];
    private readonly Dictionary<(int Source, int Port, int Button), int> _turboCount = [];
    private readonly long[,] _lastPressed = new long[PortCount, ButtonCount];
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<int> _heldHotkeys = [];
    private readonly HashSet<(int Source, int Key)> _padHotkeys = [];
    private long _sequence;

    public InputService(KeyboardMapping keyboard)
    {
        _keyboard = keyboard;
    }

    /// <summary>Replaces the keyboard bindings (for example the two-player preset); releases held keys.</summary>
    public void SetKeyboardMapping(KeyboardMapping mapping)
    {
        lock (_gate)
        {
            ReleaseSourceLocked(KeyboardSource);
            _heldKeys.Clear();
            _keyboard = mapping;
        }
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
    public PadLayout Layout { get; set; }

    /// <summary>True while any typing key is held (the Apple IIe reads it).</summary>
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
        var typed = TypingMode && Type(virtualKey);
        if (KeyboardMapping.Hotkeys.Contains(virtualKey))
        {
            lock (_gate)
            {
                _heldHotkeys.Add(virtualKey);
            }

            return virtualKey is not (VirtualKeys.Escape or VirtualKeys.F11); // the page handles these
        }

        if (!_keyboard.TryGetBinding(virtualKey, out var port, out var button, out var turbo))
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
                    PressTurbo(KeyboardSource, port, button);
                }
                else
                {
                    Press(KeyboardSource, port, button);
                }
            }
        }

        return true;
    }

    public bool KeyUp(int virtualKey)
    {
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

        if (!_keyboard.TryGetBinding(virtualKey, out var port, out var button, out var turbo))
        {
            return TypingMode;
        }

        lock (_gate)
        {
            if (_heldKeys.Remove(virtualKey))
            {
                if (turbo)
                {
                    ReleaseTurbo(KeyboardSource, port, button);
                }
                else
                {
                    Release(KeyboardSource, port, button);
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
            _zapperTrigger = false;
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
                    state |= (ControllerState)(1 << bit);
                }
            }

            var period = _turboPeriod;
            if (Layout is PadLayout.Snes or PadLayout.PlayStation)
            {
                // No turbo on SNES or PlayStation pads: those keys are Y (turbo B's key) and X (turbo A's key), the
                // PlayStation's Square and Triangle.
                foreach (var ((_, p, bit), _) in _turboCount)
                {
                    if (p == port)
                    {
                        var button = (ControllerState)(1 << bit);
                        state |= button == ControllerState.B ? ControllerState.Y : button == ControllerState.A ? ControllerState.X : button;
                    }
                }
            }
            else if (frame % period < (period + 1) / 2)
            {
                foreach (var ((_, p, bit), _) in _turboCount)
                {
                    if (p == port)
                    {
                        state |= (ControllerState)(1 << bit);
                    }
                }
            }

            if (!AllowOppositeDirections)
            {
                state = ResolveAxis(state, port, ControllerState.Left, ControllerState.Right);
                state = ResolveAxis(state, port, ControllerState.Up, ControllerState.Down);
            }

            return state;
        }
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

    private static int BitIndex(ControllerState button) => System.Numerics.BitOperations.TrailingZeroCount((int)button);
}

/// <summary>Which console's pad the controllers and keyboard stand in for.</summary>
public enum PadLayout
{
    Nes,
    Genesis,
    Snes,

    /// <summary>The SNES's positions (Cross south, Circle east, Square west, Triangle north) plus the triggers as L2 and R2.</summary>
    PlayStation,
}
