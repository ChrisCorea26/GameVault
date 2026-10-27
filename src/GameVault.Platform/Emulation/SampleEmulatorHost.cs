using GameVault.Library.Challenges;
using GameVault.Library.Memory;
using GameVault.Library.Scanning;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The emulator service under <c>--sample-data</c>: no ROM needed. It draws an animated test pattern
/// (scrolling color bars from the NES palette) through the real video path, so the Player, GameView and
/// swap chain can all be previewed and smoke-tested. The system comes from the file's extension, so a sample
/// PlayStation 2 game (an .iso) reports itself as one and draws a 640×448 frame, as the PS2's usual screen.
/// </summary>
public sealed class SampleEmulatorHost : IEmulatorService, IDisposable
{
    private readonly IVideoSink _video;
    private readonly SynchronizationContext? _context;
    private readonly ushort[] _frame = new ushort[IVideoSink.Width * IVideoSink.Height];
    private readonly ushort[] _wideFrame = new ushort[WideWidth * WideHeight];

    // The PS2's usual NTSC picture.
    private const int WideWidth = 640;
    private const int WideHeight = 448;
    private Timer? _timer;
    private int _tick;

    public SampleEmulatorHost(IVideoSink video)
    {
        _video = video;
        _context = SynchronizationContext.Current;
    }

    public EmulatorStatus Status { get; private set; }

    public string? GameTitle { get; private set; }

    /// <summary>The sample game's system, from its file's extension (the NES for anything unknown); null when idle.</summary>
    public GameVault.Core.Systems.GameSystem? System { get; private set; }

    private bool IsWide => System == GameVault.Core.Systems.GameSystem.PlayStation2;

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
            return Task.FromResult<byte[]?>(IsWide
                ? PngEncoder.Encode(PngEncoder.ToArgb(_wideFrame, GameVault.Core.Ppu.NtscPalette.Argb), WideWidth, WideHeight)
                : PngEncoder.Encode(PngEncoder.ToArgb(_frame, GameVault.Core.Ppu.NtscPalette.Argb), 256, 240));
        }
    }

    public bool IsRewinding => false;

    public double RewindSeconds => 42;

    public Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        GameTitle = "Sample Game";
        GameKey = "sample:2";
        System = RomFiles.SystemOf(path) ?? GameVault.Core.Systems.GameSystem.Nes;
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

    public bool NetplayNeedsReply { get; private set; }

    public Task<string> HostInternetNetplayAsync()
    {
        NetplayInviteCode = "M3TQ8-ZK2RW";
        NetplayNeedsReply = true;
        NetplayStatus = NetplayStatus.WaitingForPlayer;
        NetplayChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(NetplayInviteCode);
    }

    public Task AcceptNetplayReplyAsync(string replyCode)
    {
        NetplayNeedsReply = false;
        NetplayStatus = NetplayStatus.Playing;
        NetplayChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
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
        NetplayNeedsReply = false;
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
        System = null;
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
            if (IsWide)
            {
                // The same pattern stretched to the PS2's 640×448, shown at a 4:3 screen's shape.
                for (var y = 0; y < WideHeight; y++)
                {
                    var row = y * IVideoSink.Height / WideHeight * IVideoSink.Width;
                    for (var x = 0; x < WideWidth; x++)
                    {
                        _wideFrame[y * WideWidth + x] = _frame[row + x * IVideoSink.Width / WideWidth];
                    }
                }

                _video.Present(_wideFrame, GameVault.Core.Ppu.NtscPalette.Argb, WideWidth, WideHeight, 4.0 / 3.0 / ((double)WideWidth / WideHeight));
            }
            else
            {
                _video.Present(_frame, []);
            }
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

    // ---------------------------------------------------------------- challenges (SPEC §7.9), for previewing the flyout

    /// <summary>A challenge for the sample game, offered by the sample challenge files.</summary>
    public static ChallengeFile SampleChallenge { get; } = new()
    {
        Id = "sample-challenge",
        RomSha1 = "sample:2",
        GameTitle = "Crystal Caverns",
        Name = "Crystal sprint",
        Note = "Grab three crystals before the bats wake up",
        Goal = [MemoryCondition.Named("crystals", ">=", 3)],
        Values = new Dictionary<string, MemoryValue> { ["crystals"] = new() { Address = 0x0050 } },
        TimeLimitFrames = 3606,
        StartState = string.Empty,
    };

    public IReadOnlyList<string> MemoryValueNames => GameKey is null ? [] : ["crystals", "health", "lives"];

    public bool IsChallengeActive { get; private set; }

    public event EventHandler<ChallengeOutcome>? ChallengeFinished;

    public Task<ChallengeFile> CreateChallengeAsync(string name, IReadOnlyList<MemoryCondition> goal, int? timeLimitFrames, string? note) =>
        GameKey is null
            ? Task.FromException<ChallengeFile>(new ChallengeException("Start a game first."))
            : Task.FromResult(SampleChallenge with
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Sample challenge" : name.Trim(),
                Note = note,
                Goal = goal,
                TimeLimitFrames = timeLimitFrames,
                Created = DateTimeOffset.Now,
            });

    /// <summary>Pretends the player reaches the goal a few seconds in.</summary>
    public Task StartChallengeAsync(ChallengeFile challenge)
    {
        IsChallengeActive = true;
        _ = FinishSampleAttemptAsync(challenge);
        return Task.CompletedTask;
    }

    private async Task FinishSampleAttemptAsync(ChallengeFile challenge)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!IsChallengeActive)
        {
            return;
        }

        IsChallengeActive = false;
        var result = new ChallengeResult
        {
            ChallengeId = challenge.Id,
            RomSha1 = challenge.RomSha1,
            Player = "Sample player",
            Frames = 2154,
            Inputs = string.Empty,
            Finished = DateTimeOffset.Now,
        };
        RaiseFinished(new ChallengeOutcome(ChallengeOutcomeKind.Done, result.Frames, result));
    }

    public void CancelChallenge()
    {
        if (IsChallengeActive)
        {
            IsChallengeActive = false;
            RaiseFinished(new ChallengeOutcome(ChallengeOutcomeKind.GaveUp, 0, null));
        }
    }

    public Task<ChallengeCheck> VerifyChallengeAsync(ChallengeFile challenge, ChallengeResult result) =>
        Task.FromResult(new ChallengeCheck(true, $"Checked: {result.Player} really did it in {ChallengeVerifier.Time(result.Frames)}."));

    private void RaiseFinished(ChallengeOutcome outcome)
    {
        if (_context is null)
        {
            ChallengeFinished?.Invoke(this, outcome);
        }
        else
        {
            _context.Post(_ => ChallengeFinished?.Invoke(this, outcome), null);
        }
    }
}
