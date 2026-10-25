using System.IO.Compression;
using System.Text;
using GameVault.Library.Cheats;

namespace GameVault.Library.Tests;

/// <summary>Code collections the player imports (SPEC §7.8): .xlsx and .csv, title matching, notes.</summary>
public sealed class CodeCollectionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("gamevault-collections-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Store => Path.Combine(_root.FullName, "Collections");

    /// <summary>A minimal .xlsx: a "Notes" sheet first, then an "NES" sheet with shared and inline strings.</summary>
    private string Workbook(string name, params string?[][] rows)
    {
        var path = Path.Combine(_root.FullName, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Write(string entry, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Write("xl/workbook.xml", $"""<workbook xmlns="{Main}" xmlns:r="{Rel}"><sheets><sheet name="Info" sheetId="1" r:id="rId1"/><sheet name="NES" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        Write("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="/xl/worksheets/sheet2.xml"/></Relationships>""");
        Write("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="{Main}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Info</t></is></c></row></sheetData></worksheet>""");

        var shared = new List<string>();
        var sheet = new StringBuilder($"""<worksheet xmlns="{Main}"><sheetData>""");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"""<row r="{r + 1}">""");
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is not { } value)
                {
                    continue; // empty cells are simply absent, as Excel writes them
                }

                var reference = $"{(char)('A' + c)}{r + 1}";
                if (int.TryParse(value, out _))
                {
                    sheet.Append($"""<c r="{reference}"><v>{value}</v></c>""");
                }
                else if (c == 3)
                {
                    sheet.Append($"""<c r="{reference}" t="inlineStr"><is><r><t>{System.Security.SecurityElement.Escape(value)}</t></r></is></c>""");
                }
                else
                {
                    shared.Add(value);
                    sheet.Append($"""<c r="{reference}" t="s"><v>{shared.Count - 1}</v></c>""");
                }
            }

            sheet.Append("</row>");
        }

        Write("xl/worksheets/sheet2.xml", sheet + "</sheetData></worksheet>");
        Write("xl/sharedStrings.xml", $"""<sst xmlns="{Main}">{string.Concat(shared.Select(s => $"<si><t>{System.Security.SecurityElement.Escape(s)}</t></si>"))}</sst>""");
        return path;
    }

    [Theory]
    [InlineData("Super Mario Bros. (World)", "SUPER MARIO BROS.")]
    [InlineData("Legend of Zelda, The (USA)", "The Legend of Zelda")]
    [InlineData("Mega Man 2 (USA)", "MegaMan II")]
    [InlineData("Teenage Mutant Ninja Turtles II - The Arcade Game", "Teenage Mutant Ninja Turtles II: The Arcade Game")]
    [InlineData("Tom & Jerry [!]", "Tom and Jerry")]
    public void Titles_written_differently_match(string a, string b) => GameTitles.Key(a).Should().Be(GameTitles.Key(b));

    [Fact]
    public void Different_games_and_x_men_stay_apart()
    {
        GameTitles.Key("Mega Man 2").Should().NotBe(GameTitles.Key("Mega Man 3"));
        GameTitles.Key("X-Men").Should().Be("xmen", "a leading X isn't a roman numeral");
    }

    [Fact]
    public void A_spreadsheet_imports_codes_notes_and_sources()
    {
        var path = Workbook("My Codes.xlsx",
            ["Made-up Codes"],
            [],
            ["Game", "#", "Key in…", "Effect…", "Code Source"],
            ["Star Courier", "1", "PASIOALE", "Start with 9 ships", "My notebook"],
            ["Star Courier", "2", "sxio-po + AEKPIL", "Infinite fuel & speed", "My notebook"],
            ["Star Courier", null, "CODES 1 AND 2 WORK IN 1-PLAYER GAMES ONLY", null, "My notebook"],
            ["Moon Miner II", "1", "GXOOZTEY", "Slower timer", null],
            ["Broken Game", "1", "NOTACODE", "Nothing", null]);
        var collections = new CodeCollections(Store);

        var result = collections.Import(path);

        result.Succeeded.Should().BeTrue(string.Join(" ", result.Errors));
        result.Name.Should().Be("My Codes");
        result.Games.Should().Be(2, "a game with no valid codes is left out");
        result.Codes.Should().Be(3);

        var star = new CodeCollections(Store).Find("Star Courier (USA)").Should().ContainSingle().Subject;
        star.Collection.Should().Be("My Codes");
        star.Game.Codes.Select(c => c.Code).Should().Equal("PASIOALE", "SXIOPO + AEKPIL");
        star.Game.Codes[1].Description.Should().Be("Infinite fuel & speed");
        star.Game.Codes[0].Source.Should().Be("My notebook");
        star.Game.Notes.Should().Equal("CODES 1 AND 2 WORK IN 1-PLAYER GAMES ONLY");

        collections.Find("Moon Miner 2").Should().ContainSingle();
        collections.Search("moon").Should().Equal("Moon Miner II");
        collections.List().Should().Equal(("My Codes", 2, 3));

        collections.Remove("My Codes");
        collections.Find("Star Courier").Should().BeEmpty();
    }

    [Fact]
    public void Csv_files_import_too()
    {
        var path = Path.Combine(_root.FullName, "list.csv");
        File.WriteAllText(path, "Game,Code,Description\r\n\"Star Courier\",PASIOALE,\"Start with 9 ships, one player\"\r\nStar Courier,075A:09,\"Says \"\"hi\"\"\"\r\n");

        var result = new CodeCollections(Store).Import(path);

        result.Codes.Should().Be(2);
        var game = new CodeCollections(Store).Find("Star Courier")[0].Game;
        game.Codes[0].Description.Should().Be("Start with 9 ships, one player");
        game.Codes[1].Description.Should().Be("Says \"hi\"");
    }

    [Fact]
    public void Files_without_the_needed_columns_or_codes_are_refused()
    {
        var noHeader = Path.Combine(_root.FullName, "plain.csv");
        File.WriteAllText(noHeader, "just,some,cells\n");
        new CodeCollections(Store).Import(noHeader).Errors.Should().ContainSingle().Which.Should().Contain("header row");

        var noCodes = Path.Combine(_root.FullName, "empty.csv");
        File.WriteAllText(noCodes, "Game,Code,Effect\nX,NOPE,Nothing\n");
        new CodeCollections(Store).Import(noCodes).Errors.Should().ContainSingle().Which.Should().Contain("No NES");

        var notZip = Path.Combine(_root.FullName, "fake.xlsx");
        File.WriteAllText(notZip, "not a workbook");
        new CodeCollections(Store).Import(notZip).Succeeded.Should().BeFalse();
        new CodeCollections(Store).List().Should().BeEmpty("nothing is saved from a failed import");
    }

    [Fact]
    public void Codes_that_work_together_are_kept_together()
    {
        var import = CheatFiles.Import("OZTLLX + AATLGZ + SZLIVO        Always stay big\nSXIOPO\tInfinite lives\n", "codes.txt");

        import.Codes.Should().Equal(("OZTLLX + AATLGZ + SZLIVO", "Always stay big", true), ("SXIOPO", "Infinite lives", true));
        new CheatStore(_root.FullName).Add("sha1:aa", "oztllx+aatlgz", "Big").Parsed.Should().HaveCount(2);
    }
}
