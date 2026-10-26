using GameVault.Core;
using GameVault.Library.Memory;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>What one frame showed: a life lost (with a state from a few seconds earlier), a new room, overlay values.</summary>
public readonly record struct WatchResult(
    byte[]? BeforeDeathState,
    ushort[]? BeforeDeathFrame,
    (int Area, int Room)? NewRoom,
    IReadOnlyList<StatValue>? Overlay)
{
    public bool IsEmpty => BeforeDeathState is null && NewRoom is null && Overlay is null;
}

/// <summary>
/// Watches the running game through its memory map (SPEC §7.9). It runs on the emulation thread after each
/// real frame, never during rewind or run-ahead's hidden frames, and reads only the bytes the map lists, with
/// side-effect-free peeks.
/// <list type="bullet">
/// <item><b>Lives:</b> when the lives value drops while playing, it hands back the state from about
/// <see cref="SecondsBeforeDeath"/> seconds earlier ("rewind to before I died").</item>
/// <item><b>Rooms:</b> each change of (area, room) while playing, for automatic maps.</item>
/// <item><b>Overlay:</b> the overlay values about 6 times a second, when they change.</item>
/// </list>
/// After a state load or a rewind, <see cref="Resync"/> makes it take the new values as its starting point,
/// so jumping around in time never looks like a death.
/// </summary>
public sealed class GameWatcher
{
    public const int SecondsBeforeDeath = 5;
    private const int FramesPerSecond = 60;
    private const int StatesKept = 10;
    private const int OverlayInterval = 10;

    private readonly Queue<(long Frame, byte[] State, ushort[] Screen)> _recent = new();
    private MemoryMap? _map;
    private NesConsole? _console;
    private Func<int, byte> _readRam;
    private long _frame;
    private int? _lives;
    private (int, int)? _room;
    private bool _resync = true;
    private IReadOnlyList<StatValue> _overlay = [];

    public GameWatcher() => _readRam = address => _console!.Bus.Peek((ushort)address);

    /// <summary>The running game's memory map, or null (the watcher then does nothing).</summary>
    public MemoryMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            _recent.Clear();
            _overlay = [];
            Resync();
        }
    }

    public void Resync() => _resync = true;

    public WatchResult OnFrame(NesConsole console)
    {
        if (_map is not { } map)
        {
            return default;
        }

        _console = console;
        _frame++;
        if (_frame % FramesPerSecond == 0)
        {
            _recent.Enqueue((_frame, console.SaveSnapshot(), console.Ppu.FrameBuffer.ToArray()));
            while (_recent.Count > StatesKept)
            {
                _recent.Dequeue();
            }
        }

        var playing = map.IsPlaying(_readRam);
        int? lives = playing && map.Lives is { } livesName ? map.Read(livesName, _readRam) : null;
        (int, int)? room = playing && map.Map is { } layout
            ? (layout.Area is { } area ? map.Read(area, _readRam) : 0, map.Read(layout.Room, _readRam))
            : null;

        byte[]? beforeDeath = null;
        ushort[]? beforeDeathScreen = null;
        (int, int)? newRoom = null;
        if (_resync)
        {
            _resync = false;
        }
        else
        {
            if (lives is { } now && _lives is { } before && now < before && _recent.Count > 0)
            {
                // The newest saved state at least a few seconds old, or the oldest there is.
                var target = _frame - SecondsBeforeDeath * FramesPerSecond;
                var pick = _recent.LastOrDefault(s => s.Frame <= target);
                if (pick.State is null)
                {
                    pick = _recent.Peek();
                }

                beforeDeath = pick.State;
                beforeDeathScreen = pick.Screen;
            }

            if (room is { } current && current != _room)
            {
                newRoom = current;
            }
        }

        // Outside gameplay keep the last known values, so a pause menu or a cut scene doesn't count as a change.
        _lives = lives ?? _lives;
        _room = room ?? _room;

        IReadOnlyList<StatValue>? overlay = null;
        if (_frame % OverlayInterval == 0 && map.Overlay.Count > 0)
        {
            var values = map.Overlay
                .Where(o => o.When.All(c => c.Holds(map, _readRam)))
                .Select(o => new StatValue(o.Label, Format(map, o.Value)))
                .ToList();
            if (!values.SequenceEqual(_overlay))
            {
                _overlay = values;
                overlay = values;
            }
        }

        return new WatchResult(beforeDeath, beforeDeathScreen, newRoom, overlay);
    }

    private string Format(MemoryMap map, string name)
    {
        var value = map.Values[name];
        var number = value.Read(_readRam);
        return value.Max is { } max ? $"{number} of {max}" : number.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }
}
