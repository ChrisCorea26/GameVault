using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GameVault.Core;
using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Fds;
using GameVault.Core.Cheats;
using GameVault.Core.Ppu;
using GameVault.Platform.Audio;
using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;
using GameVault.Platform.Netplay;
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
    private readonly BatterySaveStore? _diskSaves;

    // Famicom Disk System: the tracks as built from the image (the patch base) and as last saved.
    private byte[][]? _diskOriginal;
    private byte[][]? _diskSaved;
    private bool _diskKeyHeld;
    private int _diskSideRequested;
    private int _reportedDiskSide = -1;
    private volatile int _insertedDiskSide = -1;

    // Netplay (SPEC §7.7, LAN-only). Set and cleared on the emulation thread.
    private const int NetplayInputDelay = 1;
    private static readonly TimeSpan NetplayTimeout = TimeSpan.FromSeconds(5);
    private RollbackSession? _netplay;
    private UdpNetplayLink? _netLink;
    private volatile bool _netplayActive;
    private NetplayStatus _netplayStatus;
    private byte[]? _romData;

    // Codes (SPEC §7.8): the engine last reported, to notice when a load or rewind brought back other codes.
    private CheatEngine? _seenCheats;
    private volatile bool _cheatsActive;
    private bool _codesOffKeyHeld;
    private readonly RunAhead _runAhead = new();
    private readonly RewindBuffer _rewind = new();
    private readonly PasswordWatcher _passwords = new();
    private uint[] _palette = NtscPalette.Argb.ToArray();
    private double _speed = 1.0;
    private long _rewindCapacity = RewindBuffer.DefaultCapacity;
    private bool _profileRemovesSpriteLimit;
    private AssistOptions _assist = AssistOptions.Off;
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
    private bool _passwordKeyHeld;
    private bool _screenshotKeyHeld;
    private bool _retryKeyHeld;
    private bool _splitKeyHeld;
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
        _diskSaves = paths is null ? null : new BatterySaveStore(paths, ".fdssave");
        _context = SynchronizationContext.Current;
        _present = console => _video.Present(console.Ppu.FrameBuffer, _palette);
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

    /// <summary>A password screen was recognized (SPEC §7.3). Raised on the UI thread with a screenshot.</summary>
    public event EventHandler<PasswordCapturedEventArgs>? PasswordCaptured;

    /// <summary>F9: the player wants to note a password by hand. Raised on the UI thread.</summary>
    public event EventHandler? ManualPasswordRequested;

    /// <summary>F12: save a screenshot to the journal (SPEC §7.4). Raised on the UI thread.</summary>
    public event EventHandler? ScreenshotRequested;

    /// <summary>
    /// The active codes changed without <see cref="SetCheats"/>: a save state or rewind brought back the codes
    /// that were on then, or F3 turned them all off. Raised on the UI thread with the codes now on.
    /// </summary>
    public event EventHandler<IReadOnlyList<CheatCode>>? CheatsChanged;

    public bool CheatsActive => _cheatsActive;

    public void SetCheats(IReadOnlyList<CheatCode> codes)
    {
        if (_netplayActive)
        {
            return; // both players must run the same game: no codes during netplay
        }

        var copy = codes.ToList();
        _cheatsActive = copy.Count > 0;
        Post(() =>
        {
            if (_console is { } console)
            {
                console.SetCheats(copy);
                _seenCheats = console.Bus.Cheats;
            }
        });
    }

    public NetplayStatus NetplayStatus => _netplayStatus;

    public string? NetplayInviteCode { get; private set; }

    public event EventHandler? NetplayChanged;

    public Task<string> HostNetplayAsync() => HostNetplayAsync(advertise: null);

    /// <param name="advertise">The address to put in the invite code (tests use loopback); this PC's LAN address by default.</param>
    internal Task<string> HostNetplayAsync(IPAddress? advertise)
    {
        EnsureNetplayPossible();
        var address = advertise ?? InviteCode.LocalNetworkAddress()
            ?? throw new NetplayException("This PC doesn't seem to be on a network. Connect to the same network as the other player and try again.");
        var link = UdpNetplayLink.Listen();
        var code = InviteCode.Encode(new IPEndPoint(address, link.LocalPort));
        StartNetplay(link, localPlayer: 0, code);
        return Task.FromResult(code);
    }

    public Task JoinNetplayAsync(string inviteCode)
    {
        EnsureNetplayPossible();
        if (!InviteCode.TryDecode(inviteCode, out var host))
        {
            throw new NetplayException("That invite code isn't right. It has 10 letters and digits, like ABCDE-12345.");
        }

        StartNetplay(UdpNetplayLink.Connect(host), localPlayer: 1, code: null);
        return Task.CompletedTask;
    }

    public void LeaveNetplay()
    {
        if (!_netplayActive)
        {
            return;
        }

        Post(EndNetplay);
        RaiseNotice("Netplay ended. You can keep playing, but this session's progress isn't saved to the game's save file.");
    }

    private void EnsureNetplayPossible()
    {
        if (Status is not (EmulatorStatus.Running or EmulatorStatus.Paused) || _romData is null)
        {
            throw new NetplayException("Start the game first. Both players need the same game running.");
        }

        if (FdsImage.IsFdsImage(_romData))
        {
            throw new NetplayException("Netplay works with cartridge games for now, not Disk System games.");
        }
    }

    /// <summary>
    /// Both players start from power-on with no save file and no codes, so the two games are identical; the
    /// player's own save file is left alone for the rest of the session.
    /// </summary>
    private void StartNetplay(UdpNetplayLink link, int localPlayer, string? code)
    {
        var data = _romData!;
        var profile = Profile ?? new GameProfile { Sha1 = string.Empty };
        var console = new NesConsole(ApplyProfile(INesRom.Parse(data), profile));
        if (_audioActive && _audio is not null)
        {
            console.EnableAudio(_audio.SampleRate);
        }

        console.ConnectInput(InputSetup.Controllers);
        console.Power();
        _netplayActive = true;
        Post(() =>
        {
            EndNetplay();
            NetplayInviteCode = code;
            FlushBattery();
            _console = console;
            _battery = null;
            _diskOriginal = null;
            _seenCheats = null;
            _cheatsActive = false;
            _rewind.Clear();
            _rewinding = false;
            _netLink = link;
            _netplay = new RollbackSession(console, link, localPlayer, NetplayInputDelay);
            _netplayActive = true;
            _running = true;
            SetNetplayStatus(localPlayer == 0 ? NetplayStatus.WaitingForPlayer : NetplayStatus.Connecting);
        });
        SetAudioPaused(false);
        SetStatus(EmulatorStatus.Running);
    }

    /// <summary>Emulation thread: closes the session, if any.</summary>
    private void EndNetplay()
    {
        _netLink?.Dispose();
        _netLink = null;
        _netplay = null;
        _netplayActive = false;
        NetplayInviteCode = null;
        SetNetplayStatus(NetplayStatus.Off);
    }

    private void SetNetplayStatus(NetplayStatus status)
    {
        if (_netplayStatus == status)
        {
            return;
        }

        _netplayStatus = status;
        Raise(() => NetplayChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>One netplay frame: exchange inputs, roll back if needed, run, show it. Emulation thread.</summary>
    private void RunNetplayFrame(NesConsole console, RollbackSession session, UdpNetplayLink link)
    {
        var ran = session.AdvanceFrame(_input.Sample(0, session.Frame));
        var status = session.State switch
        {
            NetplayState.Desynced => NetplayStatus.OutOfSync,
            NetplayState.Mismatch => NetplayStatus.DifferentGame,
            _ when link.LastReceived is { } last && DateTime.UtcNow - last > NetplayTimeout => NetplayStatus.ConnectionLost,
            NetplayState.Connecting => _netplayStatus,
            NetplayState.Waiting => NetplayStatus.Waiting,
            _ => NetplayStatus.Playing,
        };
        SetNetplayStatus(status);
        if (!ran)
        {
            return;
        }

        _present(console);
        Interlocked.Increment(ref _frameCount);
        if (console.Audio is { } resampler)
        {
            int count;
            while ((count = resampler.Read(_samples)) > 0)
            {
                if (_audioActive)
                {
                    _audio!.Write(_samples.AsSpan(0, count));
                }
            }
        }
    }

    /// <summary>A disk side was inserted or ejected (Famicom Disk System). Raised on the UI thread.</summary>
    public event EventHandler? DiskChanged;

    public int DiskSideCount { get; private set; }

    public int? InsertedDiskSide => _insertedDiskSide < 0 ? null : _insertedDiskSide;

    public void InsertDiskSide(int side) => Post(() =>
    {
        if (_console?.Fds is { } fds && side >= 0 && side < fds.SideCount)
        {
            _diskSideRequested = side;
            fds.InsertSide(side);
            UpdateDiskSide(fds);
        }
    });

    public void EjectDisk() => Post(() =>
    {
        if (_console?.Fds is { } fds)
        {
            fds.Eject();
            UpdateDiskSide(fds);
        }
    });

    /// <summary>"Disk 1, side A", "Disk 1, side B", "Disk 2, side A", … (sides are stored in that order).</summary>
    public static string DiskSideName(int side) => $"Disk {side / 2 + 1}, side {(char)('A' + side % 2)}";

    /// <summary>F6 / F7: practice mode retry and split (SPEC §7.6). Raised on the UI thread.</summary>
    public event EventHandler? PracticeRetryRequested;

    public event EventHandler? PracticeSplitRequested;

    /// <summary>Finds the password definition for a game by headerless SHA-1. Null: no password capture.</summary>
    public Func<string, Library.Passwords.PasswordDefinition?>? PasswordDefinitionResolver { get; set; }

    public event EventHandler<TimeSpan>? PlaytimeElapsed;

    /// <summary>Looks up a game's profile by headerless SHA-1 (SPEC §7.5). Null: no profiles.</summary>
    public Func<string, GameProfile>? ProfileResolver { get; set; }

    public string? GameKey { get; private set; }

    public bool UsesZapper { get; private set; }

    public bool UsesFourScore { get; private set; }

    public bool IsRewinding { get; private set; }

    public double RewindSeconds { get; private set; }

    /// <summary>The profile applied to the running game.</summary>
    public GameProfile? Profile { get; private set; }

    /// <summary>Rewind memory cap in bytes (SPEC §7.2; 256 MB by default).</summary>
    public long RewindCapacity
    {
        set => Post(() =>
        {
            _rewindCapacity = Math.Max(1024 * 1024, value);
            ApplyAssist();
        });
    }

    public void SetAssist(AssistOptions options) => Post(() =>
    {
        _assist = options;
        ApplyAssist();
    });

    /// <summary>Applies assist mode on the emulation thread (SPEC §7.6).</summary>
    private void ApplyAssist()
    {
        _speed = Math.Clamp(_assist.SpeedPercent, 10, 100) / 100.0;
        _palette = Video.ColorVisionPalettes.Create(NtscPalette.Argb, _assist.Palette);
        _rewind.CapacityBytes = _assist.UnlimitedRewind ? 4L * 1024 * 1024 * 1024 : _rewindCapacity;
        if (_console is { } console)
        {
            console.Ppu.RemoveSpriteLimit = _profileRemovesSpriteLimit || _assist.RemoveFlicker;
        }
    }

    public Task<byte[]?> CaptureStateAsync() => InspectAsync(console => console.SaveSnapshot());

    public Task<bool> RestoreStateAsync(byte[] state) => RestoreAsync(state);

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
            // Library games can be zipped (one .nes or .fds inside), so read them the way the scanner does.
            data = await Task.Run(() => LibraryScanner.ReadRom(path), cancellationToken).ConfigureAwait(false)
                ?? throw new GameLoadException("That file isn't an NES game GameVault can open. It plays .nes and .fds files, and .zip files with one of them inside.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
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

        byte[]? bios = null;
        if (FdsImage.IsFdsImage(data) && _settings is not null)
        {
            var biosPath = (await _settings.LoadAsync(cancellationToken).ConfigureAwait(false)).FdsBiosPath;
            if (!string.IsNullOrWhiteSpace(biosPath))
            {
                try
                {
                    bios = await File.ReadAllBytesAsync(biosPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new GameLoadException(
                        "GameVault couldn't open your Disk System BIOS. Check the file chosen in Settings → Disk System.", ex);
                }
            }
        }

        Load(data, Path.GetFileNameWithoutExtension(path), bios);
    }

    /// <summary>
    /// Loads a game from bytes already in memory. Famicom Disk System images also need the player's own
    /// 8 KB BIOS in <paramref name="fdsBios"/>.
    /// </summary>
    public void Load(byte[] data, string title, byte[]? fdsBios = null)
    {
        var hashes = RomHasher.Hash(data);
        var profile = ProfileResolver?.Invoke(hashes.Sha1) ?? new GameProfile { Sha1 = hashes.Sha1 };
        NesConsole console;
        try
        {
            if (FdsImage.IsFdsImage(data))
            {
                if (fdsBios is null)
                {
                    throw new GameLoadException(
                        "This is a Famicom Disk System game. To play it, choose your own Disk System BIOS " +
                        "(disksys.rom) in Settings → Disk System. GameVault doesn't include it.");
                }

                console = NesConsole.FromFds(data, fdsBios);
            }
            else
            {
                console = new NesConsole(ApplyProfile(INesRom.Parse(data), profile));
            }
        }
        catch (RomFormatException ex) when (FdsImage.IsFdsImage(data))
        {
            throw new GameLoadException(ex.Message, ex); // a damaged disk image or a BIOS of the wrong size
        }
        catch (RomFormatException ex)
        {
            throw new GameLoadException("That file isn't an NES game, or it's damaged. GameVault plays .nes and .fds files.", ex);
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

        _romData = data;
        byte[][]? diskOriginal = null;
        byte[][]? diskSaved = null;
        if (console.Fds is { } disk)
        {
            diskOriginal = disk.Tracks.Select(t => t.ToArray()).ToArray();
            try
            {
                if (_diskSaves?.TryRead(console.RomHash) is { } patch)
                {
                    disk.ApplyDiskPatch(patch);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                RaiseNotice("GameVault couldn't read this disk's saved progress, so the game starts without it.");
            }

            diskSaved = disk.Tracks.Select(t => t.ToArray()).ToArray();
        }

        console.ConnectInput(profile.Input switch
        {
            InputType.Zapper => InputSetup.Zapper,
            InputType.FourScore => InputSetup.FourScore,
            _ => InputSetup.Controllers,
        });
        console.Ppu.RemoveSpriteLimit = profile.RemoveSpriteLimit == true || _assist.RemoveFlicker;
        var passwordDefinition = PasswordDefinitionResolver?.Invoke(hashes.Sha1);
        console.Power();
        EnsureThread();
        Post(() =>
        {
            EndNetplay();
            FlushBattery(); // the previous game's
            _console = console;
            _seenCheats = null;
            _cheatsActive = false;
            _diskOriginal = diskOriginal;
            _diskSaved = diskSaved;
            _diskSideRequested = 0;
            _reportedDiskSide = -1;
            if (console.Fds is { } fds)
            {
                UpdateDiskSide(fds);
            }
            _battery = battery;
            _batterySaved = battery?.BatteryRam.ToArray() ?? [];
            _batteryTitle = title;
            _running = true;
            _runAhead.ResetAutoDisable();
            _rewind.Clear();
            _rewinding = false;
            _playedFrames = 0;
            _passwords.Definition = passwordDefinition;
            _profileRemovesSpriteLimit = profile.RemoveSpriteLimit == true;
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
        UsesFourScore = console.InputSetup == InputSetup.FourScore;
        DiskSideCount = console.Fds?.SideCount ?? 0;
        _insertedDiskSide = console.Fds is null ? -1 : 0;
        SetAudioPaused(false);

        GameTitle = title;
        FaultMessage = null;
        SetStatus(EmulatorStatus.Running);
    }

    public void Pause()
    {
        // The other player's game can't pause with ours, so netplay keeps running (and keeps exchanging inputs).
        if (Status != EmulatorStatus.Running || _netplayActive)
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
            EndNetplay();
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
        UsesFourScore = false;
        DiskSideCount = 0;
        _insertedDiskSide = -1;
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

    /// <summary>The current screen as a full-size PNG (screenshots, manual password capture).</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        var frame = await InspectAsync(console => console.Ppu.FrameBuffer.ToArray()).ConfigureAwait(false);
        return frame is null ? null : PngEncoder.Encode(PngEncoder.ToArgb(frame, NtscPalette.Argb), 256, 240);
    }

    /// <summary>Loads a save state between frames. Returns false if it doesn't fit the running game.</summary>
    public Task<bool> RestoreAsync(byte[] state)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_console is null || _netplay is not null)
            {
                result.SetResult(false); // loading a state would put the two netplay games out of step
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
            if (_netplay is { } session && _netLink is { } link)
            {
                // Netplay runs at the NTSC frame rate by the clock; rewind, fast-forward, slow motion and the
                // state hotkeys are off because both games must stay identical.
                RunNetplayFrame(console, session, link);
                nextFrame += frameTicks;
                if (!WaitUntil(clock, nextFrame))
                {
                    nextFrame = clock.ElapsedTicks;
                }

                continue;
            }

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
                // Slow motion: telling the resampler the CPU runs slower stretches each frame's sound, so the
                // audio clock paces fewer frames per second and the pitch drops with the speed.
                console.Audio!.ClockRate = console.CpuClockRate * _speed * _pacing!.ClockRateFactor(consumed, _rateClock.Elapsed.TotalSeconds);
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

            nextFrame += fastForward ? frameTicks / MaxFastForward : frameTicks / _speed;
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

            if (console.Bus.Cheats != _seenCheats)
            {
                _seenCheats = console.Bus.Cheats;
                var codes = console.Cheats;
                _cheatsActive = codes.Count > 0;
                Raise(() => CheatsChanged?.Invoke(this, codes));
            }

            _rewind.OnFrame(console);
            if (console.Fds is { } drive)
            {
                UpdateDiskSide(drive);
            }

            var runAheadOff = _runAhead.RunFrame(console, _present);
            if (_passwords.OnFrame(console) is { } password)
            {
                var png = PngEncoder.Encode(PngEncoder.ToArgb(console.Ppu.FrameBuffer, _palette), 256, 240);
                Raise(() => PasswordCaptured?.Invoke(this, new PasswordCapturedEventArgs(password, png)));
            }

            if (runAheadOff)
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

        // F4: the next disk side, as when turning the disk over or swapping disks (input.md §8).
        var diskKey = _input.IsHotkeyHeld(VirtualKeys.F4);
        if (diskKey && !_diskKeyHeld && _console?.Fds is { } fds)
        {
            _diskSideRequested = (_diskSideRequested + 1) % fds.SideCount;
            fds.InsertSide(_diskSideRequested);
            RaiseNotice($"Inserting {DiskSideName(_diskSideRequested)}.");
            UpdateDiskSide(fds);
        }

        _diskKeyHeld = diskKey;

        // F3: every code off at once (SPEC §7.8; input.md §8).
        var codesOff = _input.IsHotkeyHeld(VirtualKeys.F3);
        if (codesOff && !_codesOffKeyHeld && _console is { Cheats.Count: > 0 } withCodes)
        {
            withCodes.SetCheats([]);
            RaiseNotice("All codes are off.");
        }

        _codesOffKeyHeld = codesOff;

        Edge(VirtualKeys.F9, ref _passwordKeyHeld, () => ManualPasswordRequested?.Invoke(this, EventArgs.Empty));
        Edge(VirtualKeys.F12, ref _screenshotKeyHeld, () => ScreenshotRequested?.Invoke(this, EventArgs.Empty));
        Edge(VirtualKeys.F6, ref _retryKeyHeld, () => PracticeRetryRequested?.Invoke(this, EventArgs.Empty));
        Edge(VirtualKeys.F7, ref _splitKeyHeld, () => PracticeSplitRequested?.Invoke(this, EventArgs.Empty));
    }

    private void Edge(int key, ref bool wasHeld, Action onPress)
    {
        var held = _input.IsHotkeyHeld(key);
        if (held && !wasHeld)
        {
            Raise(onPress);
        }

        wasHeld = held;
    }

    /// <summary>Raises <see cref="DiskChanged"/> when the drive's contents changed. Emulation thread.</summary>
    private void UpdateDiskSide(FdsAdapter fds)
    {
        var side = fds.InsertedSide ?? -1;
        if (side == _reportedDiskSide)
        {
            return;
        }

        _reportedDiskSide = side;
        _insertedDiskSide = side;
        Raise(() => DiskChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Saves battery RAM and disk writes that changed. Runs on the emulation thread.</summary>
    private void FlushBattery(bool background = false)
    {
        FlushBatteryRam(background);
        FlushDisk(background);
    }

    /// <summary>
    /// Disk writes go to "Title - hash.fdssave" as a patch against the image, never into the image itself
    /// (Phase 7 plan).
    /// </summary>
    private void FlushDisk(bool background)
    {
        if (_console?.Fds is not { IsDiskModified: true } fds || _diskSaves is null || _diskOriginal is null || _diskSaved is null)
        {
            return;
        }

        var changed = false;
        for (var i = 0; i < fds.SideCount && !changed; i++)
        {
            changed = !fds.Tracks[i].AsSpan().SequenceEqual(_diskSaved[i]);
        }

        if (!changed)
        {
            return;
        }

        _diskSaved = fds.Tracks.Select(t => t.ToArray()).ToArray();
        var patch = FdsDiskPatch.Create(_diskOriginal, _diskSaved);
        var title = _batteryTitle;
        var hash = _console.RomHash;

        void Write()
        {
            try
            {
                _diskSaves.Save(title, hash, patch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseNotice("GameVault couldn't save this disk's progress. " + ex.Message);
            }
        }

        if (background)
        {
            _ = Task.Run(Write);
        }
        else
        {
            Write();
        }
    }

    private void FlushBatteryRam(bool background)
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

/// <summary>A password read from the screen, with a screenshot of it.</summary>
public sealed class PasswordCapturedEventArgs(string password, byte[] screenshotPng) : EventArgs
{
    public string Password { get; } = password;

    public byte[] ScreenshotPng { get; } = screenshotPng;
}
