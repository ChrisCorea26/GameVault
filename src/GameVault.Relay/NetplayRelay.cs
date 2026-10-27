using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GameVault.Relay;

/// <summary>
/// Pairs two netplay players by room and forwards their packets to each other (outstanding-work plan §13). A player
/// joins a room by sending <c>GVRJ</c> and the room's 32-bit number; the relay answers <c>GVRA</c> each time. After
/// that every datagram from one of a room's two players goes to the other. Rooms idle for five minutes are forgotten,
/// and a third player only takes a seat whose player has been silent for a minute.
/// </summary>
public sealed class NetplayRelay : IDisposable
{
    public const int DefaultPort = 47801;

    public static ReadOnlySpan<byte> JoinMagic => "GVRJ"u8;

    public static ReadOnlySpan<byte> AckMagic => "GVRA"u8;

    private static readonly TimeSpan SeatTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RoomTimeout = TimeSpan.FromMinutes(5);

    private readonly UdpClient _udp;
    private readonly Dictionary<uint, Room> _rooms = [];
    private readonly Dictionary<IPEndPoint, uint> _roomOf = [];
    private readonly Func<DateTime> _clock;

    public NetplayRelay(int port = DefaultPort, Func<DateTime>? clock = null)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _clock = clock ?? (() => DateTime.UtcNow);
        if (OperatingSystem.IsWindows())
        {
            const int SioUdpConnReset = -1744830452;
            _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>Rooms with at least one player (for the console and tests).</summary>
    public int RoomCount
    {
        get
        {
            lock (_rooms)
            {
                return _rooms.Count;
            }
        }
    }

    /// <summary>Two players were paired in a room (the room's number).</summary>
    public event Action<uint>? Paired;

    /// <summary>The join packet a player sends for <paramref name="room"/>.</summary>
    public static byte[] JoinPacket(uint room)
    {
        var packet = new byte[8];
        JoinMagic.CopyTo(packet);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), room);
        return packet;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // a player went away; carry on
            }

            Handle(received.Buffer, received.RemoteEndPoint);
        }
    }

    /// <summary>One datagram from <paramref name="from"/>.</summary>
    internal void Handle(byte[] data, IPEndPoint from)
    {
        var now = _clock();
        IPEndPoint? forwardTo = null;
        var ack = false;
        uint? paired = null;
        lock (_rooms)
        {
            Expire(now);
            if (data.Length == 8 && data.AsSpan(0, 4).SequenceEqual(JoinMagic))
            {
                var room = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
                ack = Join(room, from, now, out var nowPaired);
                paired = nowPaired ? room : null;
            }
            else if (_roomOf.TryGetValue(from, out var room) && _rooms.TryGetValue(room, out var seats))
            {
                var mine = seats.A?.Equals(from) == true ? 0 : 1;
                seats.LastSeen[mine] = now;
                forwardTo = mine == 0 ? seats.B : seats.A;
            }
        }

        try
        {
            if (ack)
            {
                _udp.Send(AckMagic.ToArray(), 4, from);
            }

            if (forwardTo is not null)
            {
                _udp.Send(data, data.Length, forwardTo);
            }
        }
        catch (SocketException)
        {
        }

        if (paired is { } p)
        {
            Paired?.Invoke(p);
        }
    }

    private bool Join(uint room, IPEndPoint from, DateTime now, out bool paired)
    {
        paired = false;
        if (!_rooms.TryGetValue(room, out var seats))
        {
            seats = new Room();
            _rooms[room] = seats;
        }

        if (seats.A?.Equals(from) == true || seats.B?.Equals(from) == true)
        {
            var mine = seats.A?.Equals(from) == true ? 0 : 1;
            seats.LastSeen[mine] = now;
            return true;
        }

        // An empty seat, or one whose player has gone quiet.
        int seat;
        if (seats.A is null || now - seats.LastSeen[0] > SeatTimeout)
        {
            seat = 0;
        }
        else if (seats.B is null || now - seats.LastSeen[1] > SeatTimeout)
        {
            seat = 1;
        }
        else
        {
            return false; // the room is full: no answer
        }

        var old = seat == 0 ? seats.A : seats.B;
        if (old is not null)
        {
            _roomOf.Remove(old);
        }

        if (_roomOf.TryGetValue(from, out var previous) && previous != room)
        {
            Leave(from, previous);
        }

        if (seat == 0)
        {
            seats.A = from;
        }
        else
        {
            seats.B = from;
        }

        seats.LastSeen[seat] = now;
        _roomOf[from] = room;
        paired = seats.A is not null && seats.B is not null;
        return true;
    }

    private void Leave(IPEndPoint player, uint room)
    {
        _roomOf.Remove(player);
        if (_rooms.TryGetValue(room, out var seats))
        {
            if (seats.A?.Equals(player) == true)
            {
                seats.A = null;
            }

            if (seats.B?.Equals(player) == true)
            {
                seats.B = null;
            }
        }
    }

    private void Expire(DateTime now)
    {
        List<uint>? idle = null;
        foreach (var (room, seats) in _rooms)
        {
            var last = seats.LastSeen[0] > seats.LastSeen[1] ? seats.LastSeen[0] : seats.LastSeen[1];
            if (now - last > RoomTimeout || (seats.A is null && seats.B is null))
            {
                (idle ??= []).Add(room);
            }
        }

        foreach (var room in idle ?? [])
        {
            var seats = _rooms[room];
            if (seats.A is not null)
            {
                _roomOf.Remove(seats.A);
            }

            if (seats.B is not null)
            {
                _roomOf.Remove(seats.B);
            }

            _rooms.Remove(room);
        }
    }

    public void Dispose() => _udp.Dispose();

    private sealed class Room
    {
        public IPEndPoint? A { get; set; }

        public IPEndPoint? B { get; set; }

        public DateTime[] LastSeen { get; } = new DateTime[2];
    }
}
