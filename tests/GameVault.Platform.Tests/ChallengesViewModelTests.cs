using GameVault.Library.Challenges;
using GameVault.Platform.Emulation;
using GameVault.Platform.Video;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>The Player's Challenges flyout (SPEC §7.9) over the sample emulator.</summary>
public sealed class ChallengesViewModelTests
{
    /// <summary>The sample host raises its events directly, not through xUnit's context (see <see cref="NoSynchronizationContext"/>).</summary>
    private static SampleEmulatorHost HostWithoutContext()
    {
        using (NoSynchronizationContext.Enter())
        {
            return new SampleEmulatorHost(new FrameSink());
        }
    }

    [Fact]
    public async Task Making_a_challenge_saves_its_file()
    {
        using var host = HostWithoutContext();
        await host.LoadAsync("sample.nes");
        var files = new InMemoryChallengeFiles();
        var vm = new ChallengesViewModel(host, files);
        vm.Attach();

        Assert.True(vm.CanMakeChallenges);
        vm.Name = "Crystal dash";
        vm.GoalValueIndex = 0;
        vm.OpIndex = 5;
        vm.Target = "3";
        vm.TimeLimitSeconds = "30";
        await vm.MakeCommand.ExecuteAsync(null);

        var (kind, name, content) = Assert.Single(files.Saved);
        Assert.Equal(ChallengeFileKind.Challenge, kind);
        Assert.Equal("Crystal dash", name);
        var saved = ChallengeFiles.LoadChallenge(content);
        Assert.Equal("crystals ≥ 3", saved.DescribeGoal());
        Assert.Equal(1803, saved.TimeLimitFrames);
    }

    [Fact]
    public async Task Bad_numbers_are_explained_and_nothing_is_saved()
    {
        using var host = HostWithoutContext();
        await host.LoadAsync("sample.nes");
        var files = new InMemoryChallengeFiles();
        var vm = new ChallengesViewModel(host, files) { Target = "lots" };

        await vm.MakeCommand.ExecuteAsync(null);
        Assert.Contains("whole number", vm.Message);

        vm.Target = "1";
        vm.TimeLimitSeconds = "-4";
        await vm.MakeCommand.ExecuteAsync(null);
        Assert.Contains("seconds", vm.Message);
        Assert.Empty(files.Saved);
    }

    [Fact]
    public async Task Taking_a_challenge_then_saving_and_checking_the_result()
    {
        using var host = HostWithoutContext();
        await host.LoadAsync("sample.nes");
        var files = new InMemoryChallengeFiles { ToOpen = { [ChallengeFileKind.Challenge] = ChallengeFiles.Save(SampleEmulatorHost.SampleChallenge) } };
        var vm = new ChallengesViewModel(host, files);
        vm.Attach();

        await vm.OpenCommand.ExecuteAsync(null);
        Assert.True(vm.HasChallenge);
        Assert.StartsWith("Crystal sprint: crystals ≥ 3, within 1:00.00", vm.ChallengeSummary);

        var finished = new TaskCompletionSource();
        host.ChallengeFinished += (_, _) => finished.TrySetResult();
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsAttempting);
        Assert.False(vm.StartCommand.CanExecute(null));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(vm.IsAttempting);
        Assert.True(vm.HasResult);
        Assert.Contains("Done in 0:35.84", vm.Message);

        await vm.SaveResultCommand.ExecuteAsync(null);
        var result = files.Saved.Single(f => f.Kind == ChallengeFileKind.Result);
        files.ToOpen[ChallengeFileKind.Result] = result.Content;
        await vm.CheckResultCommand.ExecuteAsync(null);
        Assert.StartsWith("Checked:", vm.Message);
        vm.Detach();
    }

    [Fact]
    public async Task Giving_up_ends_the_attempt_without_a_result()
    {
        using var host = HostWithoutContext();
        await host.LoadAsync("sample.nes");
        var files = new InMemoryChallengeFiles { ToOpen = { [ChallengeFileKind.Challenge] = ChallengeFiles.Save(SampleEmulatorHost.SampleChallenge) } };
        var vm = new ChallengesViewModel(host, files);
        vm.Attach();

        await vm.OpenCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);
        var finished = new TaskCompletionSource();
        host.ChallengeFinished += (_, _) => finished.TrySetResult();
        vm.GiveUpCommand.Execute(null);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(vm.IsAttempting);
        Assert.False(vm.HasResult);
        Assert.Equal("Attempt stopped.", vm.Message);
    }

    [Fact]
    public async Task A_file_that_isnt_a_challenge_is_explained()
    {
        using var host = HostWithoutContext();
        var files = new InMemoryChallengeFiles { ToOpen = { [ChallengeFileKind.Challenge] = "not json" } };
        var vm = new ChallengesViewModel(host, files);

        await vm.OpenCommand.ExecuteAsync(null);
        Assert.False(vm.HasChallenge);
        Assert.Equal("That file isn't a GameVault challenge.", vm.Message);
    }
}
