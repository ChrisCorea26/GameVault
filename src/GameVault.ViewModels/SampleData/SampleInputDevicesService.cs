using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels.SampleData;

/// <summary>
/// <c>--sample-data</c> devices (docs/design/input.md §9): the keyboard and an Xbox Wireless Controller on
/// Player 1, an 8BitDo SN30 Pro on Player 2, and an unassigned DualSense Wireless Controller.
/// </summary>
public sealed class SampleInputDevicesService : IInputDevicesService
{
    private readonly InputDevicesModel _model = new(new PortAssignmentPolicy());

    public SampleInputDevicesService()
    {
        _model.Connect(new ControllerInfo(1, "xbox", "Xbox Wireless Controller"));
        _model.Connect(new ControllerInfo(2, "8bitdo", "8BitDo SN30 Pro"));
        _model.Connect(new ControllerInfo(3, "dualsense", "DualSense Wireless Controller"));
    }

    public IReadOnlyList<InputPortInfo> Ports => _model.BuildPorts();

    public IReadOnlyList<InputSourceInfo> Unassigned => _model.BuildUnassigned();

    public bool TwoPlayerKeyboard
    {
        get => _model.TwoPlayerKeyboard;
        set
        {
            _model.TwoPlayerKeyboard = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public int TurboPeriodFrames { get; set; } = 4;

    public event EventHandler? Changed;

    public event EventHandler<string>? Notice;

    public event EventHandler<ControllerDisconnectedEventArgs>? ControllerDisconnected;

    public void SetGameRunning(bool running) => _model.Policy.GameRunning = running;

    public void SetPlayerCount(int count)
    {
        foreach (var notice in _model.Policy.SetPortCount(count))
        {
            Notice?.Invoke(this, notice);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SwapPorts(int a, int b)
    {
        _model.Policy.Swap(a, b);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AssignToPort(string sourceId, int port)
    {
        if (_model.AssignToPort(sourceId, port) is { } notice)
        {
            Notice?.Invoke(this, notice);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(string sourceId, bool pinned)
    {
        _model.SetPinned(sourceId, pinned);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Simulates unplugging a pad during play (for previews and tests).</summary>
    public void SimulateDisconnect(int instanceId, string name)
    {
        if (_model.Disconnect(instanceId) is { } port && _model.Policy.GameRunning)
        {
            ControllerDisconnected?.Invoke(this, new ControllerDisconnectedEventArgs(port, name));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
