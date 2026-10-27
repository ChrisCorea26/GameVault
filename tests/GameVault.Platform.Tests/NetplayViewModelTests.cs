using GameVault.Platform.Emulation;
using GameVault.Platform.Video;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>The Player's Netplay flyout (SPEC §7.7), against the sample host used by <c>--sample-data</c>.</summary>
public sealed class NetplayViewModelTests : IDisposable
{
    private readonly SampleEmulatorHost _host = new(new FrameSink());
    private readonly InMemoryClipboard _clipboard = new();
    private readonly NetplayViewModel _vm;

    public NetplayViewModelTests()
    {
        _vm = new NetplayViewModel(_host, _clipboard);
        _vm.Attach();
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Hosting_shows_a_code_to_share_and_copies_it()
    {
        _vm.IsIdle.Should().BeTrue();

        await _vm.HostCommand.ExecuteAsync(null);

        _vm.Status.Should().Be(NetplayStatus.WaitingForPlayer);
        _vm.HasInviteCode.Should().BeTrue();
        _vm.StatusText.Should().StartWith("Waiting for player 2");
        _vm.CopyCodeCommand.Execute(null);
        _clipboard.Text.Should().Be(_vm.InviteCode);

        _vm.LeaveCommand.Execute(null);
        _vm.IsActive.Should().BeFalse();
        _vm.HasInviteCode.Should().BeFalse();
    }

    [Fact]
    public async Task Joining_needs_a_code_and_says_which_player_you_are()
    {
        _vm.JoinCommand.CanExecute(null).Should().BeFalse();
        _vm.JoinCode = "K7Q2M-9XD4P";

        await _vm.JoinCommand.ExecuteAsync(null);

        _vm.Status.Should().Be(NetplayStatus.Playing);
        _vm.StatusText.Should().Be("Connected. You are Player 2.");
    }

    [Fact]
    public async Task Hosting_over_the_internet_asks_for_the_reply_code()
    {
        await _vm.HostInternetCommand.ExecuteAsync(null);

        _vm.NeedsReply.Should().BeTrue();
        _vm.StatusText.Should().Contain("reply code");
        _vm.AcceptReplyCommand.CanExecute(null).Should().BeFalse();
        _vm.ReplyCode = "ABCDE-12345";

        await _vm.AcceptReplyCommand.ExecuteAsync(null);

        _vm.NeedsReply.Should().BeFalse();
        _vm.Status.Should().Be(NetplayStatus.Playing);
        _vm.ReplyCode.Should().BeEmpty();
    }
}
