using GameVault.Library.Challenges;
using GameVault.Library.Memory;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>
/// Verified challenges (SPEC §7.9). The test ROM reads both controllers into $00/$01 continuously, so a goal of
/// "$00 = $80" means "player 1 is holding A (only)".
/// </summary>
public sealed class ChallengeTests : IDisposable
{
    private readonly InputService _input = new(KeyboardMapping.CreateDefault());
    private readonly EmulatorHost _host;

    public ChallengeTests() => _host = new EmulatorHost(new FrameSink(), _input);

    public void Dispose() => _host.Dispose();

    private static IReadOnlyList<MemoryCondition> HoldingA => [MemoryCondition.Raw(0x0000, "==", 0x80)];

    private async Task<ChallengeOutcome> Attempt(ChallengeFile challenge, bool pressA)
    {
        var finished = new TaskCompletionSource<ChallengeOutcome>();
        _host.ChallengeFinished += (_, outcome) => finished.TrySetResult(outcome);
        await _host.StartChallengeAsync(challenge);
        _host.IsChallengeActive.Should().BeTrue();
        if (pressA)
        {
            await Task.Delay(300);
            _input.KeyDown(VirtualKeys.X); // A
        }

        var outcome = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        _input.KeyUp(VirtualKeys.X);
        return outcome;
    }

    [Fact]
    public async Task A_finished_attempt_verifies_and_a_tampered_one_does_not()
    {
        _host.Load(NetplayTests.TwoPlayerRom(), "Two players");
        var challenge = await _host.CreateChallengeAsync("Press A", HoldingA, timeLimitFrames: 3600, note: "Just press A");
        challenge.DescribeGoal().Should().Be("RAM $0000 = 128");

        // Round-trip through the file format, as when the challenge is shared.
        challenge = ChallengeFiles.LoadChallenge(ChallengeFiles.Save(challenge));
        var outcome = await Attempt(challenge, pressA: true);

        outcome.Kind.Should().Be(ChallengeOutcomeKind.Done);
        var result = ChallengeFiles.LoadResult(ChallengeFiles.Save(outcome.Result!));
        result.Frames.Should().Be(outcome.Frames).And.BeGreaterThan(5);
        _host.IsChallengeActive.Should().BeFalse();

        (await _host.VerifyChallengeAsync(challenge, result)).Verified.Should().BeTrue();

        var inputs = InputRecording.Decode(Convert.FromBase64String(result.Inputs));
        inputs[^1] = (0, 0); // claim the goal was reached without pressing A
        var forged = result with { Inputs = Convert.ToBase64String(InputRecording.Encode(inputs)) };
        (await _host.VerifyChallengeAsync(challenge, forged)).Verified.Should().BeFalse();

        (await _host.VerifyChallengeAsync(challenge, result with { Frames = result.Frames - 1 })).Verified.Should().BeFalse();
        (await _host.VerifyChallengeAsync(challenge with { Id = "other" }, result)).Message.Should().Contain("different challenge");
        (await _host.VerifyChallengeAsync(challenge with { TimeLimitFrames = 2 }, result)).Message.Should().Contain("time limit");
    }

    [Fact]
    public async Task Time_runs_out_and_states_cant_be_loaded_during_an_attempt()
    {
        _host.Load(NetplayTests.TwoPlayerRom(), "Two players");
        var start = (await _host.CaptureStateAsync())!;
        var challenge = await _host.CreateChallengeAsync("Too slow", HoldingA, timeLimitFrames: 30, note: null);

        var finished = new TaskCompletionSource<ChallengeOutcome>();
        _host.ChallengeFinished += (_, outcome) => finished.TrySetResult(outcome);
        await _host.StartChallengeAsync(challenge);
        (await _host.RestoreStateAsync(start)).Should().BeFalse("no loading states mid-attempt");

        var outcome = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
        outcome.Kind.Should().Be(ChallengeOutcomeKind.TimeUp);
        outcome.Frames.Should().Be(30);
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public async Task Challenges_only_run_on_their_own_game()
    {
        _host.Load(NetplayTests.TwoPlayerRom(), "Two players");
        var challenge = await _host.CreateChallengeAsync("Press A", HoldingA, null, null);

        _host.Load(GameWatcherTests.LivesAndRoomsRom(), "Another game");
        var act = () => _host.StartChallengeAsync(challenge);
        await act.Should().ThrowAsync<ChallengeException>().WithMessage("Open*first*");

        var noGoal = () => _host.CreateChallengeAsync("Nothing", [], null, null);
        await noGoal.Should().ThrowAsync<ChallengeException>().WithMessage("Choose a goal*");
    }

    [Fact]
    public void Recordings_compress_held_buttons()
    {
        var frames = Enumerable.Repeat(((byte)0x80, (byte)0), 1000).Concat(Enumerable.Repeat(((byte)0, (byte)1), 300)).ToList();

        var encoded = InputRecording.Encode(frames);

        encoded.Length.Should().BeLessThan(10);
        InputRecording.Decode(encoded).Should().Equal(frames);
        var damaged = () => InputRecording.Decode([0x80]);
        damaged.Should().Throw<FormatException>();
        ChallengeVerifier.Time(3606).Should().Be("1:00.00");
    }
}
