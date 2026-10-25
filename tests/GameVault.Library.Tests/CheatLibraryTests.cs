using GameVault.Library.Cheats;

namespace GameVault.Library.Tests;

/// <summary>The player's code lists, import/export and bundled suggestions (SPEC §7.8, Phase 8).</summary>
public sealed class CheatLibraryTests : IDisposable
{
    private const string Game = "sha1:0123456789abcdef0123456789abcdef01234567";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gamevault-cheats-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void Codes_are_saved_per_game_normalized_and_toggleable()
    {
        var store = new CheatStore(_root.FullName);
        var lives = store.Add(Game, "sxio-po", " Infinite lives ");
        store.Add(Game, "075a:09", "Nine lives");

        var reloaded = new CheatStore(_root.FullName).List(Game);
        reloaded.Select(c => c.Code).Should().Equal("SXIOPO", "075A:09");
        reloaded[0].Description.Should().Be("Infinite lives");
        reloaded.Should().OnlyContain(c => c.Enabled);

        store.Update(Game, lives with { Enabled = false });
        store.List(Game)[0].Enabled.Should().BeFalse();

        store.SetAllEnabled(Game, false);
        store.List(Game).Should().OnlyContain(c => !c.Enabled);

        store.Delete(Game, lives.Id);
        store.List(Game).Should().ContainSingle();
        store.List("sha1:other").Should().BeEmpty();

        var bad = () => store.Add(Game, "NOTACODE", "");
        bad.Should().Throw<FormatException>();
    }

    [Fact]
    public void Plain_text_lists_import_and_export()
    {
        const string text = """
            # My codes
            SXIOPO  Infinite lives

            075A:09 Nine lives
            AEKPIL
            """;

        var import = CheatFiles.Import(text, "codes.txt");

        import.Succeeded.Should().BeTrue();
        import.Codes.Should().Equal(("SXIOPO", "Infinite lives", true), ("075A:09", "Nine lives", true), ("AEKPIL", "", true));

        var store = new CheatStore(_root.FullName);
        store.AddRange(Game, import.Codes).Should().Be(3);
        store.AddRange(Game, import.Codes).Should().Be(0, "codes already in the list are skipped");

        var exported = CheatFiles.Export(store.List(Game));
        CheatFiles.Import(exported, "again.txt").Codes.Should().Equal(import.Codes);
    }

    [Fact]
    public void A_bad_line_rejects_the_whole_file_with_its_line_number()
    {
        var import = CheatFiles.Import("SXIOPO lives\nNOPE\nSXIOPB bad letter\n", "codes.txt");

        import.Succeeded.Should().BeFalse();
        import.Codes.Should().BeEmpty("nothing is half-imported");
        import.Errors.Should().HaveCount(2);
        import.Errors[0].Should().StartWith("Line 2:");
        import.Errors[1].Should().StartWith("Line 3:").And.Contain("\"B\"");
    }

    [Fact]
    public void Fceux_cheat_files_import()
    {
        const string cht = """
            0300:0f:Max health
            :0301:02:Switched off
            C075a:09:03:Lives only when 3
            S91d9:ad:ROM patch
            """;

        var import = CheatFiles.Import(cht, "game.cht");

        import.Errors.Should().BeEmpty();
        import.Codes.Should().Equal(
            ("0300:0F", "Max health", true),
            ("0301:02", "Switched off", false),
            ("075A?03:09", "Lives only when 3", true),
            ("SXIOPO", "ROM patch", true));

        CheatFiles.Import("2000:01:PPU register", "x.cht").Errors.Should().ContainSingle().Which.Should().Contain("$2000");
        CheatFiles.Import("not a line", "x.cht").Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Bundled_suggestions_are_read_by_hash_and_bad_codes_reported()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "a.json"), """
            { "sha1": "0123456789abcdef0123456789abcdef01234567", "title": "Example Quest",
              "codes": [ { "code": "sxiopo", "description": "Infinite lives" }, { "code": "BAD!", "description": "x" } ] }
            """);
        File.WriteAllText(Path.Combine(_root.FullName, "broken.json"), "{ not json");

        var suggestions = new CheatSuggestions(_root.FullName);

        suggestions.For("0123456789ABCDEF0123456789ABCDEF01234567").Should().Equal(new SuggestedCode("SXIOPO", "Infinite lives"));
        suggestions.Errors.Should().HaveCount(2);
        new CheatSuggestions(Path.Combine(_root.FullName, "missing")).Count.Should().Be(0);
    }
}
