using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;

namespace GameVault.ViewModels.Tests;

/// <summary>"Previously on…" (SPEC §7.9, Phase 9).</summary>
public class RecapViewModelTests
{
    private const string Game = "sample:2"; // the sample journal keeps notes for this game
    private readonly InMemorySessionLog _sessions = new() { SampleHistory = false };
    private readonly SampleJournalService _journal = new();
    private readonly SamplePasswordService _passwords = new();
    private readonly SampleTimelineService _timeline = new();

    private RecapViewModel Recap() => new(_sessions, _journal, _passwords, _timeline);

    [Fact]
    public async Task Nothing_shows_for_a_new_game_or_a_short_break()
    {
        var recap = Recap();
        await recap.ShowAsync(Game, "Star Courier", DateTimeOffset.Now);
        recap.IsVisible.Should().BeFalse("never played");

        await _sessions.RecordAsync(Game, new PlaySession(DateTimeOffset.Now.AddHours(-5), TimeSpan.FromMinutes(30)));
        await recap.ShowAsync(Game, "Star Courier", DateTimeOffset.Now);
        recap.IsVisible.Should().BeFalse("less than a day");
    }

    [Fact]
    public async Task After_a_break_it_recalls_the_last_session()
    {
        await _sessions.RecordAsync(Game, new PlaySession(DateTimeOffset.Now.AddDays(-3), TimeSpan.FromMinutes(42)));
        await _passwords.AddAsync(Game, "QUEST GAM", null);
        var note = await _journal.CreateNoteAsync(Game, "Where the key is");
        var recap = Recap();

        await recap.ShowAsync(Game, "Star Courier", DateTimeOffset.Now);

        recap.IsVisible.Should().BeTrue();
        recap.Heading.Should().Be("Previously on Star Courier…");
        recap.LastSession.Should().Be("Last played 3 days ago, for 42 minutes.");
        recap.Password.Should().Be("QUEST GAM");
        recap.Note.Should().StartWith("Your newest note: Where the key is");

        recap.DismissCommand.Execute(null);
        recap.IsVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(1.5, "yesterday")]
    [InlineData(20, "2 weeks ago")]
    [InlineData(400, "13 months ago")]
    [InlineData(1000, "2 years ago")]
    public void Breaks_are_said_plainly(double days, string text) => RecapViewModel.Ago(TimeSpan.FromDays(days)).Should().Be(text);

    [Theory]
    [InlineData(0.5, "less than a minute")]
    [InlineData(75, "an hour and 15 minutes")]
    [InlineData(185, "3 hours and 5 minutes")]
    public void Lengths_are_said_plainly(double minutes, string text) => RecapViewModel.Length(TimeSpan.FromMinutes(minutes)).Should().Be(text);
}
