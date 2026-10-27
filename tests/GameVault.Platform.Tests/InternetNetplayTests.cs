using System.Net;
using System.Net.Sockets;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Netplay;
using GameVault.Platform.Video;
using GameVault.Relay;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>
/// Internet netplay (outstanding-work plan §13) on loopback: STUN against a stand-in server, the reply code, invite
/// codes for internet hosts and relays, and the relay pairing two players.
/// </summary>
public sealed class InternetNetplayTests : IDisposable
{
    private readonly UdpClient _stun = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly NetplayRelay _relay = new(0);

    public InternetNetplayTests()
    {
        // The stand-in STUN server tells each client the address it sees, as a public one would.
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var request = await _stun.ReceiveAsync(_stop.Token);
                    var reply = StunMessage.Response(request.Buffer, request.RemoteEndPoint);
                    await _stun.SendAsync(reply, request.RemoteEndPoint, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                }
            }
        });
        _ = _relay.RunAsync(_stop.Token);
    }

    private IPEndPoint StunEndPoint => new(IPAddress.Loopback, ((IPEndPoint)_stun.Client.LocalEndPoint!).Port);

    public void Dispose()
    {
        _stop.Cancel();
        _stun.Dispose();
        _relay.Dispose();
    }

    private static void WaitFor(Func<bool> condition, string because, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            Thread.Sleep(10);
        }
    }

    private static byte[] Game() => GameVault.Core.Tests.Cartridge.BankedRom.Build(0, 4, 8);

    [Fact]
    public void Stun_responses_give_the_mapped_address()
    {
        var request = StunMessage.Request(out var transaction);
        var client = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 61234);

        StunMessage.TryParseResponse(StunMessage.Response(request, client), transaction, out var mapped).Should().BeTrue();

        mapped.Should().Be(client);
        StunMessage.TryParseResponse(StunMessage.Response(request, client), new byte[12], out _).Should().BeFalse("another transaction");
        StunMessage.IsStun(request).Should().BeTrue();
        StunMessage.IsStun(new byte[] { 4, 0, 0 }).Should().BeFalse("a netplay packet");
    }

    [Fact]
    public void Internet_and_relay_codes_say_how_to_connect()
    {
        var host = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 47800);
        InviteCode.TryDecode(InviteCode.Encode(host, internet: true), out var decoded, out var kind, out _).Should().BeTrue();
        decoded.Should().Be(host);
        kind.Should().Be(InviteKind.Internet);
        InviteCode.TryDecode(InviteCode.Encode(host), out _, out kind, out _).Should().BeTrue();
        kind.Should().Be(InviteKind.Lan);

        var relay = new IPEndPoint(IPAddress.Parse("192.0.2.200"), NetplayRelay.DefaultPort);
        var code = InviteCode.EncodeRelay(relay, 0xDEADBEEF);
        code.Should().MatchRegex("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$");
        InviteCode.TryDecode(code.ToLowerInvariant(), out decoded, out kind, out var room).Should().BeTrue();
        (decoded, kind, room).Should().Be((relay, InviteKind.Relay, 0xDEADBEEFu));
    }

    [Fact]
    public async Task A_link_learns_its_public_address_from_stun()
    {
        using var link = UdpNetplayLink.Listen(0);

        var mapped = await link.DiscoverPublicEndPointAsync([StunEndPoint], TimeSpan.FromSeconds(3));

        mapped.Should().Be(new IPEndPoint(IPAddress.Loopback, link.LocalPort));
        (await link.DiscoverPublicEndPointAsync([new IPEndPoint(IPAddress.Loopback, 1)], TimeSpan.FromMilliseconds(300)))
            .Should().BeNull("nobody answers there");
    }

    [Fact]
    public async Task The_relay_pairs_two_players_by_room_and_passes_their_packets()
    {
        var relay = new IPEndPoint(IPAddress.Loopback, _relay.Port);
        using var a = UdpNetplayLink.Listen(0);
        using var b = UdpNetplayLink.Connect(relay);

        (await a.JoinRelayAsync(relay, 42, TimeSpan.FromSeconds(3))).Should().BeTrue();
        (await b.JoinRelayAsync(relay, 42, TimeSpan.FromSeconds(3))).Should().BeTrue();
        a.Send(new NetplayPacket { FirstFrame = 1, Inputs = [9], AckFrame = -1 });

        NetplayPacket received = null!;
        WaitFor(() => b.TryReceive(out received), "B hears A through the relay");
        received.Inputs.Should().Equal(9);

        using var c = UdpNetplayLink.Listen(0);
        (await c.JoinRelayAsync(relay, 42, TimeSpan.FromMilliseconds(600))).Should().BeFalse("the room is full");
    }

    [Fact]
    public async Task Hosting_over_the_internet_connects_once_the_host_has_the_reply_code()
    {
        using var hostA = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        using var hostB = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        hostA.StunServers = _ => Task.FromResult<IReadOnlyList<IPEndPoint>>([StunEndPoint]);
        hostB.StunServers = hostA.StunServers;
        hostA.Load(Game(), "Game");
        hostB.Load(Game(), "Game");

        var code = await hostA.HostInternetNetplayAsync();
        InviteCode.TryDecode(code, out _, out var kind, out _).Should().BeTrue();
        kind.Should().Be(InviteKind.Internet);
        WaitFor(() => hostA.NetplayNeedsReply, "the host waits for the reply code");

        await hostB.JoinNetplayAsync(code);
        WaitFor(() => hostB.NetplayReplyCode is not null, "the joiner gets a reply code");
        await hostA.AcceptNetplayReplyAsync(hostB.NetplayReplyCode!);

        WaitFor(() => hostA.NetplayStatus == NetplayStatus.Playing && hostB.NetplayStatus == NetplayStatus.Playing, "both play");
        hostA.NetplayNeedsReply.Should().BeFalse();
    }

    [Fact]
    public async Task Hosting_through_a_relay_needs_no_reply_code()
    {
        var settings = new InMemorySettingsStore(new AppSettings { NetplayRelayServer = $"127.0.0.1:{_relay.Port}" });
        using var hostA = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()), settings);
        using var hostB = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()));
        hostA.Load(Game(), "Game");
        hostB.Load(Game(), "Game");

        var code = await hostA.HostInternetNetplayAsync();
        InviteCode.TryDecode(code, out _, out var kind, out _).Should().BeTrue();
        kind.Should().Be(InviteKind.Relay);
        await hostB.JoinNetplayAsync(code);

        WaitFor(() => hostA.NetplayStatus == NetplayStatus.Playing && hostB.NetplayStatus == NetplayStatus.Playing, "both play through the relay");
        hostA.NetplayNeedsReply.Should().BeFalse();
    }
}
