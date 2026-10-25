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

    public event EventHandler<string>? Notice
    {
        add { }
        remove { }
    }

    public event EventHandler? RewindChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<TimeSpan>? PlaytimeElapsed
    {
        add { }
        remove { }
    }

    /// <summary>The sample library's "Crystal Caverns", so its timeline and details show real-looking data.</summary>
    public string? GameKey { get; private set; }

    public bool UsesZapper => false;

    public bool UsesFourScore => false;

    public long FrameCount => _tick;

    public event EventHandler? ManualPasswordRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? ScreenshotRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PracticeRetryRequested
    {
        add { }
        remove { }
    }

    public event EventHandler? PracticeSplitRequested
    {
        add { }
        remove { }
    }

    public void SetAssist(AssistOptions options)
    {
    }

    public Task<byte[]?> CaptureStateAsync() => Task.FromResult<byte[]?>(BitConverter.GetBytes(_tick));

    public Task<bool> RestoreStateAsync(byte[] state)
    {
        Interlocked.Exchange(ref _tick, BitConverter.ToInt32(state));
        return Task.FromResult(true);
    }

    public Task<byte[]?> CaptureScreenshotAsync()
    {
        lock (_frame)
        {
            return Task.FromResult<byte[]?>(PngEncoder.Encode(PngEncoder.ToArgb(_frame, GameVault.Core.Ppu.NtscPalette.Argb), 256, 240));
        }
    }

    public bool IsRewinding => false;

    public double RewindSeconds => 42;

    public Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        GameTitle = "Sample Game";
        GameKey = "sample:2";
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

    // --sample-data: netplay that answers like the real thing, without a network.
    public NetplayStatus NetplayStatus { get; private set; }

    public string? NetplayInviteCode { get; private set; }

    public event EventHandler? NetplayChanged;

    public Task<string> HostNetplayAsync()
    {
        NetplayInviteCode = "K7Q2M-9XD4P";
        NetplayStatus = NetplayStatus.WaitingForPlayer;
        NetplayChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(NetplayInviteCode);
    }

    public Task JoinNetplayAsync(string inviteCode)
    {
        NetplayInviteCode = null;
        NetplayStatus = NetplayStatus.Playing;
        NetplayChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void LeaveNetplay()
    {
        NetplayInviteCode = null;
        NetplayStatus = NetplayStatus.Off;
        NetplayChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        LeaveNetplay();
        _timer?.Dispose();
        _timer = null;
        GameTitle = null;
        GameKey = null;
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
