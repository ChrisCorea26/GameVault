using System.Text.Json;
using System.Text.Json.Serialization;
using GameVault.Core.Input;

namespace GameVault.ViewModels.Services.Input;

/// <summary>
/// The pads GameVault stands in for (docs/design/input.md §6). The Master System and SG-1000 share the NES's bindings
/// in the core, but their buttons have their own names and their own remaps.
/// </summary>
public enum InputSystem
{
    Nes,
    Sega,
    Genesis,
    Snes,
    PlayStation,
    Nintendo64,
    PlayStation2,
}

/// <summary>
/// How a keyboard key reaches the game. N64 and PS2 games read the arrows as the analog stick, so their D-pad keys and
/// the PS2's right-stick keys are held apart (docs/design/input.md §6.5, §6.6).
/// </summary>
public enum KeyKind
{
    Normal,
    DPad,
    RightStick,
}

/// <summary>
/// One thing a player can press, named the way the system's own pad names it (GV-018): its controller-state bit, whether
/// it is a turbo button, and its default keyboard key and pad button.
/// </summary>
/// <param name="Id">Stable name stored in settings, such as "B" or "TurboA".</param>
/// <param name="DefaultKey">The default key (a Windows virtual-key code) for Player 1, or 0 for none.</param>
/// <param name="DefaultPad">The default pad input (<see cref="InputCodes"/>), or null for none.</param>
/// <param name="KeyboardOnly">True for actions a pad does with a stick (the N64's and PS2's analog sticks).</param>
/// <param name="PadFixed">True when a pad does it with a trigger or a stick that can't be moved (shown, not remappable).</param>
public sealed record InputAction(
    string Id,
    string Label,
    ControllerState Button,
    bool Turbo,
    KeyKind Kind,
    int DefaultKey,
    string? DefaultPad,
    bool KeyboardOnly = false,
    bool PadFixed = false);

/// <summary>A changed binding, ready for the input layer: the target it presses and the input that now presses it.</summary>
/// <param name="Input">An <see cref="InputCodes"/> code, or <see cref="InputCodes.None"/> to leave the action unbound.</param>
public sealed record ResolvedRemap(ControllerState Button, bool Turbo, KeyKind Kind, string Input);

/// <summary>Inputs as stored in settings: "key:90" (a virtual-key code), "pad:South" (a pad button by position), or "none".</summary>
public static class InputCodes
{
    public const string None = "none";

    public static string Key(int virtualKey) => $"key:{virtualKey}";

    public static string Pad(string button) => $"pad:{button}";

    public static bool TryKey(string? code, out int virtualKey)
    {
        virtualKey = 0;
        return code is not null && code.StartsWith("key:", StringComparison.Ordinal) && int.TryParse(code.AsSpan(4), out virtualKey);
    }

    public static bool TryPad(string? code, out string button)
    {
        button = code is not null && code.StartsWith("pad:", StringComparison.Ordinal) ? code[4..] : string.Empty;
        return button.Length > 0;
    }

    /// <summary>What a binding looks like in Settings: "Z", "Right Shift", "South button (A)", "—".</summary>
    public static string Describe(string? code) =>
        TryKey(code, out var key) ? KeyName(key)
        : TryPad(code, out var button) ? PadName(button)
        : code is { } fixedInput && fixedInput.StartsWith("fixed:", StringComparison.Ordinal) ? fixedInput[6..]
        : "—";

    public static string PadName(string button) => button switch
    {
        "South" => "Bottom button (Xbox A, PlayStation Cross)",
        "East" => "Right button (Xbox B, PlayStation Circle)",
        "West" => "Left button (Xbox X, PlayStation Square)",
        "North" => "Top button (Xbox Y, PlayStation Triangle)",
        "Back" => "View / Select",
        "Start" => "Menu / Start",
        "LeftShoulder" => "Left shoulder (LB, L1)",
        "RightShoulder" => "Right shoulder (RB, R1)",
        "LeftStick" => "Left stick click",
        "RightStick" => "Right stick click",
        "DPadUp" => "D-pad up",
        "DPadDown" => "D-pad down",
        "DPadLeft" => "D-pad left",
        "DPadRight" => "D-pad right",
        "Guide" => "Guide",
        _ => button,
    };

    /// <summary>A key's name as printed on a US keyboard.</summary>
    public static string KeyName(int key) => key switch
    {
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Num {key - 0x60}",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0xA0 => "Left Shift",
        0xA1 => "Right Shift",
        0xA2 => "Left Ctrl",
        0xA3 => "Right Ctrl",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key {key}",
    };
}

/// <summary>
/// The actions of each system's pad with their defaults (docs/design/input.md §4.1, §6). The input layer keeps its own
/// default tables; a test checks they agree with these.
/// </summary>
public static class InputActions
{
    // Windows virtual-key codes for the defaults.
    private const int Up = 0x26, Down = 0x28, Left = 0x25, Right = 0x27, Enter = 0x0D, RightShift = 0xA1;
    private const int KeyA = 0x41, KeyC = 0x43, KeyE = 0x45, KeyF = 0x46, KeyG = 0x47, KeyH = 0x48, KeyI = 0x49, KeyJ = 0x4A;
    private const int KeyK = 0x4B, KeyL = 0x4C, KeyQ = 0x51, KeyS = 0x53, KeyT = 0x54, KeyW = 0x57, KeyX = 0x58, KeyZ = 0x5A;
    private const int Digit1 = 0x31, Digit2 = 0x32, Digit3 = 0x33, Digit4 = 0x34, Digit5 = 0x35, Digit6 = 0x36;

    public static IReadOnlyList<InputSystem> Systems { get; } = Enum.GetValues<InputSystem>();

    public static string SystemName(InputSystem system) => system switch
    {
        InputSystem.Nes => "NES",
        InputSystem.Sega => "Master System and SG-1000",
        InputSystem.Genesis => "Genesis / Mega Drive",
        InputSystem.Snes => "Super Nintendo",
        InputSystem.PlayStation => "PlayStation",
        InputSystem.Nintendo64 => "Nintendo 64",
        InputSystem.PlayStation2 => "PlayStation 2",
        _ => system.ToString(),
    };

    private static InputAction[] DPad(string prefix = "") =>
    [
        new("Up", $"{prefix}Up", ControllerState.Up, false, KeyKind.Normal, Up, "pad:DPadUp"),
        new("Down", $"{prefix}Down", ControllerState.Down, false, KeyKind.Normal, Down, "pad:DPadDown"),
        new("Left", $"{prefix}Left", ControllerState.Left, false, KeyKind.Normal, Left, "pad:DPadLeft"),
        new("Right", $"{prefix}Right", ControllerState.Right, false, KeyKind.Normal, Right, "pad:DPadRight"),
    ];

    /// <summary>N64 and PS2: the arrows push the (left) stick and T/F/G/H are the D-pad.</summary>
    private static InputAction[] StickAndDPad(string stick) =>
    [
        new("StickUp", $"{stick} up", ControllerState.Up, false, KeyKind.Normal, Up, null, KeyboardOnly: true),
        new("StickDown", $"{stick} down", ControllerState.Down, false, KeyKind.Normal, Down, null, KeyboardOnly: true),
        new("StickLeft", $"{stick} left", ControllerState.Left, false, KeyKind.Normal, Left, null, KeyboardOnly: true),
        new("StickRight", $"{stick} right", ControllerState.Right, false, KeyKind.Normal, Right, null, KeyboardOnly: true),
        new("Up", "D-pad up", ControllerState.Up, false, KeyKind.DPad, KeyT, "pad:DPadUp"),
        new("Down", "D-pad down", ControllerState.Down, false, KeyKind.DPad, KeyG, "pad:DPadDown"),
        new("Left", "D-pad left", ControllerState.Left, false, KeyKind.DPad, KeyF, "pad:DPadLeft"),
        new("Right", "D-pad right", ControllerState.Right, false, KeyKind.DPad, KeyH, "pad:DPadRight"),
    ];

    private static readonly Dictionary<InputSystem, IReadOnlyList<InputAction>> Catalog = new()
    {
        [InputSystem.Nes] =
        [
            .. DPad(),
            new("B", "B", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:South"),
            new("A", "A", ControllerState.A, false, KeyKind.Normal, KeyX, "pad:East"),
            new("Select", "Select", ControllerState.Select, false, KeyKind.Normal, RightShift, "pad:Back"),
            new("Start", "Start", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
            new("TurboB", "Turbo B", ControllerState.B, true, KeyKind.Normal, KeyA, "pad:West"),
            new("TurboA", "Turbo A", ControllerState.A, true, KeyKind.Normal, KeyS, "pad:North"),
        ],
        [InputSystem.Sega] =
        [
            .. DPad(),
            new("B", "Button 1", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:South"),
            new("A", "Button 2", ControllerState.A, false, KeyKind.Normal, KeyX, "pad:East"),
            new("Start", "Pause", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
            new("TurboB", "Turbo 1", ControllerState.B, true, KeyKind.Normal, KeyA, "pad:West"),
            new("TurboA", "Turbo 2", ControllerState.A, true, KeyKind.Normal, KeyS, "pad:North"),
        ],

        // On the Genesis the state's B is the pad's A and its A is the pad's B (ControllerState remarks).
        [InputSystem.Genesis] =
        [
            .. DPad(),
            new("B", "A", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:West"),
            new("A", "B", ControllerState.A, false, KeyKind.Normal, KeyX, "pad:South"),
            new("C", "C", ControllerState.C, false, KeyKind.Normal, KeyC, "pad:East"),
            new("X", "X", ControllerState.X, false, KeyKind.Normal, KeyQ, "pad:LeftShoulder"),
            new("Y", "Y", ControllerState.Y, false, KeyKind.Normal, KeyW, "pad:North"),
            new("Z", "Z", ControllerState.Z, false, KeyKind.Normal, KeyE, "pad:RightShoulder"),
            new("Select", "Mode", ControllerState.Select, false, KeyKind.Normal, RightShift, "pad:Back"),
            new("Start", "Start", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
            new("TurboB", "Turbo A", ControllerState.B, true, KeyKind.Normal, KeyA, null),
            new("TurboA", "Turbo B", ControllerState.A, true, KeyKind.Normal, KeyS, null),
        ],
        [InputSystem.Snes] =
        [
            .. DPad(),
            new("B", "B", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:South"),
            new("A", "A", ControllerState.A, false, KeyKind.Normal, KeyX, "pad:East"),
            new("Y", "Y", ControllerState.Y, false, KeyKind.Normal, KeyA, "pad:West"),
            new("X", "X", ControllerState.X, false, KeyKind.Normal, KeyS, "pad:North"),
            new("L", "L", ControllerState.L, false, KeyKind.Normal, Digit1, "pad:LeftShoulder"),
            new("R", "R", ControllerState.R, false, KeyKind.Normal, Digit2, "pad:RightShoulder"),
            new("Select", "Select", ControllerState.Select, false, KeyKind.Normal, RightShift, "pad:Back"),
            new("Start", "Start", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
        ],
        [InputSystem.PlayStation] =
        [
            .. DPad(),
            .. PlayStationButtons(),
        ],
        [InputSystem.Nintendo64] =
        [
            .. StickAndDPad("Stick"),
            new("B", "A", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:South"),
            new("Y", "B", ControllerState.Y, false, KeyKind.Normal, KeyA, "pad:West"),
            new("L2", "Z", ControllerState.L2, false, KeyKind.Normal, Digit3, "fixed:Either trigger", PadFixed: true),
            new("L", "L", ControllerState.L, false, KeyKind.Normal, Digit1, "pad:LeftShoulder"),
            new("R", "R", ControllerState.R, false, KeyKind.Normal, Digit2, "pad:RightShoulder"),
            new("Start", "Start", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
            new("C", "C-up", ControllerState.C, false, KeyKind.Normal, KeyI, "fixed:Right stick up", PadFixed: true),
            new("A", "C-down", ControllerState.A, false, KeyKind.Normal, KeyK, "pad:East"),
            new("X", "C-left", ControllerState.X, false, KeyKind.Normal, KeyJ, "pad:North"),
            new("Z", "C-right", ControllerState.Z, false, KeyKind.Normal, KeyL, "fixed:Right stick right", PadFixed: true),
        ],
        [InputSystem.PlayStation2] =
        [
            .. StickAndDPad("Left stick"),
            new("RightStickUp", "Right stick up", ControllerState.Up, false, KeyKind.RightStick, KeyI, null, KeyboardOnly: true),
            new("RightStickDown", "Right stick down", ControllerState.Down, false, KeyKind.RightStick, KeyK, null, KeyboardOnly: true),
            new("RightStickLeft", "Right stick left", ControllerState.Left, false, KeyKind.RightStick, KeyJ, null, KeyboardOnly: true),
            new("RightStickRight", "Right stick right", ControllerState.Right, false, KeyKind.RightStick, KeyL, null, KeyboardOnly: true),
            .. PlayStationButtons(),
            new("L3", "L3", ControllerState.L3, false, KeyKind.Normal, Digit5, "pad:LeftStick"),
            new("R3", "R3", ControllerState.R3, false, KeyKind.Normal, Digit6, "pad:RightStick"),
        ],
    };

    private static InputAction[] PlayStationButtons() =>
    [
        new("B", "Cross", ControllerState.B, false, KeyKind.Normal, KeyZ, "pad:South"),
        new("A", "Circle", ControllerState.A, false, KeyKind.Normal, KeyX, "pad:East"),
        new("Y", "Square", ControllerState.Y, false, KeyKind.Normal, KeyA, "pad:West"),
        new("X", "Triangle", ControllerState.X, false, KeyKind.Normal, KeyS, "pad:North"),
        new("L", "L1", ControllerState.L, false, KeyKind.Normal, Digit1, "pad:LeftShoulder"),
        new("R", "R1", ControllerState.R, false, KeyKind.Normal, Digit2, "pad:RightShoulder"),
        new("L2", "L2", ControllerState.L2, false, KeyKind.Normal, Digit3, "fixed:Left trigger", PadFixed: true),
        new("R2", "R2", ControllerState.R2, false, KeyKind.Normal, Digit4, "fixed:Right trigger", PadFixed: true),
        new("Select", "Select", ControllerState.Select, false, KeyKind.Normal, RightShift, "pad:Back"),
        new("Start", "Start", ControllerState.Start, false, KeyKind.Normal, Enter, "pad:Start"),
    ];

    public static IReadOnlyList<InputAction> For(InputSystem system) => Catalog[system];

    /// <summary>An action's default input for a device: Player 1's keys, or the pad's button.</summary>
    public static string? DefaultFor(InputAction action, bool keyboard) =>
        keyboard ? (action.DefaultKey != 0 ? InputCodes.Key(action.DefaultKey) : null) : action.KeyboardOnly ? null : action.DefaultPad;

    /// <summary>
    /// The keyboard presets (SPEC §5.4): the standard layout, and one-handed layouts with everything under one hand.
    /// Each preset's keys replace the matching defaults; actions it doesn't list keep theirs unless a preset key takes
    /// their key.
    /// </summary>
    public static IReadOnlyList<string> KeyboardPresets { get; } = ["Standard", "One-handed, left hand", "One-handed, right hand"];

    /// <summary>The keys a preset sets, by action id (for the NES-style actions every system has).</summary>
    public static IReadOnlyDictionary<string, int> PresetKeys(int preset) => preset switch
    {
        // Left hand: W A S D to move, Q and E as B and A, R and F as Start and Select, 1 and 3 as turbo.
        1 => new Dictionary<string, int>
        {
            ["Up"] = KeyW, ["Down"] = KeyS, ["Left"] = KeyA, ["Right"] = 0x44, ["B"] = KeyQ, ["A"] = KeyE,
            ["Start"] = 0x52, ["Select"] = KeyF, ["TurboB"] = Digit1, ["TurboA"] = Digit3,
        },

        // Right hand: I J K L to move, U and O as B and A, P and ; as Start and Select, 7 and 9 as turbo.
        2 => new Dictionary<string, int>
        {
            ["Up"] = KeyI, ["Down"] = KeyK, ["Left"] = KeyJ, ["Right"] = KeyL, ["B"] = 0x55, ["A"] = 0x4F,
            ["Start"] = 0x50, ["Select"] = 0xBA, ["TurboB"] = 0x37, ["TurboA"] = 0x39,
        },
        _ => new Dictionary<string, int>(),
    };
}

/// <summary>One device's changes from the defaults for one player and system, everywhere or for one game.</summary>
public sealed record RemapEntry
{
    /// <summary><see cref="IInputDevicesService.KeyboardId"/>, or a controller model (the SDL GUID; input.md §6).</summary>
    public required string Device { get; init; }

    /// <summary>The port, 0–3.</summary>
    public int Player { get; init; }

    public InputSystem System { get; init; }

    /// <summary>A game key for a per-game remap, or null for every game.</summary>
    public string? Game { get; init; }

    /// <summary>Action id → input code (<see cref="InputCodes"/>), for the actions that differ from the defaults.</summary>
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Every remap the player made (SPEC §5.4: per player and per game; input.md §6: per controller model), stored as JSON in
/// <see cref="AppSettings.InputRemaps"/>. Immutable: changes return a new set.
/// </summary>
public sealed class InputRemapSet
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static InputRemapSet Empty { get; } = new([]);

    public InputRemapSet(IReadOnlyList<RemapEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<RemapEntry> Entries { get; }

    public static InputRemapSet Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return new InputRemapSet(JsonSerializer.Deserialize<List<RemapEntry>>(json, Json) ?? []);
        }
        catch (JsonException)
        {
            return Empty; // a damaged setting falls back to the defaults rather than stopping the app
        }
    }

    public string ToJson() => Entries.Count == 0 ? string.Empty : JsonSerializer.Serialize(Entries, Json);

    /// <summary>
    /// The bindings in effect for a device, player and system: the defaults, then the every-game remap, then the game's
    /// own. Keyboard defaults apply to Player 1 only (Player 2's keys come from the two-player preset).
    /// </summary>
    public IReadOnlyDictionary<string, string> Effective(string device, int player, InputSystem system, string? game, bool includeDefaults = true)
    {
        var keyboard = device == IInputDevicesService.KeyboardId;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (includeDefaults && (!keyboard || player == 0))
        {
            foreach (var action in InputActions.For(system))
            {
                if (InputActions.DefaultFor(action, keyboard) is { } input)
                {
                    result[action.Id] = input;
                }
            }
        }

        foreach (var entry in Matching(device, player, system, null))
        {
            Apply(result, entry.Bindings);
        }

        if (game is not null)
        {
            foreach (var entry in Matching(device, player, system, game))
            {
                Apply(result, entry.Bindings);
            }
        }

        return result;
    }

    /// <summary>
    /// What the input layer needs: every action whose input differs from its default, as the target and the input that
    /// now presses it (or <see cref="InputCodes.None"/>).
    /// </summary>
    public IReadOnlyList<ResolvedRemap> Resolve(string device, int player, InputSystem system, string? game)
    {
        var keyboard = device == IInputDevicesService.KeyboardId;
        var effective = Effective(device, player, system, game);
        var changes = new List<ResolvedRemap>();
        foreach (var action in InputActions.For(system))
        {
            var current = effective.GetValueOrDefault(action.Id) ?? InputCodes.None;
            var original = (!keyboard || player == 0 ? InputActions.DefaultFor(action, keyboard) : null) ?? InputCodes.None;
            if (current != original && !(action.PadFixed && !keyboard))
            {
                changes.Add(new ResolvedRemap(action.Button, action.Turbo, action.Kind, current));
            }
        }

        return changes;
    }

    /// <summary>
    /// Binds <paramref name="input"/> to an action. Any other action the same input pressed is left unbound, so one key
    /// or button does one thing.
    /// </summary>
    public InputRemapSet Bind(string device, int player, InputSystem system, string? game, string actionId, string input)
    {
        var effective = Effective(device, player, system, game);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal) { [actionId] = input };
        if (input != InputCodes.None)
        {
            foreach (var (other, code) in effective)
            {
                if (other != actionId && code == input)
                {
                    changes[other] = InputCodes.None;
                }
            }
        }

        return With(device, player, system, game, changes);
    }

    /// <summary>Applies a keyboard preset (<see cref="InputActions.KeyboardPresets"/>) to every system, for every game.</summary>
    public InputRemapSet ApplyKeyboardPreset(int player, int preset)
    {
        var set = ResetAll(IInputDevicesService.KeyboardId, player);
        foreach (var system in InputActions.Systems)
        {
            var ids = InputActions.For(system).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (action, key) in InputActions.PresetKeys(preset))
            {
                // On the N64 and PS2 the arrows are the stick: a preset's directions move the stick there.
                var id = ids.Contains("Stick" + action) ? "Stick" + action : action;
                if (ids.Contains(id))
                {
                    set = set.Bind(IInputDevicesService.KeyboardId, player, system, null, id, InputCodes.Key(key));
                }
            }
        }

        return set;
    }

    /// <summary>Back to the defaults for one device, player, system and scope.</summary>
    public InputRemapSet Reset(string device, int player, InputSystem system, string? game) =>
        new(Entries.Where(e => !Is(e, device, player, system, game)).ToList());

    /// <summary>Back to the defaults for a device and player on every system and game.</summary>
    public InputRemapSet ResetAll(string device, int player) =>
        new(Entries.Where(e => !(e.Device == device && e.Player == player)).ToList());

    public bool HasRemaps(string device, int player, InputSystem system, string? game) =>
        Entries.Any(e => Is(e, device, player, system, game) && e.Bindings.Count > 0);

    private InputRemapSet With(string device, int player, InputSystem system, string? game, Dictionary<string, string> changes)
    {
        var existing = Entries.FirstOrDefault(e => Is(e, device, player, system, game));
        var bindings = new Dictionary<string, string>(existing?.Bindings ?? [], StringComparer.Ordinal);

        // Below this scope: the defaults and, for a game, the every-game remap.
        var below = game is null
            ? new InputRemapSet([]).Effective(device, player, system, null)
            : Reset(device, player, system, game).Effective(device, player, system, game);
        foreach (var (action, input) in changes)
        {
            var inherited = below.GetValueOrDefault(action) ?? InputCodes.None;
            if (input == inherited)
            {
                bindings.Remove(action);
            }
            else
            {
                bindings[action] = input;
            }
        }

        var entries = Entries.Where(e => e != existing).ToList();
        if (bindings.Count > 0)
        {
            entries.Add(new RemapEntry { Device = device, Player = player, System = system, Game = game, Bindings = bindings });
        }

        return new InputRemapSet(entries);
    }

    private IEnumerable<RemapEntry> Matching(string device, int player, InputSystem system, string? game) =>
        Entries.Where(e => Is(e, device, player, system, game));

    private static bool Is(RemapEntry entry, string device, int player, InputSystem system, string? game) =>
        entry.Device == device && entry.Player == player && entry.System == system && entry.Game == game;

    private static void Apply(Dictionary<string, string> result, Dictionary<string, string> bindings)
    {
        foreach (var (action, input) in bindings)
        {
            result[action] = input;
        }
    }
}
