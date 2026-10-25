using System.Collections.Concurrent;
using System.Diagnostics;
using GameVault.Core;
using GameVault.Core.Cartridge;
using GameVault.Core.Ppu;
using GameVault.Platform.Audio;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Emulation;

/// <summary>
/// Owns the emulation thread (SPEC §5.1). The UI never touches the console directly: commands go through
/// a queue and run between frames. Input is sampled as late as possible before each frame
/// (docs/design/input.md §2).
/// </summary>
/// <remarks>
/// Timing (SPEC §5.6): when an audio device is available, audio is the master clock. A frame is emulated
/// whenever the device buffer drops below target, and the resampling ratio is nudged so the buffer holds
/// steady. Without audio, frames are paced by a high-resolution timer. Holding Tab fast-forwards at up to 8×
/// with the sound muted.
/// </remarks>
public sealed class EmulatorHost : IEmulatorService, IDisposable
{
    /// <summary>NTSC: 39375000 / 655171 ≈ 60.0988 frames per second (NESdev Wiki: "Cycle reference chart").</summary>
    public const double NtscFrameRate = 39375000.0 / 655171.0;

    /// <summary>Fast-forward cap (SPEC §5.6).</summary>
    public const int MaxFastForward = 8;

    private readonly IVideoSink _video;
    private readonly InputService _input;
    private readonly ISettingsStore? _settings;
    private readonly IAudioOutput? _audio;
    private readonly float[] _samples = new float[8192];
    private AudioPacing? _pacing;
    private bool _audioActive;

    // Owned by the emulation thread: how much audio has been written since the rate measurement began.
    private long _samplesWritten;
    private Stopwatch? _rateClock;
    private readonly SynchronizationContext? _context;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _shutdown;

    // Owned by the emulation thread.
    private NesConsole? _console;
    private bool _running;
    private long _frameCount;

    public EmulatorHost(IVideoSink video, InputService input, ISettingsStore? settings = null, IAudioOutput? audio = null)
    {
        _video = video;
        _input = input;
        _settings = settings;
        _audio = audio;
        _context = SynchronizationContext.Current;
    }

    public EmulatorStatus Status { get; private set; }

    public string? GameTitle { get; private set; }

    public string? FaultMessage { get; private set; }

    /// <summary>Frames emulated since the game was loaded (diagnostics and tests).</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public event EventHandler? StatusChanged;

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameLoadException("GameVault couldn't open that file. Check that it still exists and isn't in use.", ex);
        }

        if (_settings is not null)
        {
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            _input.AllowOppositeDirections = settings.AllowOppositeDirections;
            if (_audio is not null)
            {
                _audio.Volume = Math.Clamp(settings.Volume, 0, 100) / 100f;
            }
        }

        Load(data, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Loads a game from bytes already in memory.</summary>
    public void Load(byte[] data, string title)
    {
        NesConsole console;
        try
        {
            console = NesConsole.FromBytes(data);
        }
        catch (RomFormatException ex)
        {
            throw new GameLoadException("That file isn't an NES game, or it's damaged. GameVault plays .nes files.", ex);
        }
        catch (UnsupportedMapperException ex)
        {
            throw new GameLoadException(
                $"This game uses a cartridge type GameVault doesn't support yet (mapper {ex.Mapper}).", ex);
        }

        if (_audio is not null)
        {
            // Devices start on first use, never in a constructor (SPEC §9.1).
            _audioActive = _audioActive || _audio.TryStart();
            if (_audioActive)
            {
                console.EnableAudio(_audio.SampleRate);
                _pacing ??= new AudioPacing(_audio.SampleRate);
                _audio.Flush();
            }
        }

        console.Power();
        EnsureThread();
        Post(() =>
        {
            _console = console;
            _running = true;
            Interlocked.Exchange(ref _frameCount, 0);
        });
        SetAudioPaused(false);

        GameTitle = title;
        FaultMessage = null;
        SetStatus(EmulatorStatus.Running);
    }

    public void Pause()
    {
        if (Status != EmulatorStatus.Running)
        {
            return;
        }

        Post(() => _running = false);
        SetAudioPaused(true);
        SetStatus(EmulatorStatus.Paused);
    }

    public void Resume()
    {
        if (Status != EmulatorStatus.Paused)
        {
            return;
        }

        Post(() => _running = _console is not null);
        SetAudioPaused(false);
        SetStatus(EmulatorStatus.Running);
    }

    public void Reset() => Post(() => _console?.Reset());

    public void Stop()
    {
        if (Status == EmulatorStatus.Idle)
        {
            return;
        }

        Post(() =>
        {
            _console = null;
            _running = false;
        });
        SetAudioPaused(true);
        GameTitle = null;
        SetStatus(EmulatorStatus.Idle);
    }

    /// <summary>Runs a read-only query against the console on the emulation thread, between frames.</summary>
    internal Task<T?> InspectAsync<T>(Func<NesConsole, T> query)
    {
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => result.SetResult(_console is null ? default : query(_console)));
        return result.Task;
    }

    /// <summary>Audio underruns since start (diagnostics; SPEC §10 soak target is zero).</summary>
    public long AudioUnderruns => _audio?.Underruns ?? 0;

    /// <summary>True when an audio device is the master clock.</summary>
    public bool IsAudioClocked => _audioActive;

    private void SetAudioPaused(bool paused)
    {
        if (_audio is not null)
        {
            _audio.Paused = paused;
            if (!paused)
            {
                _audio.Flush();
            }
        }
    }

    public void Dispose()
    {
        _shutdown = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        _audio?.Dispose();
    }

    private void Post(Action command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            Name = "GameVault emulation",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var frameTicks = Stopwatch.Frequency / NtscFrameRate;
        var nextFrame = (double)clock.ElapsedTicks;
        var wasFastForward = false;

        while (!_shutdown)
        {
            while (_commands.TryDequeue(out var command))
            {
                command();
            }

            if (!_running || _console is null)
            {
                _rateClock = null;
                _samplesWritten = 0;
                _wake.WaitOne();
                nextFrame = clock.ElapsedTicks;
                continue;
            }

            var console = _console;
            var fastForward = _input.IsHotkeyHeld(VirtualKeys.Tab);
            if (wasFastForward && !fastForward)
            {
                _audio?.Flush(); // resume from silence rather than a burst of stale sound
            }

            wasFastForward = fastForward;
            var audioClocked = _audioActive && !fastForward && console.Audio is not null;

            if (audioClocked && !_pacing!.ShouldRunFrame(_audio!.BufferedSamples))
            {
                _wake.WaitOne(1);
                nextFrame = clock.ElapsedTicks;
                continue;
            }

            if (audioClocked)
            {
                _rateClock ??= Stopwatch.StartNew();
                var consumed = _samplesWritten - _audio!.BufferedSamples;
                console.Audio!.ClockRate = console.CpuClockRate * _pacing!.ClockRateFactor(consumed, _rateClock.Elapsed.TotalSeconds);
            }
            else
            {
                // Paused, fast-forwarding or no device: restart the device-rate measurement afterwards.
                _rateClock = null;
                _samplesWritten = 0;
            }

            if (!TryRunFrame(console))
            {
                continue;
            }

            if (console.Audio is { } resampler)
            {
                int count;
                while ((count = resampler.Read(_samples)) > 0)
                {
                    if (audioClocked)
                    {
                        _samplesWritten += _audio!.Write(_samples.AsSpan(0, count));
                    }
                }
            }

            if (audioClocked)
            {
                nextFrame = clock.ElapsedTicks;
                continue;
            }

            nextFrame += fastForward ? frameTicks / MaxFastForward : frameTicks;
            if (!WaitUntil(clock, nextFrame))
            {
                // Fell far behind (debugger, system sleep): re-anchor instead of bursting to catch up.
                nextFrame = clock.ElapsedTicks;
            }
        }
    }

    private bool TryRunFrame(NesConsole console)
    {
        try
        {
            for (var port = 0; port < InputService.PortCount; port++)
            {
                console.SetControllerState(port, _input.Sample(port));
            }

            console.RunFrame();
            _video.Present(console.Ppu.FrameBuffer, NtscPalette.Argb);
            Interlocked.Increment(ref _frameCount);
            return true;
        }
        catch (Exception ex)
        {
            _running = false;
            _console = null;
            Fault(ex);
            return false;
        }
    }

    /// <summary>
    /// Sleeps most of the wait, then spins the last stretch for accurate pacing. Returns false if the
    /// target is already more than 100 ms in the past.
    /// </summary>
    private bool WaitUntil(Stopwatch clock, double targetTicks)
    {
        var spinMargin = Stopwatch.Frequency / 500.0; // 2 ms
        while (!_shutdown && _commands.IsEmpty)
        {
            var remaining = targetTicks - clock.ElapsedTicks;
            if (remaining <= 0)
            {
                break;
            }

            if (remaining > spinMargin)
            {
                _wake.WaitOne(TimeSpan.FromMilliseconds(1));
            }
            else
            {
                Thread.SpinWait(20);
            }
        }

        return clock.ElapsedTicks - targetTicks <= Stopwatch.Frequency / 10.0;
    }

    private void Fault(Exception ex)
    {
        Raise(() =>
        {
            FaultMessage = "The game stopped because of an emulator error. " + ex.Message;
            Status = EmulatorStatus.Faulted;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetStatus(EmulatorStatus status)
    {
        Status = status;
        Raise(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private void Raise(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
