namespace GameVault.ViewModels.Services.Input;

/// <summary>
/// Shared logic for <see cref="IInputDevicesService"/> implementations: builds the port/source view from a
/// <see cref="PortAssignmentPolicy"/> and applies user actions to it.
/// </summary>
public sealed class InputDevicesModel
{
    private readonly Dictionary<string, ControllerInfo> _byId = new(StringComparer.Ordinal);

    public InputDevicesModel(PortAssignmentPolicy policy)
    {
        Policy = policy;
    }

    public PortAssignmentPolicy Policy { get; }

    public bool TwoPlayerKeyboard { get; set; }

    public static string IdOf(ControllerInfo controller) => $"pad:{controller.InstanceId}";

    public string? Connect(ControllerInfo controller)
    {
        _byId[IdOf(controller)] = controller;
        return Policy.Connect(controller);
    }

    public int? Disconnect(int instanceId)
    {
        _byId.Remove($"pad:{instanceId}");
        return Policy.Disconnect(instanceId);
    }

    public bool TryGetController(string sourceId, out ControllerInfo controller) =>
        _byId.TryGetValue(sourceId, out controller!);

    public IReadOnlyList<InputPortInfo> BuildPorts()
    {
        var ports = new List<InputPortInfo>();
        for (var port = 0; port < Policy.PortCount; port++)
        {
            var sources = new List<InputSourceInfo>();
            if (port == 0 || (port == 1 && TwoPlayerKeyboard))
            {
                sources.Add(new InputSourceInfo(IInputDevicesService.KeyboardId, "Keyboard", IsKeyboard: true, IsPinned: false));
            }

            if (Policy.AssignedTo(port) is { } pad)
            {
                sources.Add(new InputSourceInfo(IdOf(pad), pad.Name, IsKeyboard: false, Policy.IsPinned(pad.Key)));
            }

            ports.Add(new InputPortInfo(port, sources, Policy.WaitingFor(port)));
        }

        return ports;
    }

    public IReadOnlyList<InputSourceInfo> BuildUnassigned() =>
        Policy.Unassigned.Select(c => new InputSourceInfo(IdOf(c), c.Name, IsKeyboard: false, Policy.IsPinned(c.Key))).ToList();

    public string? AssignToPort(string sourceId, int port) =>
        TryGetController(sourceId, out var controller) ? Policy.AssignToPort(controller.InstanceId, port) : null;

    public void SetPinned(string sourceId, bool pinned)
    {
        if (!TryGetController(sourceId, out var controller))
        {
            return;
        }

        if (!pinned)
        {
            Policy.Unpin(controller.Key);
        }
        else if (Policy.PortOf(controller.InstanceId) is { } port)
        {
            Policy.Pin(controller.Key, port);
        }
    }

    /// <summary>Pins as stored in settings: "key=port" entries separated by '|'.</summary>
    public static string SerializePins(IReadOnlyDictionary<string, int> pins) =>
        string.Join('|', pins.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

    public static IEnumerable<KeyValuePair<string, int>> ParsePins(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (var entry in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.LastIndexOf('=');
            if (split > 0 && int.TryParse(entry[(split + 1)..], out var port))
            {
                yield return new KeyValuePair<string, int>(entry[..split], port);
            }
        }
    }
}
