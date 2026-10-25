using System.Net;
using System.Net.Sockets;

namespace GameVault.Platform.Netplay;

/// <summary>
/// Netplay over UDP on the local network (SPEC §7.7; the owner chose LAN-only to start). The host listens
/// and takes the first player who sends a packet as its peer; the joining player knows the host's address
/// from the invite code. Packets from anyone else are ignored. Never blocks: <see cref="TryReceive"/> only
/// reads datagrams that have already arrived.
/// </summary>
public sealed class UdpNetplayLink : INetplayLink, IDisposable
{
    /// <summary>The port the host tries first; any free port is used if it is taken.</summary>
    public const int DefaultPort = 47800;

    private readonly UdpClient _udp;
    private IPEndPoint? _peer;

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
                    continue; // someone else: one game, two players
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
