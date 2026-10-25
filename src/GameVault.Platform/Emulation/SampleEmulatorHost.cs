using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The emulator service under <c>--sample-data</c>: no ROM needed. It draws an animated test pattern
/// (scrolling color bars from the NES palette) through the real video path, so the Player, GameView and
/// swap chain can all be previewed and smoke-tested.
/// </summary>
public sealed class SampleEmulatorHost : IEmulatorService, IDisposable
{
    private readonly IVideoSink _video;
    private readonly SynchronizationContext? _context;
    private readonly ushort[] _frame = new ushort[IVideoSink.Width * IVideoSink.Height];
    private Timer? _timer;
    private int _tick;

    public SampleEmulatorHost(IVideoSink video)
    {
        _video = video;
        _context = SynchronizationContext.Current;
    }

    public EmulatorStatus Status { get; private set; }

    public string? GameTitle { get; private set; }

    public string? FaultMessage => null;

    public event EventHandler? StatusChanged;

    public Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        GameTitle = "Sample Game";
        SetStatus(EmulatorStatus.Running);

        // The first frame is drawn right away, so a starved thread pool can't leave the screen empty.
        DrawFrame();
        _timer ??= new Timer(_ => DrawFrame(), null, TimeSpan.FromMilliseconds(1000 / 60.0), TimeSpan.FromMilliseconds(1000 / 60.0));
        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (Status == EmulatorStatus.Running)
        {
            SetStatus(EmulatorStatus.Paused);
        }
    }

    public void Resume()
    {
        if (Status == EmulatorStatus.Paused)
        {
            SetStatus(EmulatorStatus.Running);
        }
    }

    public void Reset() => Interlocked.Exchange(ref _tick, 0);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        GameTitle = null;
        SetStatus(EmulatorStatus.Idle);
    }

    public void Dispose() => _timer?.Dispose();

    private void DrawFrame()
    {
        if (Status != EmulatorStatus.Running)
        {
            return;
        }

        lock (_frame)
        {
            DrawPattern(Interlocked.Increment(ref _tick));
            _video.Present(_frame, []);
        }
    }

    private void DrawPattern(int tick)
    {
        for (var y = 0; y < IVideoSink.Height; y++)
        {
            for (var x = 0; x < IVideoSink.Width; x++)
            {
                // 12 hue bars in 4 brightness bands, scrolling right; the bottom band is grays.
                var band = y / 60;
                var hue = ((x + tick) / 21 % 12) + 1;
                var color = band == 3 ? 0x00 + ((x / 64) << 4) : (band << 4) + hue;
                _frame[(y << 8) | x] = (ushort)color;
            }
        }
    }

    private void SetStatus(EmulatorStatus status)
    {
        Status = status;
        if (_context is null || SynchronizationContext.Current == _context)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _context.Post(_ => StatusChanged?.Invoke(this, EventArgs.Empty), null);
        }
    }
}
