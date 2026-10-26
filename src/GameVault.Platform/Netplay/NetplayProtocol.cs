namespace GameVault.Platform.Netplay;

/// <summary>
/// One message between the two players (SPEC §7.7). Every packet carries the sender's recent inputs, so a lost
/// packet is covered by the next one, and how far the sender has heard from us, so old inputs stop being
/// resent. Packets never contain ROM data; the ROM is identified by its hash in <see cref="Hello"/>.
/// </summary>
public sealed record NetplayPacket
{
    /// <summary>Sent until the other side answers: the game (ROM hash) and settings both must share.</summary>
    public HelloMessage? Hello { get; init; }

    /// <summary>The sender has received our <see cref="Hello"/>, so we can stop repeating it.</summary>
    public bool HelloSeen { get; init; }

    /// <summary>The frame of <see cref="Inputs"/>[0].</summary>
    public int FirstFrame { get; init; }

    /// <summary>The sender's controller for consecutive frames, starting at <see cref="FirstFrame"/>.</summary>
    public ushort[] Inputs { get; init; } = [];

    /// <summary>The newest frame of ours the sender has received without gaps (-1 for none).</summary>
    public int AckFrame { get; init; } = -1;

    /// <summary>A checksum of the sender's state at the start of a frame both inputs are final for.</summary>
    public FrameChecksum? Checksum { get; init; }
}

public sealed record HelloMessage(ulong RomHash, int InputDelay, int ProtocolVersion);

public readonly record struct FrameChecksum(int Frame, uint Value);

/// <summary>
/// Moves packets between the two players. The real transport (UDP through a relay/STUN service) waits on the
/// owner's choice of service (Phase 7 plan); <see cref="SimulatedLink"/> stands in for it in tests.
/// Implementations may drop, delay or reorder packets.
/// </summary>
public interface INetplayLink
{
    void Send(NetplayPacket packet);

    bool TryReceive(out NetplayPacket packet);
}

/// <summary>
/// Two connected ends of a lossy, delayed network, for tests: packets arrive after a latency (plus random
/// jitter, which also reorders them) or are dropped. Time advances with <see cref="Tick"/>, one call per
/// frame, and the randomness is seeded, so every run is the same.
/// </summary>
public sealed class SimulatedNetwork
{
    private readonly Random _random;
    private readonly int _latency;
    private readonly int _jitter;
    private readonly double _loss;
    private long _now;

    public SimulatedNetwork(int latencyFrames, int jitterFrames = 0, double lossRate = 0, int seed = 1)
    {
        _random = new Random(seed);
        _latency = latencyFrames;
        _jitter = jitterFrames;
        _loss = lossRate;
        A = new SimulatedLink(this);
        B = new SimulatedLink(this);
        A.Peer = B;
        B.Peer = A;
    }

    public SimulatedLink A { get; }

    public SimulatedLink B { get; }

    /// <summary>When false, nothing gets through (a dropped connection).</summary>
    public bool Connected { get; set; } = true;

    public void Tick() => _now++;

    internal void Deliver(SimulatedLink to, NetplayPacket packet)
    {
        if (!Connected || _random.NextDouble() < _loss)
        {
            return;
        }

        to.Enqueue(_now + _latency + (_jitter > 0 ? _random.Next(_jitter + 1) : 0), packet);
    }

    internal long Now => _now;
}

public sealed class SimulatedLink : INetplayLink
{
    private readonly SimulatedNetwork _network;
    private readonly PriorityQueue<NetplayPacket, (long At, long Order)> _inbox = new();
    private long _order;

    internal SimulatedLink(SimulatedNetwork network) => _network = network;

    internal SimulatedLink Peer { get; set; } = null!;

    public void Send(NetplayPacket packet) => _network.Deliver(Peer, packet);

    public bool TryReceive(out NetplayPacket packet)
    {
        if (_inbox.TryPeek(out var next, out var key) && key.At <= _network.Now)
        {
            packet = _inbox.Dequeue();
            return true;
        }

        packet = null!;
        return false;
    }

    internal void Enqueue(long at, NetplayPacket packet) => _inbox.Enqueue(packet, (at, _order++));
}
