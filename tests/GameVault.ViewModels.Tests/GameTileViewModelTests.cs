using GameVault.Library.Models;

namespace GameVault.ViewModels.Tests;

public class GameTileViewModelTests
{
    private static GameTileViewModel Tile(string title = "Crystal Caverns", Action<GameEntryBuilder>? configure = null)
    {
        var builder = new GameEntryBuilder(title);
        configure?.Invoke(builder);
        return new GameTileViewModel(builder.Build());
    }

    [Theory]
    [InlineData("Crystal Caverns", "CC")]
    [InlineData("Knights of Ember Hall", "KO")]
    [InlineData("Excitebox", "E")]
    [InlineData("  ", "?")]
    public void Initials_use_the_first_two_words(string title, string expected)
    {
        Tile(title).Initials.Should().Be(expected);
    }

    [Fact]
    public void Subtitle_skips_missing_fields()
    {
        Tile(configure: b => b.Entry = b.Entry with { ReleaseYear = 1988, Publisher = null, Genre = "Platformer" })
            .Subtitle.Should().Be("1988 · Platformer");
    }

    [Theory]
    [InlineData(PlayStatus.Unplayed, "Unplayed")]
    [InlineData(PlayStatus.Playing, "Playing")]
    [InlineData(PlayStatus.Beaten, "Beaten")]
    [InlineData(PlayStatus.Completed, "100%")]
    public void Status_text_is_plain_language(PlayStatus status, string expected)
    {
        Tile(configure: b => b.Entry = b.Entry with { Status = status }).StatusText.Should().Be(expected);
    }

    [Fact]
    public void Playtime_text_is_empty_until_played_then_minutes_then_hours()
    {
        Tile().PlaytimeText.Should().BeEmpty();
        Tile(configure: b => b.Entry = b.Entry with { Playtime = TimeSpan.FromMinutes(42) })
            .PlaytimeText.Should().Be("42 min played");
        Tile(configure: b => b.Entry = b.Entry with { Playtime = TimeSpan.FromHours(48) })
            .PlaytimeText.Should().Be("48 h played");
    }

    public sealed class GameEntryBuilder(string title)
    {
        public GameEntry Entry { get; set; } = new() { Id = Guid.NewGuid(), Title = title };

        public GameEntry Build() => Entry;
    }
}
