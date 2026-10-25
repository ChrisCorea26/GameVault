namespace GameVault.ViewModels.Services.Input;

/// <summary>A game controller as the assignment policy sees it.</summary>
/// <param name="InstanceId">Runtime id; unique while connected.</param>
/// <param name="Key">Persistent identity: SDL GUID + serial, or GUID + name (docs/design/input.md §3.5).</param>
/// <param name="Name">Display name, e.g. "8BitDo SN30 Pro".</param>
public sealed record ControllerInfo(int InstanceId, string Key, string Name);

/// <summary>
/// Decides which controller drives which console port (docs/design/input.md §3, §5). Pure logic: no SDL,
/// no threads, so every rule is unit-tested. Callers serialize access.
/// </summary>
/// <remarks>
/// The keyboard always drives Player 1 and isn't modeled here; a port is "free" when no pad is assigned to
/// it and no disconnected pad has it reserved.
/// </remarks>
public sealed class PortAssignmentPolicy
{
    private readonly ControllerInfo?[] _assigned;
    private readonly string?[] _reservedFor;
    private readonly Dictionary<int, ControllerInfo> _connected = [];
    private readonly Dictionary<string, int> _pins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reservedNames = new(StringComparer.Ordinal);

    public const int MaxPorts = 4;

    public PortAssignmentPolicy(int portCount = 2)
    {
        _assigned = new ControllerInfo?[MaxPorts];
        _reservedFor = new string?[MaxPorts];
        PortCount = Math.Clamp(portCount, 1, MaxPorts);
    }

    /// <summary>Ports in use: 2 normally, 4 while a Four Score game runs (input.md §11, Phase 5-6).</summary>
    public int PortCount { get; private set; }

    /// <summary>
    /// Changes how many ports are in use. Pads on ports that go away become unassigned; when ports are added,
    /// waiting pads fill them in connection order. Returns the notices to show.
    /// </summary>
    public IReadOnlyList<string> SetPortCount(int count)
    {
        count = Math.Clamp(count, 1, MaxPorts);
        var notices = new List<string>();
        if (count < PortCount)
        {
            for (var port = count; port < PortCount; port++)
            {
                _assigned[port] = null;
                _reservedFor[port] = null;
            }

            PortCount = count;
            return notices;
        }

        PortCount = count;
        foreach (var pad in Unassigned.ToList())
        {
            if (LowestFreePort() is not { } free)
            {
                break;
            }

            notices.Add(Place(pad, free));
        }

        return notices;
    }

    /// <summary>True while a game is running; disconnects only reserve ports (and pause) during play.</summary>
    public bool GameRunning { get; set; }

    public ControllerInfo? AssignedTo(int port) => _assigned[port];

    /// <summary>The name of the disconnected pad a port is waiting for, or null.</summary>
    public string? WaitingFor(int port) => _reservedFor[port] is null ? null : _reservedNames.GetValueOrDefault(_reservedFor[port]!);

    public IEnumerable<ControllerInfo> Unassigned =>
        _connected.Values.Where(c => PortOf(c.InstanceId) is null).OrderBy(c => c.InstanceId);

    public IReadOnlyDictionary<string, int> Pins => _pins;

    public int? PortOf(int instanceId)
    {
        for (var port = 0; port < _assigned.Length; port++)
        {
            if (_assigned[port]?.InstanceId == instanceId)
            {
                return port;
            }
        }

        return null;
    }

    public bool IsPinned(string key) => _pins.ContainsKey(key);

    public void LoadPins(IEnumerable<KeyValuePair<string, int>> pins)
    {
        _pins.Clear();
        foreach (var (key, port) in pins)
        {
            if (port >= 0 && port < MaxPorts)
            {
                _pins[key] = port;
            }
        }
    }

    /// <summary>A pad connected (or was already connected at startup). Returns a notice to show, or null.</summary>
    public string? Connect(ControllerInfo controller)
    {
        _connected[controller.InstanceId] = controller;

        // 1. A port reserved for this very pad (it came back after a disconnect).
        var reserved = Array.IndexOf(_reservedFor, controller.Key);
        if (reserved >= 0)
        {
            _reservedFor[reserved] = null;
            return Place(controller, reserved);
        }

        // 2. A pinned port, which pushes an unpinned pad to the next free port.
        if (_pins.TryGetValue(controller.Key, out var pinned) && pinned < PortCount && _reservedFor[pinned] is null)
        {
            if (_assigned[pinned] is { } occupant && !IsPinned(occupant.Key))
            {
                _assigned[pinned] = null;
                var moved = LowestFreePort(excluding: pinned);
                if (moved is { } port)
                {
                    _assigned[port] = occupant;
                }
            }

            if (_assigned[pinned] is null)
            {
                return Place(controller, pinned);
            }
        }

        // 3. The lowest free port; otherwise the pad waits unassigned.
        return LowestFreePort() is { } free ? Place(controller, free) : null;
    }

    /// <summary>
    /// A pad disconnected. Returns the port it was driving, if any. During play, that port stays reserved for
    /// this pad until it returns, another pad takes over, or the game closes (input.md §5).
    /// </summary>
    public int? Disconnect(int instanceId)
    {
        if (!_connected.Remove(instanceId, out var controller))
        {
            return null;
        }

        var port = PortOf(instanceId);
        if (port is not { } p)
        {
            return null;
        }

        _assigned[p] = null;
        if (GameRunning)
        {
            _reservedFor[p] = controller.Key;
            _reservedNames[controller.Key] = controller.Name;
        }

        return p;
    }

    /// <summary>
    /// An unassigned pad pressed A or Start. If a port is waiting for a disconnected pad, this pad takes it
    /// over. Returns the notice, or null if nothing changed.
    /// </summary>
    public string? TryTakeOver(int instanceId)
    {
        if (!_connected.TryGetValue(instanceId, out var controller) || PortOf(instanceId) is not null)
        {
            return null;
        }

        for (var port = 0; port < PortCount; port++)
        {
            if (_reservedFor[port] is not null)
            {
                _reservedFor[port] = null;
                return Place(controller, port);
            }
        }

        return null;
    }

    /// <summary>The game closed: waiting ports are released.</summary>
    public void ClearReservations()
    {
        Array.Clear(_reservedFor);
        _reservedNames.Clear();
    }

    public void Swap(int a, int b)
    {
        (_assigned[a], _assigned[b]) = (_assigned[b], _assigned[a]);
        (_reservedFor[a], _reservedFor[b]) = (_reservedFor[b], _reservedFor[a]);
    }

    /// <summary>"Use for Player N": moves a pad to a port; a pad already there becomes unassigned.</summary>
    public string? AssignToPort(int instanceId, int port)
    {
        if (!_connected.TryGetValue(instanceId, out var controller))
        {
            return null;
        }

        if (PortOf(instanceId) is { } current)
        {
            _assigned[current] = null;
        }

        _assigned[port] = null;
        _reservedFor[port] = null;
        return Place(controller, port);
    }

    /// <summary>"Always use this controller for Player N".</summary>
    public void Pin(string key, int port) => _pins[key] = port;

    public void Unpin(string key) => _pins.Remove(key);

    private string Place(ControllerInfo controller, int port)
    {
        _assigned[port] = controller;
        return $"{controller.Name} is now Player {port + 1}";
    }

    private int? LowestFreePort(int excluding = -1)
    {
        for (var port = 0; port < PortCount; port++)
        {
            if (port != excluding && _assigned[port] is null && _reservedFor[port] is null)
            {
                return port;
            }
        }

        return null;
    }
}
