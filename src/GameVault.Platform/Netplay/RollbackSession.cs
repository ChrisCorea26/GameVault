using System.IO.Hashing;
using GameVault.Core;
using GameVault.Core.Input;

namespace GameVault.Platform.Netplay;

public enum NetplayState
{
    /// <summary>Waiting for the other player to answer.</summary>
    Connecting,

    Running,

    /// <summary>The other player is too far behind; the game waits rather than guess further ahead.</summary>
    Waiting,

    /// <summary>The two games no longer match (checksums differ). The session stops.</summary>
    Desynced,

    /// <summary>The other player has a different game or settings.</summary>
    Mismatch,
}

/// <summary>
/// Rollback netplay for two players (SPEC §7.7), built on the deterministic core and fast save states:
/// <list type="bullet">
/// <item>Each player's input is scheduled <see cref="InputDelay"/> frames ahead (0–2).</item>
/// <item>The other player's input is predicted (their last known input repeated) and the frame runs at once.</item>
/// <item>When their real input arrives and differs, the game loads the state from before that frame and runs
/// forward again with the right inputs, silently, all within one frame.</item>
/// <item>The game never predicts more than <see cref="MaxRollbackFrames"/> ahead of what it knows; past that it
/// waits.</item>
/// <item>Checksums of frames both inputs are final for are exchanged; a mismatch stops the session
/// (<see cref="NetplayState.Desynced"/>).</item>
/// </list>
/// The session is single-threaded: call <see cref="AdvanceFrame"/> once per frame on the emulation thread.
/// </summary>
public sealed class RollbackSession
{
    public const int ProtocolVersion = 1;
    public const int MaxRollbackFrames = 8;

    /// <summary>Checksums cover every Nth confirmed frame.</summary>
    public const int ChecksumInterval = 30;

    /// <summary>How many of our recent inputs each packet repeats, to cover lost packets.</summary>
    private const int MaxInputsPerPacket = 32;

    private readonly NesConsole _console;
    private readonly INetplayLink _link;
    private readonly int _localPort;
    private readonly Dictionary<int, byte> _localInputs = [];
    private readonly Dictionary<int, byte> _remoteInputs = [];
    private readonly Dictionary<int, byte> _predicted = [];
    private readonly Dictionary<int, byte[]> _states = [];
    private readonly Dictionary<int, uint> _localChecksums = [];
    private readonly Dictionary<int, uint> _remoteChecksums = [];
    private int _remoteContiguous = -1; // newest frame with every remote input up to it received
    private int _remoteAck = -1;        // newest frame of ours the other side has
    private bool _helloAnswered;
    private bool _remoteHelloSeen;
    private int _lastChecksumSent = -1;
    private int _nextChecksumFrame = ChecksumInterval;

    /// <param name="localPlayer">0 for player 1 (the host), 1 for player 2.</param>
    public RollbackSession(NesConsole console, INetplayLink link, int localPlayer, int inputDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(localPlayer, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPlayer, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputDelay, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputDelay, 2);
        _console = console;
        _link = link;
        _localPort = localPlayer;
        InputDelay = inputDelay;

        // The frames before the delay has passed have no input from either side.
        for (var frame = 0; frame < inputDelay; frame++)
        {
            _localInputs[frame] = 0;
            _remoteInputs[frame] = 0;
        }

        _remoteContiguous = inputDelay - 1;
    }

    public int InputDelay { get; }

    public NetplayState State { get; private set; } = NetplayState.Connecting;

    /// <summary>The next frame to run.</summary>
    public int Frame { get; private set; }

    /// <summary>Frames re-run because a prediction was wrong (for diagnostics and the UI).</summary>
    public long RolledBackFrames { get; private set; }

    /// <summary>The first frame whose checksums differed, when <see cref="State"/> is Desynced.</summary>
    public int? DesyncFrame { get; private set; }

    /// <summary>Every frame up to this one has both players' real inputs (final).</summary>
    public int ConfirmedFrame => Math.Min(Frame - 1, _remoteContiguous);

    /// <summary>The inputs both players used for a confirmed frame (player 1, player 2), for replays and tests.</summary>
    public (ControllerState Player1, ControllerState Player2) ConfirmedInputs(int frame)
    {
        if (frame > ConfirmedFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "That frame isn't confirmed yet.");
        }

        var local = (ControllerState)_localInputs[frame];
        var remote = (ControllerState)_remoteInputs[frame];
        return _localPort == 0 ? (local, remote) : (remote, local);
    }

    /// <summary>
    /// Takes this player's buttons, exchanges inputs, rolls back if a prediction was wrong, and runs one frame.
    /// Returns false when the frame didn't run (still connecting, waiting for the other player, or stopped).
    /// </summary>
    public bool AdvanceFrame(ControllerState localButtons)
    {
        if (State is NetplayState.Desynced or NetplayState.Mismatch)
        {
            return false;
        }

        Receive();
        if (State is NetplayState.Desynced or NetplayState.Mismatch)
        {
            return false;
        }

        if (!_remoteHelloSeen)
        {
            SendPacket();
            return false;
        }

        if (Frame - _remoteContiguous > MaxRollbackFrames)
        {
            State = NetplayState.Waiting;
            SendPacket();
            return false;
        }

        State = NetplayState.Running;
        var inputFrame = Frame + InputDelay;
        _localInputs.TryAdd(inputFrame, (byte)localButtons);
        SendPacket();

        SaveState(Frame);
        RunFrame(Frame, silent: false);
        Frame++;
        Forget();
        return true;
    }

    /// <summary>
    /// Exchanges packets without running a frame: while paused or waiting, so the other side keeps hearing
    /// from us and late inputs still correct (roll back) the frames already run.
    /// </summary>
    public void Poll()
    {
        if (State is NetplayState.Desynced or NetplayState.Mismatch)
        {
            return;
        }

        Receive();
        if (State is not (NetplayState.Desynced or NetplayState.Mismatch))
        {
            SendPacket();
        }
    }

    private void Receive()
    {
        var rollbackTo = int.MaxValue;
        while (_link.TryReceive(out var packet))
        {
            if (packet.Hello is { } hello)
            {
                if (hello.RomHash != _console.RomHash || hello.InputDelay != InputDelay || hello.ProtocolVersion != ProtocolVersion)
                {
                    State = NetplayState.Mismatch;
                    return;
                }

                _remoteHelloSeen = true;
            }

            if (packet.AckFrame >= 0)
            {
                _helloAnswered = true;
                _remoteAck = Math.Max(_remoteAck, packet.AckFrame);
            }

            for (var i = 0; i < packet.Inputs.Length; i++)
            {
                var frame = packet.FirstFrame + i;
                if (_remoteInputs.ContainsKey(frame) || frame <= _remoteContiguous)
                {
                    continue;
                }

                var input = packet.Inputs[i];
                _remoteInputs[frame] = input;
                if (frame < Frame && _predicted.TryGetValue(frame, out var guess) && guess != input)
                {
                    rollbackTo = Math.Min(rollbackTo, frame);
                }
            }

            while (_remoteInputs.ContainsKey(_remoteContiguous + 1))
            {
                _remoteContiguous++;
            }

            if (packet.Checksum is { } checksum)
            {
                _remoteChecksums[checksum.Frame] = checksum.Value;
            }
        }

        if (rollbackTo != int.MaxValue)
        {
            Rollback(rollbackTo);
        }

        ChecksumFinalStates();
        CompareChecksums();
    }

    /// <summary>Loads the state from before <paramref name="frame"/> and runs forward with what is known now.</summary>
    private void Rollback(int frame)
    {
        _console.LoadSnapshot(_states[frame]);
        for (var f = frame; f < Frame; f++)
        {
            if (f > frame)
            {
                SaveState(f);
            }

            RunFrame(f, silent: true);
            RolledBackFrames++;
        }
    }

    private void RunFrame(int frame, bool silent)
    {
        var local = _localInputs.GetValueOrDefault(frame);
        byte remote;
        if (_remoteInputs.TryGetValue(frame, out var known))
        {
            remote = known;
            _predicted.Remove(frame);
        }
        else
        {
            // Predict that the other player is still holding what they last held.
            remote = _remoteContiguous >= 0 ? _remoteInputs.GetValueOrDefault(_remoteContiguous) : (byte)0;
            _predicted[frame] = remote;
        }

        _console.SetControllerState(_localPort, (ControllerState)local);
        _console.SetControllerState(1 - _localPort, (ControllerState)remote);
        if (silent)
        {
            _console.RunFrameSilently();
        }
        else
        {
            _console.RunFrame();
        }
    }

    private void SaveState(int frame) => _states[frame] = _console.SaveSnapshot();

    /// <summary>
    /// Checksums the states that became final: a state at the start of a frame is final once every input
    /// before it is known and any rollback has corrected it.
    /// </summary>
    private void ChecksumFinalStates()
    {
        while (_nextChecksumFrame - 1 <= _remoteContiguous && _nextChecksumFrame < Frame)
        {
            if (_states.TryGetValue(_nextChecksumFrame, out var state))
            {
                _localChecksums[_nextChecksumFrame] = Crc32.HashToUInt32(state);
            }

            _nextChecksumFrame += ChecksumInterval;
        }
    }

    private void CompareChecksums()
    {
        foreach (var (frame, value) in _remoteChecksums)
        {
            if (_localChecksums.TryGetValue(frame, out var mine) && mine != value)
            {
                State = NetplayState.Desynced;
                DesyncFrame = DesyncFrame is { } earlier ? Math.Min(earlier, frame) : frame;
            }
        }
    }

    private void SendPacket()
    {
        var first = Math.Max(_remoteAck + 1, Frame + InputDelay - MaxInputsPerPacket + 1);
        first = Math.Max(first, 0);
        var inputs = new List<byte>();
        for (var frame = first; _localInputs.TryGetValue(frame, out var input) && inputs.Count < MaxInputsPerPacket; frame++)
        {
            inputs.Add(input);
        }

        FrameChecksum? checksum = null;
        var newest = _localChecksums.Keys.Where(f => f > _lastChecksumSent).DefaultIfEmpty(-1).Max();
        if (newest >= 0)
        {
            checksum = new FrameChecksum(newest, _localChecksums[newest]);
            _lastChecksumSent = newest;
        }
        else if (_localChecksums.Count > 0)
        {
            var latest = _localChecksums.Keys.Max();
            checksum = new FrameChecksum(latest, _localChecksums[latest]); // repeat it in case it was lost
        }

        _link.Send(new NetplayPacket
        {
            Hello = _helloAnswered ? null : new HelloMessage(_console.RomHash, InputDelay, ProtocolVersion),
            FirstFrame = first,
            Inputs = [.. inputs],
            AckFrame = _remoteContiguous,
            Checksum = checksum,
        });
    }

    /// <summary>Drops states and inputs no rollback or resend can need any more.</summary>
    private void Forget()
    {
        var oldestNeeded = Math.Min(_remoteContiguous + 1, Frame - MaxRollbackFrames - 1);
        foreach (var frame in _states.Keys.Where(f => f < oldestNeeded).ToList())
        {
            _states.Remove(frame);
        }

        foreach (var frame in _predicted.Keys.Where(f => f <= _remoteContiguous).ToList())
        {
            _predicted.Remove(frame);
        }

        var keepChecksumsFrom = Frame - 20 * ChecksumInterval;
        foreach (var frame in _localChecksums.Keys.Where(f => f < keepChecksumsFrom).ToList())
        {
            _localChecksums.Remove(frame);
        }

        foreach (var frame in _remoteChecksums.Keys.Where(f => f < keepChecksumsFrom).ToList())
        {
            _remoteChecksums.Remove(frame);
        }
    }
}
