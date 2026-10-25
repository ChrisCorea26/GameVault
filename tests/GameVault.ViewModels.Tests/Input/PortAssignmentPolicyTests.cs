using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels.Tests.Input;

/// <summary>docs/design/input.md §12: assignment policy.</summary>
public class PortAssignmentPolicyTests
{
    private readonly PortAssignmentPolicy _policy = new();

    private static ControllerInfo Pad(int id, string? key = null, string? name = null) =>
        new(id, key ?? $"guid-{id}", name ?? $"Pad {id}");

    [Fact]
    public void Pads_fill_ports_in_connection_order()
    {
        _policy.Connect(Pad(10)).Should().Be("Pad 10 is now Player 1");
        _policy.Connect(Pad(11)).Should().Be("Pad 11 is now Player 2");

        _policy.PortOf(10).Should().Be(0, "the first pad joins the keyboard on Player 1");
        _policy.PortOf(11).Should().Be(1);
    }

    [Fact]
    public void A_third_pad_stays_unassigned()
    {
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));

        _policy.Connect(Pad(3)).Should().BeNull();

        _policy.PortOf(3).Should().BeNull();
        _policy.Unassigned.Select(c => c.InstanceId).Should().Equal(3);
    }

    [Fact]
    public void A_freed_port_goes_to_the_next_pad_that_connects()
    {
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));
        _policy.Disconnect(1);

        _policy.Connect(Pad(3));

        _policy.PortOf(3).Should().Be(0, "outside play, a disconnect frees the port");
    }

    [Fact]
    public void Pinned_pads_take_their_port_and_push_unpinned_pads_along()
    {
        _policy.Pin("guid-7", 0);
        _policy.Connect(Pad(1));

        _policy.Connect(Pad(7)).Should().Be("Pad 7 is now Player 1");

        _policy.PortOf(7).Should().Be(0);
        _policy.PortOf(1).Should().Be(1);
    }

    [Fact]
    public void A_pin_does_not_displace_another_pinned_pad()
    {
        _policy.Pin("guid-1", 0);
        _policy.Pin("guid-2", 0);
        _policy.Connect(Pad(1));

        _policy.Connect(Pad(2));

        _policy.PortOf(1).Should().Be(0);
        _policy.PortOf(2).Should().Be(1);
    }

    [Fact]
    public void During_play_a_disconnect_reserves_the_port_for_that_pad()
    {
        _policy.GameRunning = true;
        _policy.Connect(Pad(1, name: "Xbox Wireless Controller"));
        _policy.Connect(Pad(2));

        _policy.Disconnect(1).Should().Be(0);
        _policy.WaitingFor(0).Should().Be("Xbox Wireless Controller");

        _policy.Connect(Pad(3)).Should().BeNull("Player 1 is reserved and Player 2 is taken");

        // The same physical pad (same key, new instance id) gets its port back.
        _policy.Connect(Pad(9, key: "guid-1")).Should().Be("Pad 9 is now Player 1");
        _policy.WaitingFor(0).Should().BeNull();
    }

    [Fact]
    public void An_unassigned_pad_can_take_over_a_waiting_port()
    {
        _policy.GameRunning = true;
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));
        _policy.Connect(Pad(3));
        _policy.Disconnect(2);

        _policy.TryTakeOver(3).Should().Be("Pad 3 is now Player 2");

        _policy.PortOf(3).Should().Be(1);
        _policy.WaitingFor(1).Should().BeNull();
        _policy.TryTakeOver(3).Should().BeNull("it is assigned now");
    }

    [Fact]
    public void Take_over_does_nothing_when_no_port_is_waiting()
    {
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));
        _policy.Connect(Pad(3));

        _policy.TryTakeOver(3).Should().BeNull();
    }

    [Fact]
    public void Closing_the_game_clears_reservations()
    {
        _policy.GameRunning = true;
        _policy.Connect(Pad(1));
        _policy.Disconnect(1);

        _policy.GameRunning = false;
        _policy.ClearReservations();
        _policy.Connect(Pad(5));

        _policy.PortOf(5).Should().Be(0);
    }

    [Fact]
    public void Swap_exchanges_players()
    {
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));

        _policy.Swap(0, 1);

        _policy.PortOf(1).Should().Be(1);
        _policy.PortOf(2).Should().Be(0);
    }

    [Fact]
    public void Use_for_player_moves_a_pad_and_unassigns_the_previous_one()
    {
        _policy.Connect(Pad(1));
        _policy.Connect(Pad(2));
        _policy.Connect(Pad(3));

        _policy.AssignToPort(3, 1).Should().Be("Pad 3 is now Player 2");

        _policy.PortOf(3).Should().Be(1);
        _policy.PortOf(2).Should().BeNull();
        _policy.Unassigned.Select(c => c.InstanceId).Should().Equal(2);
    }

    [Fact]
    public void Pins_load_from_settings_and_ignore_invalid_ports()
    {
        _policy.LoadPins([new("a", 1), new("b", 7)]);

        _policy.IsPinned("a").Should().BeTrue();
        _policy.IsPinned("b").Should().BeFalse();
    }

    [Fact]
    public void Four_score_games_give_waiting_pads_players_3_and_4_and_take_them_back_after()
    {
        var policy = new PortAssignmentPolicy();
        var pads = Enumerable.Range(1, 4).Select(i => new ControllerInfo(i, $"key{i}", $"Pad {i}")).ToList();
        foreach (var pad in pads)
        {
            policy.Connect(pad);
        }

        policy.Unassigned.Should().HaveCount(2, "two ports in normal games");

        var notices = policy.SetPortCount(4);

        notices.Should().Equal("Pad 3 is now Player 3", "Pad 4 is now Player 4");
        policy.PortOf(4).Should().Be(3);

        policy.SetPortCount(2);
        policy.PortOf(3).Should().BeNull();
        policy.Unassigned.Should().HaveCount(2);
    }
}
