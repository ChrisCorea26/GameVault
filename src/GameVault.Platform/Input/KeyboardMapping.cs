using GameVault.Core.Input;

namespace GameVault.Platform.Input;

/// <summary>Keyboard bindings: virtual key → (port, button). Defaults per docs/design/input.md §4.1.</summary>
public sealed class KeyboardMapping
{
    private readonly Dictionary<int, (int Port, ControllerState Button)> _bindings = [];

    /// <summary>Keys reserved for hotkeys (input.md §8). They never reach the game.</summary>
    public static IReadOnlySet<int> Hotkeys { get; } = new HashSet<int>
    {
        VirtualKeys.Escape, VirtualKeys.Backspace, VirtualKeys.Tab, VirtualKeys.F5, VirtualKeys.F8, VirtualKeys.F12,
    };

    /// <summary>Player 1 on arrows, Z = B, X = A, Enter = Start, Right Shift = Select. Player 2 has no default.</summary>
    public static KeyboardMapping CreateDefault()
    {
        var mapping = new KeyboardMapping();
        mapping.Bind(VirtualKeys.Up, 0, ControllerState.Up);
        mapping.Bind(VirtualKeys.Down, 0, ControllerState.Down);
        mapping.Bind(VirtualKeys.Left, 0, ControllerState.Left);
        mapping.Bind(VirtualKeys.Right, 0, ControllerState.Right);
        mapping.Bind(VirtualKeys.Z, 0, ControllerState.B);
        mapping.Bind(VirtualKeys.X, 0, ControllerState.A);
        mapping.Bind(VirtualKeys.Enter, 0, ControllerState.Start);
        mapping.Bind(VirtualKeys.RightShift, 0, ControllerState.Select);
        return mapping;
    }

    public void Bind(int virtualKey, int port, ControllerState button)
    {
        if (Hotkeys.Contains(virtualKey))
        {
            throw new ArgumentException($"Key 0x{virtualKey:X2} is reserved for a hotkey.", nameof(virtualKey));
        }

        _bindings[virtualKey] = (port, button);
    }

    public bool TryGetBinding(int virtualKey, out int port, out ControllerState button)
    {
        if (_bindings.TryGetValue(virtualKey, out var binding))
        {
            (port, button) = binding;
            return true;
        }

        port = 0;
        button = ControllerState.None;
        return false;
    }
}
