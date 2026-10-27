using System.Net;
using System.Net.Sockets;

namespace GameVault.Platform.Netplay;

/// <summary>
/// Netplay over UDP (SPEC §7.7). On the local network the host listens and takes the first player who sends a packet
/// as its peer; the joining player knows the host's address from the invite code. Over the internet
/// (outstanding-work plan §13) each side first learns its public address from a STUN server
/// (<see cref="DiscoverPublicEndPointAsync"/>), the host is told the joiner's through the reply code
/// (<see cref="ExpectPeer"/>), and both send until their routers let the packets through; or both join a room on a
/// relay (<see cref="JoinRelayAsync"/>), which is then the peer. Packets from anyone else are ignored. Never blocks:
/// <see cref="TryReceive"/> only reads datagrams that have already arrived.
/// </summary>
public sealed class UdpNetplayLink : INetplayLink, IDisposable
{
    /// <summary>The port the host tries first; any free port is used if it is taken.</summary>
    public const int DefaultPort = 47800;

    private readonly UdpClient _udp;
    private IPEndPoint? _peer;

    // Over the internet a router may give the other player's packets a different port than their STUN reply said:
    // packets from the same address are then taken as theirs.
    private bool _anyPortFromPeer;

    private UdpNetplayLink(UdpClient udp, IPEndPoint? peer)
    {
        _udp = udp;
        _peer = peer;
        if (OperatingSystem.IsWindows())
        {
            // Don't let an ICMP "port unreachable" from a peer that left break later receives (SIO_UDP_CONNRESET).
            const int SioUdpConnReset = -1744830452;
            _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
    }

    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    /// <summary>The other player, once known.</summary>
    public IPEndPoint? Peer => _peer;

    /// <summary>When the last packet from the peer arrived (UTC), or null before the first.</summary>
    public DateTime? LastReceived { get; private set; }

    /// <summary>Hosts a game: listens on <paramref name="port"/>, or any free port if that one is taken.</summary>
    public static UdpNetplayLink Listen(int port = DefaultPort)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        }

        return new UdpNetplayLink(udp, peer: null);
    }

    /// <summary>Joins the host at <paramref name="host"/>.</summary>
    public static UdpNetplayLink Connect(IPEndPoint host) =>
        new(new UdpClient(new IPEndPoint(IPAddress.Any, 0)), host);

    /// <summary>The host was told who will join (their reply code): send to them, so our router lets their packets in.</summary>
    public void ExpectPeer(IPEndPoint peer)
    {
        _peer = peer;
        _anyPortFromPeer = true;
    }

    /// <summary>
    /// Asks STUN servers, through this socket, what address and port the internet sees it from. Null when none
    /// answers within <paramref name="timeout"/> (no internet, or UDP blocked). Call before the game's packets flow.
    /// </summary>
    public async Task<IPEndPoint?> DiscoverPublicEndPointAsync(IEnumerable<IPEndPoint> servers, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        foreach (var server in servers)
        {
            var request = StunMessage.Request(out var transaction);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            try
            {
                for (var attempt = 0; attempt < 3 && !wait.IsCancellationRequested; attempt++)
                {
                    await _udp.SendAsync(request, server, wait.Token).ConfigureAwait(false);
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
                    retry.CancelAfter(timeout / 3);
                    while (true)
                    {
                        var received = await _udp.ReceiveAsync(retry.Token).ConfigureAwait(false);
                        if (StunMessage.TryParseResponse(received.Buffer, transaction, out var mapped))
                        {
                            return mapped;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This server didn't answer in time; try the next.
            }
            catch (SocketException)
            {
            }
        }

        return null;
    }

    /// <summary>Resolves the public STUN servers (IPv4 only). Empty without DNS.</summary>
    public static async Task<IReadOnlyList<IPEndPoint>> ResolveAsync(IEnumerable<(string Host, int Port)> servers, CancellationToken cancellationToken = default)
    {
        var resolved = new List<IPEndPoint>();
        foreach (var (host, port) in servers)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    resolved.Add(new IPEndPoint(addresses[0], port));
                }
            }
            catch (SocketException)
            {
            }
        }

        return resolved;
    }

    /// <summary>
    /// Takes a seat in <paramref name="room"/> on a relay, which becomes the peer. False when the relay doesn't answer
    /// within <paramref name="timeout"/> or the room is full.
    /// </summary>
    public async Task<bool> JoinRelayAsync(IPEndPoint relay, uint room, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var join = GameVault.Relay.NetplayRelay.JoinPacket(room);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            while (true)
            {
                await _udp.SendAsync(join, relay, wait.Token).ConfigureAwait(false);
                using var retry = CancellationTokenSource.CreateLinkedTokenSource(wait.Token);
                retry.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    while (true)
                    {
                        var received = await _udp.ReceiveAsync(retry.Token).ConfigureAwait(false);
                        if (received.RemoteEndPoint.Equals(relay) && received.Buffer.AsSpan().SequenceEqual(GameVault.Relay.NetplayRelay.AckMagic))
                        {
                            _peer = relay;
                            return true;
                        }
                    }
                }
                catch (OperationCanceledException) when (!wait.IsCancellationRequested)
                {
                    // Ask again.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Send(NetplayPacket packet)
    {
        if (_peer is null)
        {
            return;
        }

        try
        {
            var data = NetplayCodec.Encode(packet);
            _udp.Send(data, data.Length, _peer);
        }
        catch (SocketException)
        {
            // The network is unavailable for a moment; the next packet repeats everything anyway.
        }
    }

    public bool TryReceive(out NetplayPacket packet)
    {
        packet = null!;
        try
        {
            while (_udp.Available > 0)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref from);
                if (!NetplayCodec.TryDecode(data, out var decoded))
                {
                    continue;
                }

                _peer ??= from;
                if (!from.Equals(_peer))
                {
                    if (!_anyPortFromPeer || !from.Address.Equals(_peer.Address))
                    {
                        continue; // someone else: one game, two players
                    }

                    _peer = from; // their router gave them another port
                }

                LastReceived = DateTime.UtcNow;
                packet = decoded;
                return true;
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        return false;
    }

    public void Dispose() => _udp.Dispose();
}
