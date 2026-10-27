using System.Net;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Netplay;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>LAN netplay (SPEC §7.7; the owner chose LAN-only): wire format, invite codes and two real hosts over loopback.</summary>
public sealed class LanNetplayTests : IDisposable
{
    private readonly InputService _inputA = new(KeyboardMapping.CreateDefault());
    private readonly InputService _inputB = new(KeyboardMapping.CreateDefault());
    private readonly EmulatorHost _hostA;
    private readonly EmulatorHost _hostB;

    public LanNetplayTests()
    {
        _hostA = new EmulatorHost(new FrameSink(), _inputA);
        _hostB = new EmulatorHost(new FrameSink(), _inputB);
    }

    public void Dispose()
    {
        _hostA.Dispose();
        _hostB.Dispose();
    }

    private static byte[] Game(byte variant = 0)
    {
        var rom = GameVault.Core.Tests.Cartridge.BankedRom.Build(0, 4, 8);
        rom[16 + 0x100] = variant;
        return rom;
    }

    private static Func<string>? Describe;

    private static void WaitFor(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(Describe?.Invoke());
            }

            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Packets_survive_the_wire_format()
    {
        var packet = new NetplayPacket
        {
            Hello = new HelloMessage(0x0123456789ABCDEF, 1, RollbackSession.ProtocolVersion),
            HelloSeen = true,
            FirstFrame = 1234,
            Inputs = [1, 2, 3, 0xFF, 0xB050_0880, 0x0003_80F0_B050_0880], // an N64 stick in bits 16-31; a DualShock 2 right stick and L3/R3 above
            AckFrame = 1200,
            Checksum = new FrameChecksum(1170, 0xDEADBEEF),
        };

        NetplayCodec.TryDecode(NetplayCodec.Encode(packet), out var decoded).Should().BeTrue();
        decoded.Should().BeEquivalentTo(packet);

        NetplayCodec.TryDecode(NetplayCodec.Encode(new NetplayPacket { FirstFrame = 5, Inputs = [], AckFrame = -1 }), out var bare).Should().BeTrue();
        bare.Hello.Should().BeNull();
        bare.Checksum.Should().BeNull();

        NetplayCodec.TryDecode([9, 0, 0], out _).Should().BeFalse("another version");
        NetplayCodec.Encode(packet)[0].Should().Be(4, "version 4 carries all 64 bits");
        NetplayCodec.TryDecode([3, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF], out _).Should().BeFalse("version 3 peers can't join");
        RollbackSession.ProtocolVersion.Should().Be(4);
        NetplayCodec.TryDecode(NetplayCodec.Encode(packet).AsSpan(0, 10), out _).Should().BeFalse("cut short");
    }

    [Theory]
    [InlineData("192.168.1.42", 47800)]
    [InlineData("10.0.0.7", 1)]
    [InlineData("255.255.255.255", 65535)]
    public void Invite_codes_round_trip(string address, int port)
    {
        var endPoint = new IPEndPoint(IPAddress.Parse(address), port);
        var code = InviteCode.Encode(endPoint);

        code.Should().MatchRegex("^[0-9A-Z]{5}-[0-9A-Z]{5}$");
        InviteCode.TryDecode(code, out var decoded).Should().BeTrue();
        decoded.Should().Be(endPoint);
        InviteCode.TryDecode(" " + code.ToLowerInvariant().Replace("-", " ") + " ", out var forgiving).Should().BeTrue();
        forgiving.Should().Be(endPoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDE")]
    [InlineData("ABCDE-FGHJ!")]
    [InlineData("ZZZZZ-ZZZZZ")]
    public void Bad_invite_codes_are_rejected(string code) => InviteCode.TryDecode(code, out _).Should().BeFalse();

    [Fact]
    public void Udp_links_find_each_other_on_loopback()
    {
        using var host = UdpNetplayLink.Listen(0);
        using var guest = UdpNetplayLink.Connect(new IPEndPoint(IPAddress.Loopback, host.LocalPort));

        guest.Send(new NetplayPacket { FirstFrame = 1, Inputs = [7], AckFrame = -1 });
        NetplayPacket received = null!;
        WaitFor(() => host.TryReceive(out received));
        received.Inputs.Should().Equal(7);
        host.Peer.Should().NotBeNull();

        host.Send(new NetplayPacket { FirstFrame = 2, Inputs = [8], AckFrame = 1 });
        WaitFor(() => guest.TryReceive(out received));
        received.Inputs.Should().Equal(8);
    }

    [Fact]
    public async Task Two_players_on_one_network_play_the_same_game()
    {
        _hostA.Load(Game(), "Game");
        _hostB.Load(Game(), "Game");
        Describe = () => $"A {_hostA.NetplayStatus} {_hostA.FrameCount} B {_hostB.NetplayStatus} {_hostB.FrameCount}";

        var code = await _hostA.HostNetplayAsync(IPAddress.Loopback);
        WaitFor(() => _hostA.NetplayStatus == NetplayStatus.WaitingForPlayer);
        _hostA.NetplayInviteCode.Should().Be(code);

        await _hostB.JoinNetplayAsync(code);
        WaitFor(() => _hostA.NetplayStatus == NetplayStatus.Playing && _hostB.NetplayStatus == NetplayStatus.Playing);

        var startA = _hostA.FrameCount;
        var startB = _hostB.FrameCount;
        _inputA.KeyDown(VirtualKeys.Right);
        _inputB.KeyDown(VirtualKeys.X);
        WaitFor(() => _hostA.FrameCount > startA + 90 && _hostB.FrameCount > startB + 90);

        _hostA.NetplayStatus.Should().NotBe(NetplayStatus.OutOfSync);
        _hostB.NetplayStatus.Should().NotBe(NetplayStatus.OutOfSync);
        (await _hostA.RestoreStateAsync((await _hostA.CaptureStateAsync())!)).Should().BeFalse("states can't be loaded during netplay");

        _hostB.LeaveNetplay();
        WaitFor(() => _hostB.NetplayStatus == NetplayStatus.Off);
        WaitFor(() => _hostA.NetplayStatus == NetplayStatus.ConnectionLost);
    }

    [Fact]
    public async Task Disk_system_games_play_and_change_sides_on_both_machines()
    {
        var disk = GameVault.Core.Tests.Fds.FdsTestImages.Image(2, header: false);
        var bios = GameVault.Core.Tests.Fds.FdsTestImages.StubBios();
        _hostA.Load(disk, "Disk game", bios);
        _hostB.Load(disk, "Disk game", bios);
        Describe = () => $"A {_hostA.NetplayStatus} side {_hostA.InsertedDiskSide} B {_hostB.NetplayStatus} side {_hostB.InsertedDiskSide}";

        var code = await _hostA.HostNetplayAsync(IPAddress.Loopback);
        await _hostB.JoinNetplayAsync(code);
        WaitFor(() => _hostA.NetplayStatus == NetplayStatus.Playing && _hostB.NetplayStatus == NetplayStatus.Playing);

        _hostB.InsertDiskSide(1); // player 2 turns the disk over: it travels with their input
        WaitFor(() => _hostA.InsertedDiskSide == 1 && _hostB.InsertedDiskSide == 1);
        var frame = _hostA.FrameCount;
        WaitFor(() => _hostA.FrameCount > frame + 60);
        _hostA.NetplayStatus.Should().NotBe(NetplayStatus.OutOfSync);
        _hostB.NetplayStatus.Should().NotBe(NetplayStatus.OutOfSync);
    }

    [Fact]
    public async Task A_different_game_is_refused()
    {
        _hostA.Load(Game(), "Game");
        _hostB.Load(Game(variant: 1), "Other game");

        var code = await _hostA.HostNetplayAsync(IPAddress.Loopback);
        await _hostB.JoinNetplayAsync(code);

        WaitFor(() => _hostB.NetplayStatus == NetplayStatus.DifferentGame);
        WaitFor(() => _hostA.NetplayStatus == NetplayStatus.DifferentGame);
    }

    [Fact]
    public async Task Netplay_needs_a_running_cartridge_game_and_a_valid_code()
    {
        var notRunning = () => _hostA.HostNetplayAsync(IPAddress.Loopback);
        await notRunning.Should().ThrowAsync<NetplayException>().WithMessage("Start the game first*");

        _hostA.Load(Game(), "Game");
        var badCode = () => _hostA.JoinNetplayAsync("NOPE");
        await badCode.Should().ThrowAsync<NetplayException>().WithMessage("That invite code isn't right*");
    }
}
