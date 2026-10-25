using GameVault.Library.Journal;
using GameVault.Library.Practice;
using GameVault.Library.Tests.Fixtures;

namespace GameVault.Library.Tests;

public sealed class JournalAndPracticeTests : IDisposable
{
    private readonly LibraryFixture _fx = new();
    private readonly DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Notes_save_rich_text_and_attach_screenshots()
    {
        var journal = new JournalStore(Path.Combine(_fx.Root, "Journal"));
        var note = journal.CreateNote("sha1:abc", "  World 2 ", _now);
        journal.SaveNote("sha1:abc", note.Id, "World 2 secrets", @"{\rtf1 Hidden {\b exit}}", _now.AddMinutes(1));
        var shot = journal.AddImage("sha1:abc", JournalImageKind.Screenshot, [0x89, 0x50], ".png", _now.AddMinutes(2));
        var map = journal.AddImage("sha1:abc", JournalImageKind.Map, [1, 2, 3], "JPG", _now.AddMinutes(3), "Hand-drawn map");

        journal.Attach("sha1:abc", note.Id, shot.Id);
        journal.Attach("sha1:abc", note.Id, shot.Id);

        var saved = journal.Notes("sha1:abc").Single();
        saved.Title.Should().Be("World 2 secrets");
        saved.Attachments.Should().Equal(shot.Id);
        journal.LoadNote("sha1:abc", note.Id).Should().Contain(@"{\b exit}");
        journal.Images("sha1:abc").Select(i => i.Kind).Should().Equal(JournalImageKind.Map, JournalImageKind.Screenshot);
        map.Id.Should().EndWith(".jpg");
        File.ReadAllBytes(journal.ImagePath("sha1:abc", map.Id)).Should().Equal(1, 2, 3);

        journal.DeleteImage("sha1:abc", shot.Id);
        journal.Notes("sha1:abc").Single().Attachments.Should().BeEmpty();
        journal.DeleteNote("sha1:abc", note.Id);
        journal.Notes("sha1:abc").Should().BeEmpty();
    }

    [Fact]
    public void Practice_keeps_the_best_time_per_segment()
    {
        var practice = new PracticeStore(Path.Combine(_fx.Root, "Practice"));

        practice.Record("sha1:abc", 1, 600).Should().BeTrue();
        practice.Record("sha1:abc", 1, 650).Should().BeFalse();
        practice.Record("sha1:abc", 1, 590).Should().BeTrue();
        practice.Record("sha1:abc", 2, 1200).Should().BeTrue();

        practice.Bests("sha1:abc").Should().BeEquivalentTo(new Dictionary<int, long> { [1] = 590, [2] = 1200 });
        practice.Reset("sha1:abc");
        practice.Bests("sha1:abc").Should().BeEmpty();
    }
}
