using GameVault.Core;
using GameVault.Core.Input;
using GameVault.Platform.Netplay;

namespace GameVault.Platform.Tests;

/// <summary>
/// The rollback engine (SPEC §7.7) over a simulated lossy link. The real transport waits on the owner's choice
/// of relay/STUN service.
/// </summary>
public class NetplayTests
{
    /// <summary>
    /// NROM: reads both controllers into $00/$01 over and over and folds them into $02-$04, so the state
    /// depends on every input of both players.
    /// </summary>
    internal static byte[] TwoPlayerRom()
    {
        byte[] program =
        [
            0xA9, 0x01, 0x8D, 0x16, 0x40, // C000 LDA #1; STA $4016
            0xA9, 0x00, 0x8D, 0x16, 0x40, //      LDA #0; STA $4016
            0xA2, 0x08,                   //      LDX #8
            0xAD, 0x16, 0x40,             // C00C LDA $4016
            0x4A,                         //      LSR A
            0x26, 0x00,                   //      ROL $00
            0xAD, 0x17, 0x40,             //      LDA $4017
            0x4A,                         //      LSR A
            0x26, 0x01,                   //      ROL $01
            0xCA,                         //      DEX
            0xD0, 0xF1,                   //      BNE $C00C
            0xA5, 0x02, 0x18, 0x65, 0x00, 0x85, 0x02, // LDA $02; CLC; ADC $00; STA $02
            0xA5, 0x03, 0x45, 0x01, 0x2A, 0x65, 0x02, 0x85, 0x03, // LDA $03; EOR $01; ROL A; ADC $02; STA $03
            0xE6, 0x04,                   //      INC $04
            0x4C, 0x00, 0xC0,             //      JMP $C000
        ];
        var image = new byte[16 + 0x4000 + 0x2000];
        "NES\u001A"u8.CopyTo(image);
        image[4] = 1;
        image[5] = 1;
        program.CopyTo(image, 16);
        image[16 + 0x3FFC] = 0x00;
        image[16 + 0x3FFD] = 0xC0;
        return image;
    }

    /// <summary>Each player holds a random button combination for a random 1-12 frames, like real play.</summary>
    private static Func<int, ControllerState> Player(int seed)
    {
        var random = new Random(seed);
        var inputs = new List<ControllerState>();
        return frame =>
        {
            while (inputs.Count <= frame)
            {
                var buttons = (ControllerState)random.Next(256);
                var hold = random.Next(1, 13);
                for (var i = 0; i < hold; i++)
                {
                    inputs.Add(buttons);
                }
            }

            return inputs[frame];
        };
    }

    private static NesConsole NewConsole()
    {
        var console = NesConsole.FromBytes(TwoPlayerRom());
        console.Power();
        return console;
    }

    /// <summary>Runs both players to frame <paramref name="end"/>, then lets every input arrive.</summary>
    private static void Play(SimulatedNetwork network, RollbackSession a, RollbackSession b, int end, Action<int>? onTick = null)
    {
        var inputA = Player(1);
        var inputB = Player(2);
        for (var tick = 0; tick < end * 4 && (a.Frame < end || b.Frame < end || a.ConfirmedFrame < end - 1 || b.ConfirmedFrame < end - 1); tick++)
        {
            network.Tick();
            onTick?.Invoke(tick);
            if (a.Frame < end)
            {
                a.AdvanceFrame(inputA(a.Frame + a.InputDelay));
            }
            else
            {
                a.Poll();
            }

            if (b.Frame < end)
            {
                b.AdvanceFrame(inputB(b.Frame + b.InputDelay));
            }
            else
            {
                b.Poll();
            }
        }
    }

    [Theory]
    [InlineData(0, 3, 0, 0.0)]
    [InlineData(1, 4, 2, 0.15)]
    [InlineData(2, 6, 3, 0.30)]
    public void Both_players_end_in_the_same_state_as_an_offline_run(int delay, int latency, int jitter, double loss)
    {
        var network = new SimulatedNetwork(latency, jitter, loss, seed: 7);
        var consoleA = NewConsole();
        var consoleB = NewConsole();
        var a = new RollbackSession(consoleA, network.A, localPlayer: 0, delay);
        var b = new RollbackSession(consoleB, network.B, localPlayer: 1, delay);
        const int end = 300;

        Play(network, a, b, end);

        a.Frame.Should().Be(end);
        b.Frame.Should().Be(end);
        a.ConfirmedFrame.Should().Be(end - 1);
        b.ConfirmedFrame.Should().Be(end - 1);
        a.State.Should().NotBe(NetplayState.Desynced);
        consoleB.SaveSnapshot().Should().Equal(consoleA.SaveSnapshot(), "both players see the same game");

        var offline = NewConsole();
        for (var frame = 0; frame < end; frame++)
        {
            var (p1, p2) = a.ConfirmedInputs(frame);
            b.ConfirmedInputs(frame).Should().Be((p1, p2));
            offline.SetControllerState(0, p1);
            offline.SetControllerState(1, p2);
            offline.RunFrame();
        }

        offline.SaveSnapshot().Should().Equal(consoleA.SaveSnapshot(), "rollback gives the same result as playing offline");
        if (latency > delay)
        {
            (a.RolledBackFrames + b.RolledBackFrames).Should().BeGreaterThan(0, "late inputs were predicted and corrected");
        }
    }

    [Fact]
    public void A_corrupted_game_is_reported_as_a_desync()
    {
        var network = new SimulatedNetwork(latencyFrames: 2);
        var consoleA = NewConsole();
        var consoleB = NewConsole();
        var a = new RollbackSession(consoleA, network.A, 0, inputDelay: 1);
        var b = new RollbackSession(consoleB, network.B, 1, inputDelay: 1);

        Play(network, a, b, 300, onTick: tick =>
        {
            if (tick == 100)
            {
                consoleB.Bus.Write(0x0002, 0x55); // something only one side has
            }
        });

        new[] { a.State, b.State }.Should().Contain(NetplayState.Desynced);
        (a.DesyncFrame ?? b.DesyncFrame).Should().BeGreaterThan(90);
    }

    [Fact]
    public void A_different_game_is_refused()
    {
        var network = new SimulatedNetwork(latencyFrames: 1);
        var other = TwoPlayerRom();
        other[16 + 0x100] = 0xEA; // a different ROM hash
        var otherConsole = NesConsole.FromBytes(other);
        otherConsole.Power();
        var a = new RollbackSession(NewConsole(), network.A, 0, inputDelay: 1);
        var b = new RollbackSession(otherConsole, network.B, 1, inputDelay: 1);

        for (var i = 0; i < 10; i++)
        {
            network.Tick();
            a.AdvanceFrame(ControllerState.None);
            b.AdvanceFrame(ControllerState.None);
        }

        a.State.Should().Be(NetplayState.Mismatch);
        b.State.Should().Be(NetplayState.Mismatch);
        a.Frame.Should().Be(0, "nothing runs against the wrong game");
    }

    [Fact]
    public void The_game_waits_instead_of_predicting_too_far_ahead()
    {
        var network = new SimulatedNetwork(latencyFrames: 1);
        var a = new RollbackSession(NewConsole(), network.A, 0, inputDelay: 1);
        var b = new RollbackSession(NewConsole(), network.B, 1, inputDelay: 1);
        for (var i = 0; i < 20; i++)
        {
            network.Tick();
            a.AdvanceFrame(ControllerState.None);
            b.AdvanceFrame(ControllerState.None);
        }

        network.Connected = false;
        for (var i = 0; i < 30; i++)
        {
            network.Tick();
            a.AdvanceFrame(ControllerState.A);
        }

        a.State.Should().Be(NetplayState.Waiting);
        (a.Frame - a.ConfirmedFrame - 1).Should().BeLessThanOrEqualTo(RollbackSession.MaxRollbackFrames);

        network.Connected = true;
        for (var i = 0; i < 30; i++)
        {
            network.Tick();
            a.AdvanceFrame(ControllerState.A);
            b.AdvanceFrame(ControllerState.None);
        }

        a.State.Should().Be(NetplayState.Running);
    }

    [Fact]
    public void Input_delay_is_limited_to_two_frames()
    {
        var network = new SimulatedNetwork(1);
        var act = () => new RollbackSession(NewConsole(), network.A, 0, inputDelay: 3);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
