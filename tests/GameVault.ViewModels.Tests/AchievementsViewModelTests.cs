using FluentAssertions;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

/// <summary>RetroAchievements in the Player and Settings (SPEC §7.10).</summary>
public sealed class AchievementsViewModelTests
{
    private static readonly AchievementInfo Unlock = new(3, "Bat wrangler", "Defeat 25 bats", 10, "https://media.retroachievements.org/Badge/1.png", true);

    [Fact]
    public async Task Unlocks_show_one_notification_at_a_time()
    {
        var service = new SampleAchievementsService();
        var vm = new AchievementsViewModel(service);
        vm.Attach();
        vm.HasGame.Should().BeTrue();
        vm.Summary.Should().Be("2 of 5 achievements · Hardcore");

        service.Raise(new AchievementsEvent(AchievementsEventKind.AchievementUnlocked, Unlock));
        service.Raise(new AchievementsEvent(AchievementsEventKind.LeaderboardStarted, Leaderboard: new LeaderboardInfo(9, "Speedrun", "Fastest to the exit")));

        vm.HasNotification.Should().BeTrue();
        vm.NotificationHeading.Should().Be("Achievement unlocked");
        vm.NotificationTitle.Should().Be("Bat wrangler");
        vm.NotificationText.Should().Be("Defeat 25 bats (10 points)");
        vm.NotificationBadgeUri.Should().Be(new Uri("https://media.retroachievements.org/Badge/1.png"));
        vm.Summary.Should().Be("3 of 5 achievements · Hardcore");

        vm.DismissNotificationCommand.Execute(null);
        vm.HasNotification.Should().BeFalse();
        vm.Detach();
        await Task.CompletedTask;
    }

    [Fact]
    public void Trackers_and_challenge_badges_come_and_go()
    {
        var service = new SampleAchievementsService();
        var vm = new AchievementsViewModel(service);
        vm.Attach();

        service.Raise(new AchievementsEvent(AchievementsEventKind.TrackerShow, TrackerId: 7, TrackerText: "0:00.00"));
        service.Raise(new AchievementsEvent(AchievementsEventKind.TrackerUpdate, TrackerId: 7, TrackerText: "0:12.34"));
        vm.Trackers.Should().ContainSingle().Which.Text.Should().Be("0:12.34");
        service.Raise(new AchievementsEvent(AchievementsEventKind.TrackerHide, TrackerId: 7));
        vm.Trackers.Should().BeEmpty();

        service.Raise(new AchievementsEvent(AchievementsEventKind.ChallengeIndicatorShow, Unlock));
        service.Raise(new AchievementsEvent(AchievementsEventKind.ChallengeIndicatorShow, Unlock));
        vm.Challenges.Should().ContainSingle();
        service.Raise(new AchievementsEvent(AchievementsEventKind.ChallengeIndicatorHide, Unlock));
        vm.Challenges.Should().BeEmpty();

        vm.RefreshCommand.Execute(null);
        vm.Groups.Select(g => g.Label).Should().Equal("Almost there", "Locked", "Unlocked");
        vm.RichPresence.Should().Be("Exploring the Crystal Caverns, 3 lives");
    }

    [Fact]
    public void Without_a_recognised_game_the_button_hides()
    {
        var vm = new AchievementsViewModel(new SampleAchievementsService { GameRunning = false });
        vm.Attach();
        vm.HasGame.Should().BeFalse();
        vm.Summary.Should().BeEmpty();
    }

    [Fact]
    public async Task Settings_sign_in_reports_a_wrong_password_and_hardcore_is_saved()
    {
        var service = new SampleAchievementsService(signedIn: false);
        var vm = new RetroAchievementsSettingsViewModel(service);
        vm.Attach();
        vm.IsSignedOut.Should().BeTrue();
        vm.SignInCommand.CanExecute(null).Should().BeFalse();

        vm.UserName = "Player";
        vm.Password = "wrong";
        await vm.SignInCommand.ExecuteAsync(null);
        vm.Message.Should().Be("RetroAchievements didn't accept that username and password.");
        vm.IsSignedIn.Should().BeFalse();

        vm.Password = "right";
        await vm.SignInCommand.ExecuteAsync(null);
        vm.IsSignedIn.Should().BeTrue();
        vm.SignedInText.Should().StartWith("Signed in as Player");
        vm.Password.Should().BeEmpty("the password isn't kept");

        vm.Hardcore.Should().BeTrue();
        vm.Hardcore = false;
        service.Hardcore.Should().BeFalse();
        vm.Message.Should().StartWith("Hardcore is off");

        vm.SignOutCommand.Execute(null);
        vm.IsSignedOut.Should().BeTrue();
    }
}
