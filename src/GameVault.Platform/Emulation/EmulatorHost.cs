using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using GameVault.Core;
using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Fds;
using GameVault.Core.Cheats;
using GameVault.Core.Ppu;
using GameVault.Platform.Achievements;
using GameVault.Platform.Audio;
using GameVault.Core.Input;
using GameVault.Core.Apple;
using GameVault.Core.Genesis;
using GameVault.Core.Nintendo64;
using GameVault.Core.PlayStation;
using GameVault.Core.Snes;
using GameVault.Core.Sega;
using GameVault.Core.State;
using GameVault.Core.Systems;
using GameVault.Library.Challenges;
using GameVault.Library.Hashing;
using GameVault.Library.Maps;
using GameVault.Library.Memory;
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
public sealed partial class EmulatorHost : IEmulatorService, IDisposable
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

    // PlayStation memory cards are saved as raw .mcd images, the format other emulators read.
    private readonly BatterySaveStore? _memoryCards;
    private readonly BatterySaveStore? _controllerPaks;
    private BatterySaveStore? _batteryStore;

    // GV-012: the game file whose folder the next game's save goes in (set by LoadAsync), and the running game's save there.
    private string? _nextSaveBesideRom;
    private string? _batteryBesideRom;
    private readonly BatterySaveStore? _diskSaves;
    private readonly BatterySaveStore? _appleDiskSaves;

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

    // Verified challenges (SPEC §7.9): the attempt in progress, set and cleared on the emulation thread.
    private ChallengeFile? _challenge;
    private List<(ulong Player1, ulong Player2)> _challengeInputs = [];
    private volatile bool _challengeActive;

    // Game-aware features (SPEC §7.9): the memory-map watcher and its latest overlay values.
    private readonly GameWatcher _watcher = new();
    private volatile IReadOnlyList<StatValue> _statValues = [];
    private bool _livesKeyHeld;

    // Codes (SPEC §7.8): the engine last reported, to notice when a load or rewind brought back other codes.
    private CheatEngine? _seenCheats;
    private volatile bool _cheatsActive;
    private bool _codesOffKeyHeld;
    private readonly RunAhead _runAhead = new();
    private readonly RewindBuffer _rewind = new();
    private readonly PasswordWatcher _passwords = new();
    private uint[] _palette = NtscPalette.Argb.ToArray();
    private double _speed = 1.0;

    /// <summary>The game's speed: slow motion from Assist, except in hardcore mode.</summary>
    private double Speed => _hardcore ? 1.0 : _speed;
    private long _rewindCapacity = RewindBuffer.DefaultCapacity;
    private bool _profileRemovesSpriteLimit;
    private AssistOptions _assist = AssistOptions.Off;
    private readonly Action<IEmulatedConsole> _present;
    private readonly float[] _samples = new float[8192 * 2]; // interleaved left and right
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
    private IEmulatedConsole? _console;
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
        _appleDiskSaves = paths is null ? null : new BatterySaveStore(paths, ".a2save");
        _memoryCards = paths is null ? null : new BatterySaveStore(paths, ".mcd");
        _controllerPaks = paths is null ? null : new BatterySaveStore(paths, ".mpk");
        _ps2Cards = paths is null ? null : new Ps2MemoryCardStore(paths);
        _context = SynchronizationContext.Current;
        _present = console => _video.Present(console.FrameBuffer, PaletteFor(console), console.ScreenWidth, console.ScreenHeight, console.PixelAspect);
    }

    public EmulatorStatus Status { get; private set; }

    private GameSystem _romSystem;
    private string _romExtension = "";
    private AppleRoms? _appleRoms;
    private SnesSetup? _snes;

    /// <summary>The system of the running game; null when idle.</summary>
    public GameSystem? System { get; private set; }

    /// <summary>The running console when it's an NES (Game Genie, the Disk System, the Zapper and passwords are NES-only).</summary>
    private NesConsole? Nes => _console as NesConsole;

    /// <summary>The buttons sent to each port on the last frame (challenge recordings).</summary>
    private readonly ControllerState[] _lastInputs = new ControllerState[InputService.PortCount];

    /// <summary>The NES uses the player's palette preset; other systems bring their own colours.</summary>
    private ReadOnlySpan<uint> PaletteFor(IEmulatedConsole console) => console is NesConsole ? _palette : console.Palette;

    /// <summary>Screenshots and thumbnails use the standard colours, whatever the colour-vision setting.</summary>
    private static uint[] ScreenshotPalette(IEmulatedConsole console) => (console is NesConsole ? NtscPalette.Argb : console.Palette).ToArray();

    // ---------------------------------------------------------------- RetroAchievements (SPEC §7.10)

    private AchievementsClient? _achievements;
    private volatile bool _hardcore;

    /// <summary>
    /// rcheevos, when RetroAchievements is set up: it reads memory and processes each real frame on the
    /// emulation thread, and idles while paused. Set once, before the first game.
    /// </summary>
    public AchievementsClient? Achievements
    {
        get => _achievements;
        set
        {
            _achievements = value;
            if (value is not null)
            {
                value.ReadMemory = ReadForAchievements;
            }
        }
    }

    /// <summary>The RetroAchievements hash of the running game (MD5 without the 16-byte header); null when idle.</summary>
    public string? AchievementsHash { get; private set; }

    /// <summary>
    /// Hardcore mode (SPEC §7.10): no loading states, rewind, codes, slow motion, netplay or challenge attempts.
    /// Saving states and run-ahead stay allowed. Turning it on clears the running game's codes.
    /// </summary>
    public bool HardcoreActive
    {
        get => _hardcore;
        set
        {
            _hardcore = value;
            if (value)
            {
                _cheatsActive = false;
                Post(() =>
                {
                    if (_console is NesConsole console && console.Cheats.Count > 0)
                    {
                        console.SetCheats([]);
                        _seenCheats = null; // reported through CheatsChanged on the next frame
                    }
                });
            }
        }
    }

    /// <summary>NES CPU addresses $0000–$FFFF, read without side effects (emulation thread only).</summary>
    internal uint ReadForAchievements(uint address, Span<byte> buffer)
    {
        if (_console is not { } console)
        {
            return 0;
        }

        var count = 0u;
        uint limit = console switch
        {
            AppleConsole or GenesisConsole => 0x1FFFF,
            SnesConsole => 0x9FFFF,
            PlayStationConsole => 0x2003FF,
            Core.PlayStation2.Ps2Console => AchievementsHashing.PlayStation2MemoryLimit, // RDRAM, then the scratchpad
            N64Console n64 => (uint)n64.Rdram.Length - 1,
            _ => 0xFFFF,
        };
        for (var i = 0; i < buffer.Length && address + i <= limit; i++)
        {
            buffer[i] = console switch
            {
                SegaConsole sega => sega.ReadAchievementMemory((int)(address + i)),
                AppleConsole apple => apple.ReadAchievementMemory((int)(address + i)),
                GenesisConsole genesis => genesis.ReadAchievementMemory((int)(address + i)),
                SnesConsole snes => snes.ReadAchievementMemory((int)(address + i)),
                PlayStationConsole ps => ps.ReadAchievementMemory((int)(address + i)),
                N64Console n64 => n64.ReadAchievementMemory((int)(address + i)),
                _ => console.PeekMemory((int)(address + i)),
            };
            count++;
        }

        return count;
    }

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
        if (_netplayActive || _challengeActive || _hardcore)
        {
            return; // netplay and challenges replay without codes; hardcore allows none
        }

        var copy = codes.ToList();
        _cheatsActive = copy.Count > 0;
        Post(() =>
        {
            if (_console is NesConsole console)
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
        if (_hardcore)
        {
            throw new NetplayException("Netplay is off in RetroAchievements hardcore mode. Turn hardcore off in Settings to play together.");
        }

        if (Status is not (EmulatorStatus.Running or EmulatorStatus.Paused) || _romData is null)
        {
            throw new NetplayException("Start the game first. Both players need the same game running.");
        }

        if (!SystemBudgets.AllowsNetplay(_romSystem))
        {
            throw new NetplayException(SystemBudgets.PlayStation2NetplayMessage);
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
        var console = ConsoleFactory.Create(data, _romSystem, profile, fdsBios: null, _romExtension, _appleRoms, _playStation, _n64, _playStation2, _snes);
        if (_audioActive && _audio is not null)
        {
            console.EnableAudio(_audio.SampleRate);
        }

        (console as NesConsole)?.ConnectInput(InputSetup.Controllers);
        console.Power();
        _netplayActive = true;
        Post(() =>
        {
            EndNetplay();
            NetplayInviteCode = code;
            FlushBattery();
            ReleaseDiscs(_console);
            _console = console;
            _battery = null;
            _pak = null;
            _ps2Card = null;
            _diskOriginal = null;
            _seenCheats = null;
            _cheatsActive = false;
            _rewind.Clear();
            _scrubIndex = -1;
            _rewinding = false;
            _challenge = null;
            _challengeActive = false;
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
    private void RunNetplayFrame(IEmulatedConsole console, RollbackSession session, UdpNetplayLink link)
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
            while ((count = resampler.ReadStereo(_samples)) > 0)
            {
                if (_audioActive)
                {
                    _audio!.Write(_samples.AsSpan(0, count * 2));
                }
            }
        }
    }

    /// <summary>Finds a game's memory map (SPEC §7.9) by headerless SHA-1.</summary>
    public Func<string, MemoryMap?>? MemoryMapResolver { get; set; }

    public bool HasMemoryMap => MemoryMap is not null;

    /// <summary>The running game's memory map, or null.</summary>
    public MemoryMap? MemoryMap { get; private set; }

    public IReadOnlyList<StatValue> StatValues => _statValues;

    public event EventHandler? StatValuesChanged;

    /// <summary>A life was lost: the state from a few seconds earlier, for the timeline. Raised on the UI thread.</summary>
    public event EventHandler<StateCapture>? LifeLost;

    /// <summary>The player entered a room or screen (automatic maps). Raised on the UI thread with a thumbnail.</summary>
    public event EventHandler<RoomEnteredEventArgs>? RoomEntered;

    /// <summary>F2: go back to before the last life lost. Raised on the UI thread.</summary>
    public event EventHandler? BeforeDeathRequested;

    public IReadOnlyList<string> MemoryValueNames => MemoryMap?.Values.Keys.Order().ToList() ?? [];

    public bool IsChallengeActive => _challengeActive;

    public event EventHandler<ChallengeOutcome>? ChallengeFinished;

    public async Task<ChallengeFile> CreateChallengeAsync(string name, IReadOnlyList<MemoryCondition> goal, int? timeLimitFrames, string? note)
    {
        if (GameKey is not { } key || !key.StartsWith("sha1:", StringComparison.Ordinal) || _romData is null || FdsImage.IsFdsImage(_romData))
        {
            throw new ChallengeException("Start a cartridge game first; challenges begin from where you are now.");
        }

        if (_netplayActive || _challengeActive)
        {
            throw new ChallengeException("Finish the netplay session or challenge first.");
        }

        if (goal.Count == 0)
        {
            throw new ChallengeException("Choose a goal: what must be true to finish the challenge.");
        }

        var map = MemoryMap;
        var names = goal.Where(c => c.Address is null).Select(c => c.Value.GetString()!).Distinct().ToList();
        if (names.Any(n => map?.Values.ContainsKey(n) != true))
        {
            throw new ChallengeException("The goal uses a value this game's memory map doesn't have.");
        }

        // No codes in the starting point: the check replays without them.
        var state = await InspectAsync(console =>
        {
            if (console is not NesConsole nes)
            {
                return console.SaveSnapshot();
            }

            if (nes.InputSetup != InputSetup.Controllers)
            {
                return null;
            }

            var codes = nes.Cheats.ToList();
            nes.SetCheats([]);
            var snapshot = nes.SaveSnapshot();
            nes.SetCheats(codes);
            return snapshot;
        }).ConfigureAwait(false) ?? throw new ChallengeException("Challenges work with games played on controllers.");

        return new ChallengeFile
        {
            Id = Guid.NewGuid().ToString("N"),
            RomSha1 = key[5..],
            GameTitle = GameTitle ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? "Challenge" : name.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Goal = goal,
            Values = names.ToDictionary(n => n, n => map!.Values[n]),
            TimeLimitFrames = timeLimitFrames,
            StartState = Convert.ToBase64String(state),
            CreatedBy = Environment.UserName,
            Created = DateTimeOffset.Now,
        };
    }

    public Task StartChallengeAsync(ChallengeFile challenge)
    {
        if (GameKey != "sha1:" + challenge.RomSha1.ToLowerInvariant() && GameKey != "sha1:" + challenge.RomSha1)
        {
            throw new ChallengeException($"Open {(challenge.GameTitle.Length > 0 ? challenge.GameTitle : "the challenge's game")} first. Challenges only run on the same game.");
        }

        if (_netplayActive)
        {
            throw new ChallengeException("Leave netplay first.");
        }

        if (_hardcore)
        {
            throw new ChallengeException("Challenge attempts load a save state, which hardcore mode doesn't allow. Turn hardcore off in Settings first.");
        }

        var state = Convert.FromBase64String(challenge.StartState);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_console is not { } console)
            {
                started.SetException(new ChallengeException("Start the game first."));
                return;
            }

            var backup = console.SaveSnapshot();
            try
            {
                console.LoadSnapshot(state);
            }
            catch (StateFormatException)
            {
                console.LoadSnapshot(backup);
                started.SetException(new ChallengeException("The challenge's starting point doesn't work with this game."));
                return;
            }

            if (console is NesConsole nes)
            {
                nes.SetCheats([]);
                _seenCheats = nes.Bus.Cheats;
            }

            _cheatsActive = false;
            _rewind.Clear();
            _scrubIndex = -1;
            _watcher.Resync();
            _challenge = challenge;
            _challengeInputs = [];
            _challengeActive = true;
            _present(console);
            started.SetResult();
        });
        return started.Task;
    }

    public void CancelChallenge() => Post(() =>
    {
        if (_challenge is not null)
        {
            EndChallenge(new ChallengeOutcome(ChallengeOutcomeKind.GaveUp, _challengeInputs.Count, null));
        }
    });

    public Task<ChallengeCheck> VerifyChallengeAsync(ChallengeFile challenge, ChallengeResult result)
    {
        var rom = _romData;
        var system = _romSystem;
        return rom is null
            ? Task.FromResult(new ChallengeCheck(false, "Open the challenge's game first, to check results against it."))
            : Task.Run(() => ChallengeVerifier.Verify(rom, challenge, result, system, _playStation, _n64, _playStation2, _snes));
    }

    /// <summary>Emulation thread: records this frame's buttons and checks the goal and the time limit.</summary>
    private void AfterChallengeFrame(IEmulatedConsole console, ChallengeFile challenge)
    {
        _challengeInputs.Add(((ulong)_lastInputs[0], (ulong)_lastInputs[1]));
        var frames = _challengeInputs.Count;
        if (challenge.GoalHolds(console.PeekMemory))
        {
            var result = new ChallengeResult
            {
                ChallengeId = challenge.Id,
                RomSha1 = challenge.RomSha1,
                Player = Environment.UserName,
                Frames = frames,
                Inputs = Convert.ToBase64String(InputRecording.Encode(_challengeInputs)),
                Finished = DateTimeOffset.Now,
            };
            EndChallenge(new ChallengeOutcome(ChallengeOutcomeKind.Done, frames, result));
        }
        else if (challenge.TimeLimitFrames is { } limit && frames >= limit)
        {
            EndChallenge(new ChallengeOutcome(ChallengeOutcomeKind.TimeUp, frames, null));
        }
    }

    private void EndChallenge(ChallengeOutcome outcome)
    {
        _challenge = null;
        _challengeInputs = [];
        _challengeActive = false;
        Raise(() => ChallengeFinished?.Invoke(this, outcome));
    }

    /// <summary>Emulation thread: reports what the memory-map watcher saw this frame.</summary>
    private void HandleWatch(IEmulatedConsole console, WatchResult watch)
    {
        var palette = ScreenshotPalette(console);
        var (width, height) = (console.ScreenWidth, console.ScreenHeight);
        if (watch.BeforeDeathState is { } state && watch.BeforeDeathFrame is { } screen)
        {
            var hash = console.RomHash;
            _ = Task.Run(() =>
            {
                var capture = new StateCapture(hash, state, PngEncoder.Thumbnail(screen, palette, width, height));
                Raise(() => LifeLost?.Invoke(this, capture));
            });
        }

        if (watch.NewRoom is { } room)
        {
            var frame = console.FrameBuffer.ToArray();
            var key = GameKey;
            _ = Task.Run(() =>
            {
                var args = new RoomEnteredEventArgs(
                    key, room.Area, room.Room, PngEncoder.Downscale(frame, palette, MapStore.TileWidth, MapStore.TileHeight, width, height));
                Raise(() => RoomEntered?.Invoke(this, args));
            });
        }

        if (watch.Overlay is { } values)
        {
            _statValues = values;
            Raise(() => StatValuesChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    /// <summary>A disk side was inserted or ejected (Famicom Disk System). Raised on the UI thread.</summary>
    public event EventHandler? DiskChanged;

    public int DiskSideCount { get; private set; }

    public int? InsertedDiskSide => _insertedDiskSide < 0 ? null : _insertedDiskSide;

    public void InsertDiskSide(int side) => Post(() =>
    {
        if (_console is PlayStationConsole ps)
        {
            ChangePlayStationDisc(ps, side);
        }
        else if (Nes?.Fds is { } fds && side >= 0 && side < fds.SideCount)
        {
            _diskSideRequested = side;
            fds.InsertSide(side);
            UpdateDiskSide(fds);
        }
    });

    public void EjectDisk() => Post(() =>
    {
        if (_console is PlayStationConsole ps)
        {
            OpenPlayStationShell(ps);
        }
        else if (Nes?.Fds is { } fds)
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

    /// <inheritdoc />
    public bool UsesPaddle { get; private set; }

    public bool UsesFourScore { get; private set; }

    public bool IsRewinding { get; private set; }

    public double RewindSeconds { get; private set; }

    /// <inheritdoc />
    public double RewindMaxSeconds { get; private set; }

    // The scrub bar (GV-019): the history entry shown while it is held, or -1. Emulation thread.
    private int _scrubIndex = -1;
    private double _rewindStartSeconds;

    /// <inheritdoc />
    public void BeginScrub() => Post(() =>
    {
        if (_scrubIndex < 0 && _console is not null && _challenge is null && !_hardcore && _rewind.Count > 0)
        {
            if (!_rewinding)
            {
                _rewindStartSeconds = _rewind.Duration.TotalSeconds;
            }

            _scrubIndex = _rewind.Count - 1;
            PublishRewind();
        }
    });

    /// <inheritdoc />
    public void ScrubTo(double seconds) => Post(() =>
    {
        if (_scrubIndex < 0 || _console is not { } console || _rewind.Count == 0)
        {
            return;
        }

        var index = Math.Clamp((int)Math.Round(seconds / _rewind.SecondsPerEntry) - 1, 0, _rewind.Count - 1);
        if (index == _scrubIndex)
        {
            return;
        }

        try
        {
            _rewind.Restore(index, console);
            _scrubIndex = index;
            console.RunFrameSilently();
            _present(console);
        }
        catch (Exception ex) when (ex is StateFormatException or InvalidDataException)
        {
            _rewind.Clear();
            _scrubIndex = -1;
            RaiseNotice("Rewind stopped working for this game, so its history was cleared.");
        }

        PublishRewind();
    });

    /// <inheritdoc />
    public void EndScrub() => Post(() =>
    {
        if (_scrubIndex < 0)
        {
            return;
        }

        _rewind.TruncateAfter(_scrubIndex);
        _scrubIndex = -1;
        if (!_rewinding)
        {
            _audio?.Flush();
            _watcher.Resync(); // scrubbing back isn't dying
        }

        PublishRewind();
    });

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
        if (_console is NesConsole console)
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
        RomFile rom;
        try
        {
            // Library games can be zipped (one game file inside), so read them the way the scanner does.
            rom = await Task.Run(() => RomFiles.Read(path), cancellationToken).ConfigureAwait(false)
                ?? throw new GameLoadException(
                    "That file isn't a game GameVault can open. Check that it's one of the game files GameVault plays, or a .zip with one of them inside.");
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
            _nextSaveBesideRom = settings.SavesNextToRom ? path : null;
            if (_audio is not null)
            {
                _audio.Volume = Math.Clamp(settings.Volume, 0, 100) / 100f;
                _audio.Mono = settings.MonoAudio;
            }
        }

        var data = rom.Data;
        AppleRoms? appleRoms = null;
        if (rom.System == GameSystem.AppleII && _settings is not null)
        {
            appleRoms = await LoadAppleRomsAsync(cancellationToken).ConfigureAwait(false);
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

        var playStation = rom.System == GameSystem.PlayStation
            ? await LoadPlayStationSetupAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var n64 = IsNintendo64(rom.System) ? await LoadN64SetupAsync(cancellationToken).ConfigureAwait(false) : null;
        var playStation2 = rom.System == GameSystem.PlayStation2
            ? await LoadPlayStation2SetupAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var snes = rom.System == GameSystem.Snes && _settings is not null
            ? await SnesSetup.LoadAsync(await _settings.LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
            : null;
        Load(data, Path.GetFileNameWithoutExtension(path), bios, rom.System, rom.Extension, appleRoms, playStation, n64, playStation2, snes);
    }

    /// <summary>Shown when an Apple II disk starts without the Disk II boot ROM chosen in Settings.</summary>
    public const string MissingDiskRomNotice =
        "This disk can't start without the Disk II boot ROM. Choose your own Disk II boot ROM (256 bytes) in Settings → Apple II. GameVault doesn't include it.";

    /// <summary>The player's Apple II ROMs from Settings, or null when they aren't set yet.</summary>
    private async Task<AppleRoms?> LoadAppleRomsAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings!.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.AppleIIRomPath))
        {
            return null;
        }

        try
        {
            var system = await File.ReadAllBytesAsync(settings.AppleIIRomPath, cancellationToken).ConfigureAwait(false);
            var disk = string.IsNullOrWhiteSpace(settings.AppleIIDiskRomPath)
                ? null
                : await File.ReadAllBytesAsync(settings.AppleIIDiskRomPath, cancellationToken).ConfigureAwait(false);
            var character = string.IsNullOrWhiteSpace(settings.AppleIICharacterRomPath)
                ? null
                : await File.ReadAllBytesAsync(settings.AppleIICharacterRomPath, cancellationToken).ConfigureAwait(false);
            var monitor = Enum.IsDefined((AppleMonitor)settings.AppleIIMonitor) ? (AppleMonitor)settings.AppleIIMonitor : AppleMonitor.Color;
            return new AppleRoms(system, disk, character) { Monitor = monitor };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GameLoadException("GameVault couldn't open your Apple II ROM files. Check the files chosen in Settings → Apple II.", ex);
        }
    }

    /// <summary>
    /// Loads a game from bytes already in memory. Famicom Disk System images also need the player's own
    /// 8 KB BIOS in <paramref name="fdsBios"/>; PlayStation 2 discs need the player's BIOS in <paramref name="playStation2"/>.
    /// </summary>
    public void Load(
        byte[] data,
        string title,
        byte[]? fdsBios = null,
        GameSystem system = GameSystem.Nes,
        string extension = "",
        AppleRoms? appleRoms = null,
        PlayStationSetup? playStation = null,
        N64Setup? n64 = null,
        PlayStation2Setup? playStation2 = null,
        SnesSetup? snes = null)
    {
        RomHashes hashes;
        try
        {
            hashes = RomHasher.Hash(data, system);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new GameLoadException("GameVault couldn't read that disc: " + ex.Message, ex);
        }

        var profile = ProfileResolver?.Invoke(hashes.Sha1) ?? new GameProfile { Sha1 = hashes.Sha1 };
        n64 ??= IsNintendo64(system) ? new N64Setup(SaveTypes: N64SaveTypes) : null;
        var console = ConsoleFactory.Create(data, system, profile, fdsBios, extension, appleRoms, playStation, n64, playStation2, snes);
        var nes = console as NesConsole;

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

        var battery = console.Battery;
        var batteryStore = console is PlayStationConsole ? _memoryCards : _batteries;

        // Cartridge saves can sit next to the game file when the player opts in (GV-012); memory cards stay put.
        var besideRom = batteryStore is not null && batteryStore == _batteries && _nextSaveBesideRom is { } romPath
            ? batteryStore.BesideRom(romPath)
            : null;
        _nextSaveBesideRom = null;
        if (battery is not null && batteryStore is not null)
        {
            try
            {
                batteryStore.TryLoad(console.RomHash, battery.BatteryRam, besideRom);
            }
            catch (IOException)
            {
                // A damaged or locked save shouldn't stop the game; it starts with empty RAM instead.
                RaiseNotice("GameVault couldn't read this game's save file, so the game starts without it.");
            }
        }

        _romData = data;
        _romSystem = system;
        _romExtension = extension;
        _appleRoms = appleRoms;
        _playStation = playStation;
        _n64 = n64;
        _playStation2 = playStation2;
        _snes = snes;
        LoadControllerPak(console);
        LoadPs2MemoryCard(console);
        SetUpDiscs(data, system);
        _input.TypingMode = console is AppleConsole or SegaConsole { HasKeyboard: true };
        if (console is AppleConsole && appleRoms?.DiskBoot is null)
        {
            // Without the Disk II controller's ROM the Apple starts with no disk drive, so the disk can't boot (GV-010).
            RaiseNotice(MissingDiskRomNotice);
        }

        _input.Layout = console switch
        {
            GenesisConsole => PadLayout.Genesis,
            SnesConsole => PadLayout.Snes,
            PlayStationConsole => PadLayout.PlayStation,
            N64Console => PadLayout.Nintendo64,
            Core.PlayStation2.Ps2Console => PadLayout.PlayStation2,
            SegaConsole => PadLayout.Sega,
            _ => PadLayout.Nes,
        };
        var appleBoot = console is AppleConsole { Disk: var drives } && drives.DiskIn(0) is { } bootDisk
            ? WithSavedWrites(bootDisk, AppleConsole.HashDisk(data), title)
            : null;
        byte[][]? diskOriginal = null;
        byte[][]? diskSaved = null;
        if (nes?.Fds is { } disk)
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

        if (nes is not null)
        {
            nes.ConnectInput(profile.Input switch
            {
                InputType.Zapper => InputSetup.Zapper,
                InputType.FourScore => InputSetup.FourScore,
                InputType.PowerPad => InputSetup.PowerPad,
                InputType.ArkanoidPaddle => InputSetup.ArkanoidPaddle,
                _ => InputSetup.Controllers,
            });
            nes.Ppu.RemoveSpriteLimit = profile.RemoveSpriteLimit == true || _assist.RemoveFlicker;
        }

        var passwordDefinition = nes is null ? null : PasswordDefinitionResolver?.Invoke(hashes.Sha1);
        var memoryMap = MemoryMapResolver?.Invoke(hashes.Sha1);
        console.Power();
        EnsureThread();
        Post(() =>
        {
            EndNetplay();
            _challenge = null;
            _challengeActive = false;
            FlushBattery(); // the previous game's
            ReleaseDiscs(_console);
            _console = console;
            _seenCheats = null;
            _cheatsActive = false;
            _diskOriginal = diskOriginal;
            _diskSaved = diskSaved;
            _diskSideRequested = 0;
            _reportedDiskSide = -1;
            StartAppleDrives(appleBoot);
            if (nes?.Fds is { } fds)
            {
                UpdateDiskSide(fds);
            }

            _battery = battery;
            _batteryStore = batteryStore;
            _batteryBesideRom = besideRom;
            _batterySaved = battery?.BatteryRam.ToArray() ?? [];
            _batteryTitle = title;
            TrackControllerPak(console);
            TrackPs2MemoryCard(console);
            _running = true;
            _runAhead.ResetAutoDisable();
            ApplySystemBudgets(system); // clears the rewind history
            _rewinding = false;
            _playedFrames = 0;
            _passwords.Definition = passwordDefinition;
            _watcher.Map = memoryMap;
            _statValues = [];
            _profileRemovesSpriteLimit = profile.RemoveSpriteLimit == true;
            Interlocked.Exchange(ref _frameCount, 0);
        });
        if (profile.RunAheadSafe == false)
        {
            // The game database says run-ahead breaks this game; leave it off whatever the setting says.
            RunAheadFrames = 0;
        }

        RomHash = console.RomHash;
        AchievementsHash = AchievementsHashing.ForSystem(data, system);
        System = system;
        GameKey = hashes.Key;
        Profile = profile;
        UsesZapper = nes?.InputSetup == InputSetup.Zapper;
        UsesPaddle = nes?.InputSetup == InputSetup.ArkanoidPaddle;
        _input.PowerPadActive = nes?.InputSetup == InputSetup.PowerPad;
        UsesFourScore = nes?.InputSetup == InputSetup.FourScore;
        DiskSideCount = nes?.Fds?.SideCount ?? DiscNames.Count;
        MemoryMap = memoryMap;
        _insertedDiskSide = nes?.Fds is null && DiscNames.Count == 0 ? -1 : 0;
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

    public void Reset() => Post(() =>
    {
        _console?.Reset();
        _achievements?.Reset();
    });

    public void Stop()
    {
        if (Status == EmulatorStatus.Idle)
        {
            return;
        }

        _input.TypingMode = false;
        _input.Layout = PadLayout.Nes;

        Post(() =>
        {
            EndNetplay();
            FlushBattery();
            StartAppleDrives(null);
            ReportPlaytime();
            ReleaseDiscs(_console);
            _console = null;
            _battery = null;
            _pak = null;
            _ps2Card = null;
            _running = false;
            _rewind.Clear();
            _scrubIndex = -1;
        });
        SetAudioPaused(true);
        GameTitle = null;
        RomHash = null;
        AchievementsHash = null;
        GameKey = null;
        UsesZapper = false;
        UsesPaddle = false;
        _input.PowerPadActive = false;
        UsesFourScore = false;
        DiskSideCount = 0;
        DiscNames = [];
        _discPaths = [];
        _insertedDiskSide = -1;
        Profile = null;
        System = null;
        SetStatus(EmulatorStatus.Idle);
    }

    /// <summary>Runs a read-only query against the console on the emulation thread, between frames.</summary>
    internal Task<T?> InspectAsync<T>(Func<IEmulatedConsole, T> query)
    {
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => result.SetResult(_console is null ? default : query(_console)));
        return result.Task;
    }

    /// <summary>Like <see cref="InspectAsync{T}"/>, for NES-only queries (default when the console isn't an NES).</summary>
    internal Task<T?> InspectNesAsync<T>(Func<NesConsole, T> query) =>
        InspectAsync(console => console is NesConsole nes ? query(nes) : default);

    /// <summary>
    /// Takes a save state between frames, with a thumbnail of the current screen. Null when no game is running.
    /// </summary>
    public async Task<StateCapture?> CaptureAsync()
    {
        var captured = await InspectAsync(console => (
                console.RomHash, console.SaveSnapshot(), console.FrameBuffer.ToArray(), ScreenshotPalette(console), console.ScreenWidth, console.ScreenHeight))
            .ConfigureAwait(false);
        if (captured.Item2 is null)
        {
            return null;
        }

        // PNG encoding happens off the emulation thread.
        var thumbnail = PngEncoder.Thumbnail(captured.Item3, captured.Item4, captured.Item5, captured.Item6);
        return new StateCapture(captured.RomHash, captured.Item2, thumbnail);
    }

    /// <summary>The current screen as a full-size PNG (screenshots, manual password capture).</summary>
    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        var frame = await InspectAsync(console => (
            Pixels: console.FrameBuffer.ToArray(), Palette: ScreenshotPalette(console), Width: console.ScreenWidth, Height: console.ScreenHeight)).ConfigureAwait(false);
        return frame.Pixels is null ? null : PngEncoder.Encode(PngEncoder.ToArgb(frame.Pixels, frame.Palette), frame.Width, frame.Height);
    }

    /// <summary>Loads a save state between frames. Returns false if it doesn't fit the running game.</summary>
    public Task<bool> RestoreAsync(byte[] state)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            if (_console is null || _netplay is not null || _challenge is not null || _hardcore)
            {
                // Loading a state would put the netplay games out of step, spoil an attempt, or break hardcore rules.
                result.SetResult(false);
                return;
            }

            var backup = _console.SaveSnapshot();
            try
            {
                _console.LoadSnapshot(state);
                _watcher.Resync(); // a jump in time isn't a death
                _achievements?.DeserializeProgress([]); // the state has no achievement progress: start it over
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
                if (_achievements is { } achievements)
                {
                    // Paused: rcheevos still sends pending unlocks and keeps the session alive.
                    achievements.Idle();
                    _wake.WaitOne(250);
                }
                else
                {
                    _wake.WaitOne();
                }

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

            if (audioClocked && !_pacing!.ShouldRunFrame(_audio!.BufferedFrames))
            {
                _wake.WaitOne(1);
                nextFrame = clock.ElapsedTicks;
                continue;
            }

            if (audioClocked)
            {
                _rateClock ??= Stopwatch.StartNew();
                var consumed = _samplesWritten - _audio!.BufferedFrames;
                // Slow motion: telling the resampler the CPU runs slower stretches each frame's sound, so the
                // audio clock paces fewer frames per second and the pitch drops with the speed.
                console.Audio!.ClockRate = console.CpuClockRate * Speed * _pacing!.ClockRateFactor(consumed, _rateClock.Elapsed.TotalSeconds);
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
                while ((count = resampler.ReadStereo(_samples)) > 0)
                {
                    if (audioClocked)
                    {
                        _samplesWritten += _audio!.Write(_samples.AsSpan(0, count * 2));
                    }
                }
            }

            if (audioClocked)
            {
                nextFrame = clock.ElapsedTicks;
                continue;
            }

            nextFrame += fastForward ? frameTicks / MaxFastForward : frameTicks / Speed;
            if (!WaitUntil(clock, nextFrame))
            {
                // Fell far behind (debugger, system sleep): re-anchor instead of bursting to catch up.
                nextFrame = clock.ElapsedTicks;
            }
        }
    }

    private bool TryRunFrame(IEmulatedConsole console)
    {
        try
        {
            for (var port = 0; port < InputService.PortCount; port++)
            {
                var state = _input.Sample(port, console.FrameCount);
                _lastInputs[port] = state;
                if (port < console.ControllerPorts)
                {
                    console.SetControllerState(port, state);
                }
            }

            if (console is AppleConsole apple)
            {
                Type(apple);
            }
            else if (console is SegaConsole { HasKeyboard: true } sc3000)
            {
                sc3000.SetKeyboardRows(Sc3000Keyboard.Matrix(_input.TypingKeysHeld));
            }

            if (console is NesConsole { InputSetup: InputSetup.Zapper } nes)
            {
                var (aim, trigger) = _input.SampleZapper();
                nes.Zapper.Aim = aim;
                nes.Zapper.Trigger = trigger;
            }

            if (console is NesConsole { InputSetup: InputSetup.PowerPad } mat)
            {
                mat.PowerPad.Buttons = _input.SamplePowerPad();
            }

            if (console is NesConsole { InputSetup: InputSetup.ArkanoidPaddle } vaus)
            {
                var (fraction, fire) = _input.SamplePaddle();
                vaus.Paddle.SetFraction(fraction);
                vaus.Paddle.Fire = fire;
            }

            if (console is NesConsole withCodes && withCodes.Bus.Cheats != _seenCheats)
            {
                _seenCheats = withCodes.Bus.Cheats;
                var codes = withCodes.Cheats;
                _cheatsActive = codes.Count > 0;
                Raise(() => CheatsChanged?.Invoke(this, codes));
            }

            _rewind.OnFrame(console);
            if (console is NesConsole { Fds: { } drive })
            {
                UpdateDiskSide(drive);
            }

            var runAheadOff = _runAhead.RunFrame(console, _present);
            if (console is NesConsole passwordConsole && _passwords.OnFrame(passwordConsole) is { } password)
            {
                var png = PngEncoder.Encode(PngEncoder.ToArgb(console.FrameBuffer, _palette), 256, 240);
                Raise(() => PasswordCaptured?.Invoke(this, new PasswordCapturedEventArgs(password, png)));
            }

            if (_challenge is { } challenge)
            {
                AfterChallengeFrame(console, challenge);
            }

            // After run-ahead put the console back on the real frame, so achievements see real memory.
            _achievements?.DoFrame();

            var watch = _watcher.OnFrame(console);
            if (!watch.IsEmpty)
            {
                HandleWatch(console, watch);
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

    /// <summary>Keys typed since the last frame go to the Apple's keyboard; Ctrl+F12 is its Reset key.</summary>
    private void Type(AppleConsole apple)
    {
        while (_input.TryDequeueTyped(out var key, out var shift, out var control))
        {
            if (key == VirtualKeys.F12 && control)
            {
                apple.Reset();
            }
            else if (AppleKeyboard.ToAscii(key, shift, control) is { } ascii)
            {
                apple.TypeKey(ascii);
            }
        }

        apple.SetKeyHeld(_input.AnyTypingKeyHeld);
    }

    /// <summary>
    /// Backspace or the left shoulder (input.md §8) held: walk back through the rewind history. Returns true
    /// while rewinding; raises <see cref="RewindChanged"/> on start and stop.
    /// </summary>
    private bool UpdateRewinding()
    {
        // No rewinding in a challenge attempt or in hardcore mode.
        // While typing into a computer, Backspace types; Shift+Backspace rewinds.
        var held = _input.IsHotkeyHeld(VirtualKeys.Backspace) && (!_input.TypingMode || _input.ShiftHeld) && _challenge is null && !_hardcore;
        if (held != _rewinding)
        {
            _rewinding = held;
            if (held && _scrubIndex < 0)
            {
                _rewindStartSeconds = _rewind.Duration.TotalSeconds;
            }

            if (!held && _scrubIndex < 0)
            {
                _audio?.Flush(); // resume from silence
                _watcher.Resync(); // rewinding isn't dying
            }

            PublishRewind();
        }

        // While the scrub bar is held the game holds still, whether or not the rewind button still is.
        return held || _scrubIndex >= 0;
    }

    /// <summary>One step of rewind: restore an older state and show the frame that follows it, silently.</summary>
    private bool TryRewindFrame(IEmulatedConsole console)
    {
        if (_scrubIndex >= 0)
        {
            _wake.WaitOne(15); // the scrub bar moves the picture; nothing runs meanwhile
            return false;
        }

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
            _scrubIndex = -1;
            RaiseNotice("Rewind stopped working for this game, so its history was cleared.");
            return true;
        }
    }

    private void PublishRewind()
    {
        var rewinding = _rewinding || _scrubIndex >= 0;
        var seconds = _scrubIndex >= 0 ? (_scrubIndex + 1) * _rewind.SecondsPerEntry : _rewind.Duration.TotalSeconds;
        var max = Math.Max(_rewindStartSeconds, seconds);
        Raise(() =>
        {
            IsRewinding = rewinding;
            RewindSeconds = seconds;
            RewindMaxSeconds = max;
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
        if (diskKey && !_diskKeyHeld && Nes?.Fds is { } fds)
        {
            _diskSideRequested = (_diskSideRequested + 1) % fds.SideCount;
            fds.InsertSide(_diskSideRequested);
            RaiseNotice($"Inserting {DiskSideName(_diskSideRequested)}.");
            UpdateDiskSide(fds);
        }
        else if (diskKey && !_diskKeyHeld && _console is PlayStationConsole ps && _discPaths.Count > 1)
        {
            var next = (_diskSideRequested + 1) % _discPaths.Count;
            RaiseNotice($"Changing to {DiscNames[next]}.");
            ChangePlayStationDisc(ps, next);
        }

        _diskKeyHeld = diskKey;

        // F2: back to before the last life lost (SPEC §7.9).
        var livesKey = _input.IsHotkeyHeld(VirtualKeys.F2);
        if (livesKey && !_livesKeyHeld)
        {
            Raise(() => BeforeDeathRequested?.Invoke(this, EventArgs.Empty));
        }

        _livesKeyHeld = livesKey;

        // F3: every code off at once (SPEC §7.8; input.md §8).
        var codesOff = _input.IsHotkeyHeld(VirtualKeys.F3);
        if (codesOff && !_codesOffKeyHeld && _console is NesConsole { Cheats.Count: > 0 } withCodes)
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
        FlushControllerPak(background);
        FlushPs2MemoryCard(background);
        FlushDisk(background);
        FlushAppleDisks(background);
    }

    /// <summary>
    /// Disk writes go to "Title - hash.fdssave" as a patch against the image, never into the image itself
    /// (Phase 7 plan).
    /// </summary>
    private void FlushDisk(bool background)
    {
        if (Nes?.Fds is not { IsDiskModified: true } fds || _diskSaves is null || _diskOriginal is null || _diskSaved is null)
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
        if (_battery is null || _batteryStore is not { } store || _battery.BatteryRam.SequenceEqual(_batterySaved))
        {
            return;
        }

        var data = _battery.BatteryRam.ToArray();
        _batterySaved = data;
        var title = _batteryTitle;
        var hash = _console!.RomHash;
        var beside = _batteryBesideRom;

        void Write()
        {
            try
            {
                store.Save(title, hash, data, beside);
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

/// <summary>A room or screen the player entered, for automatic maps (SPEC §7.9).</summary>
public sealed record RoomEnteredEventArgs(string? GameKey, int Area, int Room, uint[] Tile);
