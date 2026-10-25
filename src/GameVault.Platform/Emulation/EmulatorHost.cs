using System.Collections.Concurrent;
using System.Diagnostics;
using GameVault.Core;
using GameVault.Core.Cartridge;
using GameVault.Core.Ppu;
using GameVault.Platform.Audio;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Library.Hashing;
using GameVault.Library.Profiles;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
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
    private readonly BatterySaveStore? _batteries;
    private readonly RunAhead _runAhead = new();
    private readonly RewindBuffer _rewind = new();
    private readonly Action<NesConsole> _present;
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
    private IBatteryBacked? _battery;
    private byte[] _batterySaved = [];
    private string _batteryTitle = string.Empty;
    private bool _quickSaveHeld;
    private bool _quickLoadHeld;
    private bool _rewinding;
    private long _playedFrames;

    /// <summary>Report playtime about once a minute.</summary>
    private const int PlaytimeReportFrames = 3600;

    /// <summary>Check battery RAM for changes about once a minute (60.1 frames per second).</summary>
    private const int BatteryCheckFrames = 3600;

    public EmulatorHost(
        IVideoSink video,
        InputService input,
        ISettingsStore? settings = null,
        IAudioOutput? audio = null,
        AppPaths? paths = null)
    {
        _video = video;
        _input = input;
        _settings = settings;
        _audio = audio;
        _batteries = paths is null ? null : new BatterySaveStore(paths);
        _context = SynchronizationContext.Current;
        _present = console => _video.Present(console.Ppu.FrameBuffer, NtscPalette.Argb);
    }

    public EmulatorStatus Status { get; private set; }

    public string? GameTitle { get; private set; }

    public string? FaultMessage { get; private set; }

    /// <summary>Frames emulated since the game was loaded (diagnostics and tests).</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public event EventHandler? StatusChanged;

    public event EventHandler<string>? Notice;

    /// <summary>F5 was pressed in the game (docs/design/input.md §8). Raised on the UI thread.</summary>
    public event EventHandler? QuickSaveRequested;

    /// <summary>F8 was pressed in the game. Raised on the UI thread.</summary>
    public event EventHandler? QuickLoadRequested;

    public event EventHandler? RewindChanged;

    public event EventHandler<TimeSpan>? PlaytimeElapsed;

    /// <summary>Looks up a game's profile by headerless SHA-1 (SPEC §7.5). Null: no profiles.</summary>
    public Func<string, GameProfile>? ProfileResolver { get; set; }

    public string? GameKey { get; private set; }

    public bool UsesZapper { get; private set; }

    public bool IsRewinding { get; private set; }

    public double RewindSeconds { get; private set; }

    /// <summary>The profile applied to the running game.</summary>
    public GameProfile? Profile { get; private set; }

    /// <summary>Rewind memory cap in bytes (SPEC §7.2; 256 MB by default).</summary>
    public long RewindCapacity
    {
        set => Post(() => _rewind.CapacityBytes = Math.Max(1024 * 1024, value));
    }

    /// <summary>Hash of the running game's ROM, which names its save files. Null when idle.</summary>
    public ulong? RomHash { get; private set; }

    /// <summary>Run-ahead frames (0–3, SPEC §5.5). Applied between frames.</summary>
    public int RunAheadFrames
    {
        set => Post(() =>
        {
            _runAhead.Frames = Math.Clamp(value, 0, 3);
            _runAhead.ResetAutoDisable();
        });
    }

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
            RunAheadFrames = settings.RunAheadFrames;
            RewindCapacity = Math.Clamp(settings.RewindMemoryMb, 16, 4096) * 1024L * 1024;
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
        var hashes = RomHasher.Hash(data);
        var profile = ProfileResolver?.Invoke(hashes.Sha1) ?? new GameProfile { Sha1 = hashes.Sha1 };
        NesConsole console;
        try
        {
            console = new NesConsole(ApplyProfile(INesRom.Parse(data), profile));
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

        var battery = console.Mapper as IBatteryBacked;
        if (battery is { HasBattery: false })
        {
            battery = null;
        }

        if (battery is not null && _batteries is not null)
        {
            try
            {
                _batteries.TryLoad(console.RomHash, battery.BatteryRam);
            }
            catch (IOException)
            {
                // A damaged or locked save shouldn't stop the game; it starts with empty RAM instead.
                RaiseNotice("GameVault couldn't read this game's save file, so the game starts without it.");
            }
        }

        console.ConnectInput(profile.Input switch
        {
            InputType.Zapper => InputSetup.Zapper,
            InputType.FourScore => InputSetup.FourScore,
            _ => InputSetup.Controllers,
        });
        console.Ppu.RemoveSpriteLimit = profile.RemoveSpriteLimit == true;
        console.Power();
        EnsureThread();
        Post(() =>
        {
            FlushBattery(); // the previous game's
            _console = console;
            _battery = battery;
            _batterySaved = battery?.BatteryRam.ToArray() ?? [];
            _batteryTitle = title;
            _running = true;
            _runAhead.ResetAutoDisable();
            _rewind.Clear();
            _rewinding = false;
            _playedFrames = 0;
            Interlocked.Exchange(ref _frameCount, 0);
        });
        if (profile.RunAheadSafe == false)
        {
            // The game database says run-ahead breaks this game; leave it off whatever the setting says.
            RunAheadFrames = 0;
        }

        RomHash = console.RomHash;
        GameKey = hashes.Key;
        Profile = profile;
        UsesZapper = console.InputSetup == InputSetup.Zapper;
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

        Post(() =>
        {
            _running = false;
            FlushBattery();
            ReportPlaytime();
        });
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
            FlushBattery();
            ReportPlaytime();
            _console = null;
            _battery = null;
            _running = false;
            _rewind.Clear();
        });
        SetAudioPaused(true);
        GameTitle = null;
        RomHash = null;
        GameKey = null;
        UsesZapper = false;
        Profile = null;
        SetStatus(EmulatorStatus.Idle);
    }

    /// <summary>Runs a read-only query against the console on the emulation thread, between frames.</summary>
    internal Task<T?> InspectAsync<T>(Func<NesConsole, T> query)
    {
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => result.SetResult(_console is null ? default : query(_console)));
        return result.Task;
    }

    /// <summary>
    /// Takes a save state between frames, with a thumbnail of the current screen. Null when no game is running.
    /// </summary>
    public async Task<StateCapture?> CaptureAsync()
    {
        var captured = await InspectAsync(console => (console.RomHash, console.SaveSnapshot(), console.Ppu.FrameBuffer.ToArray()))
            .ConfigureAwait(false);
        if (captured.Item2 is null)
        {
            return null;
        }

        // PNG encoding happens off the emulation thread.
        var thumbnail = PngEncoder.Thumbnail(captured.Item3, NtscPalette.Argb);
        return new StateCapture(captured.RomHash, captured.Item2, thumbnail);
    }

    /// <summary>Loads a save state between frames. Returns false if it doesn't fit the running game.</summary>
    public Task<bool> RestoreAsync(byte[] state)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_console is null)
            {
                result.SetResult(false);
                return;
            }

            var backup = _console.SaveSnapshot();
            try
            {
                _console.LoadSnapshot(state);
                _present(_console);
                result.SetResult(true);
            }
            catch (StateFormatException)
            {
                _console.LoadSnapshot(backup); // a bad file must not leave the game half-loaded
                result.SetResult(false);
            }
        });
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
        if (_thread is not null)
        {
            var flushed = new ManualResetEventSlim();
            Post(() =>
            {
                FlushBattery();
                flushed.Set();
            });
            flushed.Wait(TimeSpan.FromSeconds(2));
        }

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
            PollStateHotkeys();
            var rewinding = UpdateRewinding();
            var fastForward = !rewinding && _input.IsHotkeyHeld(VirtualKeys.Tab);
            if (wasFastForward && !fastForward)
            {
                _audio?.Flush(); // resume from silence rather than a burst of stale sound
            }

            wasFastForward = fastForward;
            var audioClocked = _audioActive && !fastForward && !rewinding && console.Audio is not null;

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

            if (!(rewinding ? TryRewindFrame(console) : TryRunFrame(console)))
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
                console.SetControllerState(port, _input.Sample(port, console.Ppu.FrameCount));
            }

            if (console.InputSetup == InputSetup.Zapper)
            {
                var (aim, trigger) = _input.SampleZapper();
                console.Zapper.Aim = aim;
                console.Zapper.Trigger = trigger;
            }

            _rewind.OnFrame(console);

            if (_runAhead.RunFrame(console, _present))
            {
                RaiseNotice("Reduce input lag is off for now because this PC couldn't keep up. You can change it in Settings.");
            }

            if (Interlocked.Increment(ref _frameCount) % BatteryCheckFrames == 0)
            {
                FlushBattery(background: true);
            }

            if (++_playedFrames >= PlaytimeReportFrames)
            {
                ReportPlaytime();
            }

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
    /// Backspace or the left shoulder (input.md §8) held: walk back through the rewind history. Returns true
    /// while rewinding; raises <see cref="RewindChanged"/> on start and stop.
    /// </summary>
    private bool UpdateRewinding()
    {
        var held = _input.IsHotkeyHeld(VirtualKeys.Backspace);
        if (held != _rewinding)
        {
            _rewinding = held;
            if (!held)
            {
                _audio?.Flush(); // resume from silence
            }

            PublishRewind();
        }

        return held;
    }

    /// <summary>One step of rewind: restore an older state and show the frame that follows it, silently.</summary>
    private bool TryRewindFrame(NesConsole console)
    {
        try
        {
            if (_rewind.StepBack(console))
            {
                console.RunFrameSilently();
                _present(console);
            }

            if (Interlocked.Increment(ref _frameCount) % 6 == 0 || _rewind.Count == 0)
            {
                PublishRewind();
            }

            return true;
        }
        catch (Exception ex) when (ex is StateFormatException or InvalidDataException)
        {
            _rewind.Clear();
            RaiseNotice("Rewind stopped working for this game, so its history was cleared.");
            return true;
        }
    }

    private void PublishRewind()
    {
        var rewinding = _rewinding;
        var seconds = _rewind.Duration.TotalSeconds;
        Raise(() =>
        {
            IsRewinding = rewinding;
            RewindSeconds = seconds;
            RewindChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ReportPlaytime()
    {
        if (_playedFrames == 0)
        {
            return;
        }

        var played = TimeSpan.FromSeconds(_playedFrames / NtscFrameRate);
        _playedFrames = 0;
        Raise(() => PlaytimeElapsed?.Invoke(this, played));
    }

    /// <summary>Header corrections and region from the game database (SPEC §4.5, §7.5).</summary>
    private static INesRom ApplyProfile(INesRom rom, GameProfile profile)
    {
        if (profile.Header is { } header)
        {
            rom = rom with
            {
                Mapper = header.Mapper ?? rom.Mapper,
                Submapper = header.Submapper ?? rom.Submapper,
                HasBattery = header.Battery ?? rom.HasBattery,
                PrgRamSize = header.PrgRamSize ?? rom.PrgRamSize,
                Mirroring = header.Mirroring?.ToLowerInvariant() switch
                {
                    "horizontal" => Mirroring.Horizontal,
                    "vertical" => Mirroring.Vertical,
                    "four-screen" => Mirroring.FourScreen,
                    _ => rom.Mirroring,
                },
            };
        }

        return profile.Region?.ToLowerInvariant() switch
        {
            "ntsc" => rom with { TvSystem = TvSystem.Ntsc },
            "pal" => rom with { TvSystem = TvSystem.Pal },
            "dendy" => rom with { TvSystem = TvSystem.Dendy },
            _ => rom,
        };
    }

    /// <summary>F5 / F8 act on the press, not while held.</summary>
    private void PollStateHotkeys()
    {
        var save = _input.IsHotkeyHeld(VirtualKeys.F5);
        var load = _input.IsHotkeyHeld(VirtualKeys.F8);
        if (save && !_quickSaveHeld)
        {
            Raise(() => QuickSaveRequested?.Invoke(this, EventArgs.Empty));
        }

        if (load && !_quickLoadHeld)
        {
            Raise(() => QuickLoadRequested?.Invoke(this, EventArgs.Empty));
        }

        _quickSaveHeld = save;
        _quickLoadHeld = load;
    }

    /// <summary>Writes battery RAM to its .sav file if it changed. Runs on the emulation thread.</summary>
    private void FlushBattery(bool background = false)
    {
        if (_battery is null || _batteries is null || _battery.BatteryRam.SequenceEqual(_batterySaved))
        {
            return;
        }

        var data = _battery.BatteryRam.ToArray();
        _batterySaved = data;
        var title = _batteryTitle;
        var hash = _console!.RomHash;

        void Write()
        {
            try
            {
                _batteries.Save(title, hash, data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseNotice("GameVault couldn't save this game's progress file. " + ex.Message);
            }
        }

        if (background)
        {
            _ = Task.Run(Write); // keep disk writes off the frame loop
        }
        else
        {
            Write();
        }
    }

    private void RaiseNotice(string message) => Raise(() => Notice?.Invoke(this, message));

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

/// <summary>A save state taken by <see cref="EmulatorHost.CaptureAsync"/>, with a PNG thumbnail.</summary>
public sealed record StateCapture(ulong RomHash, byte[] State, byte[] ThumbnailPng);
