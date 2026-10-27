using System.Diagnostics;
using System.Runtime.InteropServices;
using GameVault.ViewModels.Services.Input;
using Silk.NET.SDL;
using Thread = System.Threading.Thread;

namespace GameVault.Platform.Input.Controllers;

/// <summary>
/// SDL2 GameController input via Silk.NET (SPEC §2, §5.4) on its own polling thread. SDL's built-in
/// controller database covers Xbox, PlayStation, Switch Pro and 8BitDo pads with no setup screen.
/// </summary>
public sealed unsafe class SdlControllerBackend : IControllerBackend
{
    private const int PollIntervalMs = 4;

    private Thread? _thread;
    private volatile bool _stop;
    private IControllerEvents? _events;

    // Rumble asked for by the game, applied on the polling thread (SDL's controllers belong to it).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, float> _rumble = new();

    /// <summary>How long one SDL rumble request lasts; it's renewed while the motor should run.</summary>
    private const uint RumbleMs = 500;

    /// <summary>Why SDL couldn't start, or null if it did (or hasn't been started).</summary>
    public string? StartError { get; private set; }

    public void Start(IControllerEvents events)
    {
        if (_thread is not null)
        {
            return;
        }

        _events = events;
        _thread = new Thread(Run) { Name = "GameVault controllers", IsBackground = true };
        _thread.Start();
    }

    public void SetRumble(int instanceId, float level) => _rumble[instanceId] = Math.Clamp(level, 0f, 1f);

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        Sdl sdl;
        try
        {
            sdl = Sdl.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
        {
            StartError = ex.Message; // play continues with the keyboard
            return;
        }

        // Positional buttons, so Nintendo-layout pads keep B on the left and A on the right (input.md §6).
        sdl.SetHint(Sdl.HintGamecontrollerUseButtonLabels, "0");

        // There is no SDL window, so SDL can't tell whether we're in the foreground; ControllerManager does that.
        sdl.SetHint(Sdl.HintJoystickAllowBackgroundEvents, "1");

        if (sdl.Init(Sdl.InitGamecontroller | Sdl.InitJoystick | Sdl.InitEvents) != 0)
        {
            StartError = sdl.GetErrorS();
            return;
        }

        var open = new Dictionary<int, nint>();
        var clock = Stopwatch.StartNew();
        var events = _events!;
        Event e;
        try
        {
            while (!_stop)
            {
                while (sdl.PollEvent(&e) != 0)
                {
                    switch ((EventType)e.Type)
                    {
                        case EventType.Controllerdeviceadded:
                            OnAdded(sdl, e.Cdevice.Which, open, events);
                            break;
                        case EventType.Controllerdeviceremoved:
                            if (open.Remove(e.Cdevice.Which, out var removed))
                            {
                                sdl.GameControllerClose((GameController*)removed);
                                events.OnDisconnected(e.Cdevice.Which);
                            }

                            break;
                        case EventType.Controllerbuttondown:
                        case EventType.Controllerbuttonup:
                            if (e.Cbutton.Button <= (byte)PadButton.DPadRight)
                            {
                                events.OnButton(e.Cbutton.Which, (PadButton)e.Cbutton.Button, (EventType)e.Type == EventType.Controllerbuttondown);
                            }

                            break;
                        case EventType.Controlleraxismotion:
                            if (e.Caxis.Axis <= (byte)PadAxis.TriggerRight)
                            {
                                events.OnAxis(e.Caxis.Which, (PadAxis)e.Caxis.Axis, e.Caxis.Value);
                            }

                            break;
                    }
                }

                ApplyRumble(sdl, open, clock.ElapsedMilliseconds);
                events.OnTick(clock.ElapsedMilliseconds);
                Thread.Sleep(PollIntervalMs);
            }
        }
        finally
        {
            foreach (var controller in open.Values)
            {
                sdl.GameControllerClose((GameController*)controller);
            }

            sdl.Quit();
        }
    }

    private readonly Dictionary<int, (float Level, long SentMs)> _rumbleSent = [];

    /// <summary>Sends each pad's motor level to SDL when it changes, and renews a running motor before it times out.</summary>
    private void ApplyRumble(Sdl sdl, Dictionary<int, nint> open, long nowMs)
    {
        foreach (var (id, level) in _rumble)
        {
            var known = _rumbleSent.TryGetValue(id, out var sent);
            if (known && sent.Level == level && (level == 0 || nowMs - sent.SentMs < RumbleMs / 2))
            {
                continue;
            }

            if (open.TryGetValue(id, out var controller))
            {
                var strength = (ushort)(level * 0xFFFF);
                sdl.GameControllerRumble((GameController*)controller, strength, strength, level == 0 ? 0 : RumbleMs);
            }

            _rumbleSent[id] = (level, nowMs);
        }
    }

    private static void OnAdded(Sdl sdl, int deviceIndex, Dictionary<int, nint> open, IControllerEvents events)
    {
        var controller = sdl.GameControllerOpen(deviceIndex);
        if (controller is null)
        {
            return;
        }

        var joystick = sdl.GameControllerGetJoystick(controller);
        var instanceId = sdl.JoystickInstanceID(joystick);
        if (open.ContainsKey(instanceId))
        {
            sdl.GameControllerClose(controller);
            return;
        }

        open[instanceId] = (nint)controller;
        var name = sdl.GameControllerNameS(controller) ?? "Game controller";
        var serial = sdl.GameControllerGetSerialS(controller);

        var guidText = stackalloc byte[33];
        sdl.JoystickGetGUIDString(sdl.JoystickGetGUID(joystick), guidText, 33);
        var guid = Marshal.PtrToStringAnsi((nint)guidText) ?? string.Empty;

        // Persistent identity for pins and reservations (input.md §3.5).
        var key = string.IsNullOrEmpty(serial) ? $"{guid}|{name}" : $"{guid}|{serial}";
        events.OnConnected(new ControllerInfo(instanceId, key, name));
    }
}
