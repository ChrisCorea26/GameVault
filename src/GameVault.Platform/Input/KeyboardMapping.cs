using GameVault.Core.Input;
using GameVault.ViewModels.Services.Input;

namespace GameVault.Platform.Input;

/// <summary>Keyboard bindings: virtual key → (port, button). Defaults per docs/design/input.md §4.1.</summary>
public sealed class KeyboardMapping
{
    private readonly Dictionary<int, Binding> _bindings = [];

    /// <summary>Keys reserved for hotkeys (input.md §8). They never reach the game.</summary>
    public static IReadOnlySet<int> Hotkeys { get; } = new HashSet<int>
    {
        VirtualKeys.Escape, VirtualKeys.Backspace, VirtualKeys.Tab, VirtualKeys.F2, VirtualKeys.F3, VirtualKeys.F4, VirtualKeys.F5, VirtualKeys.F6, VirtualKeys.F7,
        VirtualKeys.F8, VirtualKeys.F9, VirtualKeys.F11, VirtualKeys.F12,
    };

    /// <summary>
    /// Player 1 on arrows, Z = B, X = A, Enter = Start, Right Shift = Select, and turbo next to them: A = turbo B,
    /// S = turbo A (input.md §4.1). Player 2 has no default. The Genesis pad's extra buttons: C = C, and Q, W, E
    /// = X, Y, Z (so Z, X, C are the pad's A, B, C). The shoulders on the number row: 1 and 2 are L and R (the SNES's,
    /// the PlayStation's L1 and R1), 3 and 4 the PlayStation's L2 and R2.
    /// </summary>
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
        mapping.BindTurbo(VirtualKeys.A, 0, ControllerState.B);
        mapping.BindTurbo(VirtualKeys.S, 0, ControllerState.A);
        mapping.Bind(VirtualKeys.C, 0, ControllerState.C);
        mapping.Bind(VirtualKeys.Q, 0, ControllerState.X);
        mapping.Bind(VirtualKeys.W, 0, ControllerState.Y);
        mapping.Bind(VirtualKeys.E, 0, ControllerState.Z);
        mapping.Bind(VirtualKeys.Digit1, 0, ControllerState.L);
        mapping.Bind(VirtualKeys.Digit2, 0, ControllerState.R);
        mapping.Bind(VirtualKeys.Digit3, 0, ControllerState.L2);
        mapping.Bind(VirtualKeys.Digit4, 0, ControllerState.R2);
        return mapping;
    }

    /// <summary>
    /// "Two players, one keyboard" (input.md §4.3): Player 1 moves to the right half (arrows, comma = B,
    /// period = A), and Player 2 gets the left half (WASD, F = B, G = A, Q = Select, E = Start).
    /// </summary>
    public static KeyboardMapping CreateTwoPlayer()
    {
        var mapping = new KeyboardMapping();
        mapping.Bind(VirtualKeys.Up, 0, ControllerState.Up);
        mapping.Bind(VirtualKeys.Down, 0, ControllerState.Down);
        mapping.Bind(VirtualKeys.Left, 0, ControllerState.Left);
        mapping.Bind(VirtualKeys.Right, 0, ControllerState.Right);
        mapping.Bind(VirtualKeys.Comma, 0, ControllerState.B);
        mapping.Bind(VirtualKeys.Period, 0, ControllerState.A);
        mapping.Bind(VirtualKeys.RightShift, 0, ControllerState.Select);
        mapping.Bind(VirtualKeys.Enter, 0, ControllerState.Start);

        mapping.Bind(VirtualKeys.W, 1, ControllerState.Up);
        mapping.Bind(VirtualKeys.S, 1, ControllerState.Down);
        mapping.Bind(VirtualKeys.A, 1, ControllerState.Left);
        mapping.Bind(VirtualKeys.D, 1, ControllerState.Right);
        mapping.Bind(VirtualKeys.F, 1, ControllerState.B);
        mapping.Bind(VirtualKeys.G, 1, ControllerState.A);
        mapping.Bind(VirtualKeys.Q, 1, ControllerState.Select);
        mapping.Bind(VirtualKeys.E, 1, ControllerState.Start);
        return mapping;
    }

    /// <summary>
    /// This mapping for one system's pad (docs/design/input.md §6), with the player's remaps on top (GV-001): the N64's
    /// and PS2's own keys are added (the arrows push the stick there, so T/F/G/H are the D-pad), and on pads without
    /// turbo (SNES, PlayStation, N64, PS2) the turbo keys A and S become Y and X.
    /// </summary>
    public KeyboardMapping ForLayout(PadLayout layout, IReadOnlyList<(int Port, ResolvedRemap Remap)>? remaps = null)
    {
        var mapping = new KeyboardMapping();
        foreach (var (key, binding) in _bindings)
        {
            mapping._bindings[key] = binding;
        }

        if (layout is PadLayout.Snes or PadLayout.PlayStation or PadLayout.Nintendo64 or PadLayout.PlayStation2)
        {
            foreach (var (key, binding) in mapping._bindings.ToList())
            {
                if (binding.Turbo)
                {
                    var button = binding.Button == ControllerState.B ? ControllerState.Y : binding.Button == ControllerState.A ? ControllerState.X : binding.Button;
                    mapping._bindings[key] = binding with { Button = button, Turbo = false };
                }
            }
        }

        foreach (var (key, button, kind) in layout switch
        {
            PadLayout.Nintendo64 => N64Keys,
            PadLayout.PlayStation or PadLayout.PlayStation2 => Ps2Keys,
            _ => [],
        })
        {
            mapping._bindings[key] = new Binding(0, button, false, kind);
        }

        foreach (var (port, remap) in remaps ?? [])
        {
            // One key does one thing and one action has one key: drop the target's old key, then bind the new one.
            foreach (var (key, binding) in mapping._bindings.ToList())
            {
                if (binding.Port == port && binding.Button == remap.Button && binding.Turbo == remap.Turbo && binding.Kind == remap.Kind)
                {
                    mapping._bindings.Remove(key);
                }
            }

            if (InputCodes.TryKey(remap.Input, out var newKey) && !Hotkeys.Contains(newKey))
            {
                mapping._bindings[newKey] = new Binding(port, remap.Button, remap.Turbo, remap.Kind);
            }
        }

        return mapping;
    }

    /// <summary>
    /// Keys that only mean something on the N64 pad: I/J/K/L the C buttons and T/F/G/H the D-pad (input.md §6.5).
    /// </summary>
    private static readonly (int Key, ControllerState Button, KeyKind Kind)[] N64Keys =
    [
        (VirtualKeys.I, ControllerState.C, KeyKind.Normal), // C-up
        (VirtualKeys.K, ControllerState.A, KeyKind.Normal), // C-down
        (VirtualKeys.J, ControllerState.X, KeyKind.Normal), // C-left
        (VirtualKeys.L, ControllerState.Z, KeyKind.Normal), // C-right
        (VirtualKeys.T, ControllerState.Up, KeyKind.DPad),
        (VirtualKeys.G, ControllerState.Down, KeyKind.DPad),
        (VirtualKeys.F, ControllerState.Left, KeyKind.DPad),
        (VirtualKeys.H, ControllerState.Right, KeyKind.DPad),
    ];

    /// <summary>
    /// Keys that only mean something on the PS2 pad: I/J/K/L push the right stick (where the N64's C buttons are),
    /// T/F/G/H are the D-pad, and 5 and 6 are L3 and R3 (after L1, R1, L2, R2 on 1–4; input.md §6.6).
    /// </summary>
    private static readonly (int Key, ControllerState Button, KeyKind Kind)[] Ps2Keys =
    [
        (VirtualKeys.I, ControllerState.Up, KeyKind.RightStick),
        (VirtualKeys.K, ControllerState.Down, KeyKind.RightStick),
        (VirtualKeys.J, ControllerState.Left, KeyKind.RightStick),
        (VirtualKeys.L, ControllerState.Right, KeyKind.RightStick),
        (VirtualKeys.T, ControllerState.Up, KeyKind.DPad),
        (VirtualKeys.G, ControllerState.Down, KeyKind.DPad),
        (VirtualKeys.F, ControllerState.Left, KeyKind.DPad),
        (VirtualKeys.H, ControllerState.Right, KeyKind.DPad),
        (VirtualKeys.Digit5, ControllerState.L3, KeyKind.Normal),
        (VirtualKeys.Digit6, ControllerState.R3, KeyKind.Normal),
    ];

    public void Bind(int virtualKey, int port, ControllerState button)
    {
        if (Hotkeys.Contains(virtualKey))
        {
            throw new ArgumentException($"Key 0x{virtualKey:X2} is reserved for a hotkey.", nameof(virtualKey));
        }

        _bindings[virtualKey] = new Binding(port, button, false, KeyKind.Normal);
    }

    /// <summary>Binds a key to a turbo button: while held, the button is pressed and released repeatedly.</summary>
    public void BindTurbo(int virtualKey, int port, ControllerState button)
    {
        Bind(virtualKey, port, button);
        _bindings[virtualKey] = new Binding(port, button, true, KeyKind.Normal);
    }

    public bool TryGetBinding(int virtualKey, out int port, out ControllerState button) =>
        TryGetBinding(virtualKey, out port, out button, out _);

    public bool TryGetBinding(int virtualKey, out int port, out ControllerState button, out bool turbo) =>
        TryGetBinding(virtualKey, out port, out button, out turbo, out _);

    public bool TryGetBinding(int virtualKey, out int port, out ControllerState button, out bool turbo, out KeyKind kind)
    {
        if (_bindings.TryGetValue(virtualKey, out var binding))
        {
            (port, button, turbo, kind) = binding;
            return true;
        }

        port = 0;
        button = ControllerState.None;
        turbo = false;
        kind = KeyKind.Normal;
        return false;
    }

    /// <summary>The key bound to a target, or 0 (tests and the default-binding check).</summary>
    public int KeyFor(int port, ControllerState button, bool turbo, KeyKind kind = KeyKind.Normal) =>
        _bindings.Where(b => b.Value == new Binding(port, button, turbo, kind)).Select(b => b.Key).DefaultIfEmpty(0).Min();

    private readonly record struct Binding(int Port, ControllerState Button, bool Turbo, KeyKind Kind);
}
