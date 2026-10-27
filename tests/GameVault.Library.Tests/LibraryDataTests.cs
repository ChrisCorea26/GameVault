using System.Diagnostics;
using GameVault.Library.Catalog;
using GameVault.Library.Data;
using GameVault.Library.Hashing;
using GameVault.Library.Models;
using GameVault.Library.Scanning;
using GameVault.Library.Tests.Fixtures;

namespace GameVault.Library.Tests;

public sealed class LibraryDataTests : IDisposable
{
    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Hashes_ignore_the_header_and_trainer()
    {
        var plain = RomHasher.Hash(LibraryFixture.Rom(7));
        var withTrainer = RomHasher.Hash(LibraryFixture.Rom(7, trainer: true));
        var headerless = RomHasher.Hash(LibraryFixture.Rom(7).AsSpan(16));

        withTrainer.Should().Be(plain);
        headerless.Should().Be(plain);
        plain.Sha1.Should().HaveLength(40);
        plain.Crc32.Should().HaveLength(8);
    }

    [Fact]
    public void Dat_names_give_titles_regions_and_licensing()
    {
        CatalogImporter.CleanTitle("Quest Saga (USA) (Rev 1)").Should().Be("Quest Saga");
        CatalogImporter.RegionOf("Quest Saga (USA)").Should().Be(GameRegion.NorthAmerica);
        CatalogImporter.RegionOf("Quest Saga (Japan)").Should().Be(GameRegion.Japan);
        CatalogImporter.RegionOf("Quest Saga (Germany)").Should().Be(GameRegion.Europe);
        CatalogImporter.RegionOf("Quest Saga (USA, Europe)").Should().Be(GameRegion.NorthAmerica);
        CatalogImporter.RegionOf("Quest Saga (World)").Should().Be(GameRegion.World);
        CatalogImporter.IsLicensed("Quest Saga (USA)").Should().BeTrue();
        CatalogImporter.IsLicensed("Quest Saga (USA) (Unl)").Should().BeFalse();
        CatalogImporter.IsLicensed("Quest Saga (Japan) (Proto)").Should().BeFalse();
    }

    [Fact]
    public void Importing_a_dat_twice_replaces_its_entries()
    {
        var importer = new CatalogImporter(_fx.Database);

        importer.Import(LibraryFixture.Dat(("Alpha (USA)", 1), ("Beta (Japan)", 2)), "test.dat").Games.Should().Be(2);
        importer.Import(LibraryFixture.Dat(("Alpha (USA)", 1)), "test.dat");

        var games = _fx.Repository.GetGames();
        games.Should().ContainSingle().Which.Title.Should().Be("Alpha");
        games[0].IsCataloged.Should().BeTrue();
        games[0].IsPlayable.Should().BeFalse();
    }

    [Fact]
    public void Scan_matches_catalog_finds_zips_and_keeps_unknown_files()
    {
        new CatalogImporter(_fx.Database).Import(LibraryFixture.Dat(("Alpha (USA)", 1), ("Beta (Europe)", 2), ("Gamma (USA)", 3)), "test.dat");
        _fx.WriteRom("a/alpha.nes", 1);
        _fx.WriteZip("b/beta.zip", ("beta.nes", 2));
        _fx.WriteZip("b/two.zip", ("x.nes", 4), ("y.nes", 5)); // ambiguous: ignored
        _fx.WriteRom("c/My Homebrew.nes", 9);
        File.WriteAllText(Path.Combine(_fx.Root, "readme.txt"), "not a rom");

        var result = new LibraryScanner(_fx.Database).Scan([_fx.Root]);

        result.Added.Should().Be(3);
        result.Unreadable.Should().Be(1);
        var games = _fx.Repository.GetGames().ToDictionary(g => g.Title);
        games["Alpha"].IsPlayable.Should().BeTrue();
        games["Alpha"].Owned.Should().HaveFlag(Ownership.DigitalDump);
        games["Beta"].RomPath.Should().EndWith("beta.zip");
        games["Beta"].Region.Should().Be(GameRegion.Europe);
        games["Gamma"].IsPlayable.Should().BeFalse("it is only in the checklist");
        games["My Homebrew"].IsCataloged.Should().BeFalse();
        games["My Homebrew"].IsLicensed.Should().BeFalse();
    }

    [Fact]
    public void Disk_system_images_are_found_and_hashed_without_the_fwnes_header()
    {
        static byte[] Disk(byte seed)
        {
            var side = new byte[65500];
            side[0] = 0x01;
            "*NINTENDO-HVC*"u8.CopyTo(side.AsSpan(1));
            side[100] = seed;
            return side;
        }

        byte[] withHeader = [.. "FDS\u001A"u8, 1, .. new byte[11], .. Disk(1)];
        RomHasher.Hash(withHeader).Should().Be(RomHasher.Hash(Disk(1)), "No-Intro hashes FDS images without the fwNES header");

        File.WriteAllBytes(Path.Combine(_fx.Root, "Disk Quest.fds"), withHeader);
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(_fx.Root, "zipped.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("Zipped Disk.fds").Open();
            stream.Write(Disk(2));
        }

        var result = new LibraryScanner(_fx.Database).Scan([_fx.Root]);

        result.Added.Should().Be(2);
        LibraryScanner.IsGameFile("x.FDS").Should().BeTrue();
        LibraryScanner.ReadRom(Path.Combine(_fx.Root, "zipped.zip")).Should().Equal(Disk(2));
    }

    [Fact]
    public void Master_system_and_sg1000_games_are_found_with_their_system()
    {
        var sms = new byte[0x8000];
        sms[0x7FF0] = 0x54; // "TMR SEGA" header area
        sms[123] = 7;
        var sg = new byte[0x4000];
        sg[5] = 9;
        File.WriteAllBytes(Path.Combine(_fx.Root, "Sega Quest.sms"), sms);
        File.WriteAllBytes(Path.Combine(_fx.Root, "Old Times.SG"), sg);
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(_fx.Root, "zipped.zip"), System.IO.Compression.ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("Zipped.sc").Open();
            stream.Write(new byte[0x2000]);
        }

        new LibraryScanner(_fx.Database).Scan([_fx.Root]).Added.Should().Be(3);

        var games = _fx.Repository.GetGames().ToDictionary(g => g.Title);
        games["Sega Quest"].System.Should().Be(GameVault.Core.Systems.GameSystem.MasterSystem);
        games["Old Times"].System.Should().Be(GameVault.Core.Systems.GameSystem.Sg1000);
        games["zipped"].System.Should().Be(GameVault.Core.Systems.GameSystem.Sg1000);
        games["Sega Quest"].Key.Should().Be("sha1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(sms)));

        // A 512-byte copier header doesn't change the game's identity.
        byte[] copier = [.. new byte[512], .. sms];
        RomHasher.Hash(copier, GameVault.Core.Systems.GameSystem.MasterSystem).Should().Be(RomHasher.Hash(sms, GameVault.Core.Systems.GameSystem.MasterSystem));
    }

    [Fact]
    public void Dats_name_their_system()
    {
        const string dat = """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sega - Master System - Mark III</name></header>
              <game name="Sega Quest (USA)"><rom name="Sega Quest (USA).sms" size="32768" crc="01234567" sha1="0123456789abcdef0123456789abcdef01234567"/></game>
            </datafile>
            """;
        var (games, _) = CatalogImporter.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(dat)));
        games.Should().ContainSingle().Which.System.Should().Be(GameVault.Core.Systems.GameSystem.MasterSystem);
    }

    [Fact]
    public void Rescan_skips_unchanged_files_and_drops_removed_ones()
    {
        var scanner = new LibraryScanner(_fx.Database);
        var keep = _fx.WriteRom("keep.nes", 1);
        var gone = _fx.WriteRom("gone.nes", 2);
        scanner.Scan([_fx.Root]);

        File.Delete(gone);
        var result = scanner.Scan([_fx.Root]);

        result.Unchanged.Should().Be(1);
        result.Removed.Should().Be(1);
        _fx.Repository.GetGames().Should().ContainSingle().Which.RomPath.Should().Be(keep);
    }

    [Fact]
    public void Catalog_import_after_a_scan_recognizes_files_on_rematch()
    {
        var scanner = new LibraryScanner(_fx.Database);
        _fx.WriteRom("delta.nes", 4);
        scanner.Scan([_fx.Root]);

        new CatalogImporter(_fx.Database).Import(LibraryFixture.Dat(("Delta (USA)", 4)), "late.dat");
        scanner.Rematch();

        var game = _fx.Repository.GetGames().Should().ContainSingle().Subject;
        game.Title.Should().Be("Delta");
        game.IsPlayable.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_games_details_are_saved_so_it_can_be_filtered_and_grouped()
    {
        _fx.WriteRom("alpha.nes", 1);
        new LibraryScanner(_fx.Database).Scan([_fx.Root]);
        var game = _fx.Repository.GetGames().Single();
        game.IsCataloged.Should().BeFalse();

        _fx.Repository.SaveCollection(game with
        {
            Publisher = "Community",
            Developer = "Jo Example",
            ReleaseYear = 2021,
            Genre = "Puzzle",
            Region = GameRegion.World,
            Players = 2,
        });

        var saved = _fx.Repository.GetGame(game.Key)!;
        saved.Publisher.Should().Be("Community");
        saved.Developer.Should().Be("Jo Example");
        saved.ReleaseYear.Should().Be(2021);
        saved.Genre.Should().Be("Puzzle");
        saved.Region.Should().Be(GameRegion.World);
        saved.Players.Should().Be(2);
    }

    [Fact]
    public void Collection_fields_and_playtime_are_saved()
    {
        _fx.WriteRom("alpha.nes", 1);
        new LibraryScanner(_fx.Database).Scan([_fx.Root]);
        var game = _fx.Repository.GetGames().Single();

        _fx.Repository.SaveCollection(game with
        {
            Title = "Alpha Homebrew",
            Owned = Ownership.Cartridge | Ownership.Box,
            Status = PlayStatus.Beaten,
            IsFavorite = true,
            Rating = 4,
            Notes = "Secret exit in world 2",
        });
        _fx.Repository.AddPlaytime(game.Key, TimeSpan.FromMinutes(30), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        _fx.Repository.AddPlaytime(game.Key, TimeSpan.FromMinutes(15), DateTimeOffset.FromUnixTimeSeconds(1_800_000_600));

        var saved = _fx.Repository.GetGame(game.Key)!;
        saved.Title.Should().Be("Alpha Homebrew");
        saved.Owned.Should().Be(Ownership.Cartridge | Ownership.Box | Ownership.DigitalDump);
        saved.Status.Should().Be(PlayStatus.Beaten);
        saved.IsFavorite.Should().BeTrue();
        saved.Rating.Should().Be(4);
        saved.Notes.Should().Be("Secret exit in world 2");
        saved.Playtime.Should().Be(TimeSpan.FromMinutes(45));
        saved.LastPlayed.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_800_000_600));
    }

    [Fact]
    public void Removing_a_folder_forgets_its_files_but_keeps_collection_data()
    {
        var a = Path.Combine(_fx.Root, "a");
        _fx.WriteRom("a/alpha.nes", 1);
        _fx.WriteRom("ab/other.nes", 2); // a sibling whose name starts the same must survive
        _fx.Repository.AddFolder(a);
        _fx.Repository.AddFolder(Path.Combine(_fx.Root, "ab"));
        new LibraryScanner(_fx.Database).Scan(_fx.Repository.GetFolders());
        var alpha = _fx.Repository.GetGames().Single(g => g.RomPath!.Contains("alpha"));
        _fx.Repository.SaveCollection(alpha with { IsFavorite = true });

        _fx.Repository.RemoveFolder(a);

        _fx.Repository.GetFolders().Should().ContainSingle();
        _fx.Repository.GetGames().Should().HaveCount(1, "alpha's file is forgotten; its favorite is kept for when it returns");
    }

    [Fact]
    public void Database_on_disk_survives_reopening()
    {
        var path = Path.Combine(_fx.Root, "library.db");
        using (var first = new LibraryDatabase(path))
        {
            new LibraryRepository(first).AddFolder(_fx.Root);
        }

        using var second = new LibraryDatabase(path);
        new LibraryRepository(second).GetFolders().Should().Equal(Path.GetFullPath(_fx.Root));
    }

    /// <summary>SPEC §10: 1,000 files in under 10 s on an SSD. CI gets the same budget.</summary>
    [Fact]
    public void Scans_a_thousand_files_in_under_ten_seconds()
    {
        for (var i = 0; i < 1000; i++)
        {
            var rom = LibraryFixture.Rom((byte)i);
            rom[^1] = (byte)(i >> 8); // unique content
            File.WriteAllBytes(Path.Combine(_fx.Root, $"game{i:D4}.nes"), rom);
        }

        var clock = Stopwatch.StartNew();
        var result = new LibraryScanner(_fx.Database).Scan([_fx.Root]);
        clock.Stop();

        result.Added.Should().Be(1000);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }
}

public sealed class LibraryWatcherTests : IDisposable
{
    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task Many_changes_raise_one_debounced_event()
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(300));
        var count = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);
        watcher.Watch([_fx.Root]);

        for (var i = 0; i < 20; i++)
        {
            _fx.WriteRom($"new{i}.nes", (byte)i);
        }

        await Task.Delay(1500);
        count.Should().Be(1);
    }
}
