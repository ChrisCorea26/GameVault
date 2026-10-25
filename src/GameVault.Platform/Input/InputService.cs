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
    private readonly Lock _gate = new();
    private readonly KeyboardMapping _keyboard;

    // Per (source, port, button): how many keys/inputs hold it, and when it was last pressed.
    private readonly Dictionary<(int Source, int Port, int Button), int> _holdCount = [];
    private readonly long[,] _lastPressed = new long[PortCount, ButtonCount];
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<int> _heldHotkeys = [];
    private long _sequence;

    public InputService(KeyboardMapping keyboard)
    {
        _keyboard = keyboard;
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

    /// <summary>Releases everything, e.g. when the game view loses focus (input.md §4.4).</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            _holdCount.Clear();
            _heldKeys.Clear();
            _heldHotkeys.Clear();
        }
    }

    /// <summary>True while a hotkey such as Tab (fast-forward) is held.</summary>
    public bool IsHotkeyHeld(int virtualKey)
    {
        lock (_gate)
        {
            return _heldHotkeys.Contains(virtualKey);
        }
    }

    /// <summary>The merged buttons for a port, as the game should see them this frame.</summary>
    public ControllerState Sample(int port)
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
