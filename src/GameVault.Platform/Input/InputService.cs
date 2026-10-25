using GameVault.Core.Input;

namespace GameVault.Platform.Input;

/// <summary>
/// Collects button presses from every input source, merges them per port and applies the
/// opposite-direction rule (docs/design/input.md §1, §6). Key events arrive on the UI thread; the emulation
/// thread calls <see cref="Sample"/> once per frame.
/// </summary>
public sealed class InputService
{
    public const int PortCount = 2;
    public const int KeyboardSource = 0;

    private const int ButtonCount = 8;
    /// <summary>Turbo presses for 2 frames and releases for 2: 15 presses per second at 60 Hz (input.md §6).</summary>
    public const int TurboHalfPeriod = 2;

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
    public bool KeyDown(int virtualKey)
    {
        if (KeyboardMapping.Hotkeys.Contains(virtualKey))
        {
            lock (_gate)
            {
                _heldHotkeys.Add(virtualKey);
            }

            return virtualKey != VirtualKeys.Escape;
        }

        if (!_keyboard.TryGetBinding(virtualKey, out var port, out var button))
        {
            return false;
        }

        lock (_gate)
        {
            // Ignore key-repeat: only the first KeyDown of a held key counts as a press.
            if (_heldKeys.Add(virtualKey))
            {
                Press(KeyboardSource, port, button);
            }
        }

        return true;
    }

    public bool KeyUp(int virtualKey)
    {
        if (KeyboardMapping.Hotkeys.Contains(virtualKey))
        {
            lock (_gate)
            {
                _heldHotkeys.Remove(virtualKey);
            }

            return virtualKey != VirtualKeys.Escape;
        }

        if (!_keyboard.TryGetBinding(virtualKey, out var port, out var button))
        {
            return false;
        }

        lock (_gate)
        {
            if (_heldKeys.Remove(virtualKey))
            {
                Release(KeyboardSource, port, button);
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

    /// <summary>Holds a turbo button: it toggles every <see cref="TurboHalfPeriod"/> frames while held.</summary>
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

            if (frame / TurboHalfPeriod % 2 == 0)
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
