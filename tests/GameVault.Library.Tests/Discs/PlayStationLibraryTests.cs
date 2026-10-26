using System.Security.Cryptography;
using System.Text;
using GameVault.Core.Systems;
using GameVault.Library.Catalog;
using GameVault.Library.Discs;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;

namespace GameVault.Library.Tests.Discs;

/// <summary>PlayStation games in the library (SPEC §7.11, Phase 15): disc references, PS-EXEs, hashing and Redump DATs.</summary>
public sealed class PlayStationLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gv-ps-" + Guid.NewGuid().ToString("N"));

    public PlayStationLibraryTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string WriteCueBin(string name = "game")
    {
        File.WriteAllBytes(Path.Combine(_folder, name + ".bin"), SyntheticDisc.MixedBin());
        var cue = SyntheticDisc.MixedCue.Replace("mixed disc.bin", name + ".bin");
        var path = Path.Combine(_folder, name + ".cue");
        File.WriteAllText(path, cue);
        return path;
    }

    [Fact]
    public void A_cue_is_read_as_a_reference_to_the_disc()
    {
        var cue = WriteCueBin();

        var rom = RomFiles.Read(cue);

        Assert.NotNull(rom);
        Assert.Equal(GameSystem.PlayStation, rom.System);
        Assert.True(PlayStationGames.TryGetPath(rom.Data, out var path));
        Assert.Equal(Path.GetFullPath(cue), path);
        Assert.Null(RomFiles.Read(Path.Combine(_folder, "game.bin"))); // a track on its own isn't a game
    }

    [Fact]
    public void Discs_hash_their_first_track_as_redump_lists_it()
    {
        var cue = WriteCueBin();
        var rom = RomFiles.Read(cue)!;

        var hashes = RomHasher.Hash(rom.Data, GameSystem.PlayStation);

        var track1 = SyntheticDisc.MixedBin()[..(SyntheticDisc.Track1Sectors * 2352)];
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(track1)), hashes.Sha1);
    }

    [Fact]
    public void A_playlist_hashes_its_first_disc()
    {
        WriteCueBin("disc1");
        WriteCueBin("disc2");
        var m3u = Path.Combine(_folder, "game.m3u");
        File.WriteAllText(m3u, "# two discs\ndisc1.cue\ndisc2.cue\n");

        var rom = RomFiles.Read(m3u)!;

        Assert.Equal(GameSystem.PlayStation, rom.System);
        Assert.Equal(2, PlayStationGames.DiscPaths(m3u).Count);
        Assert.Equal(RomHasher.Hash(RomFiles.Read(Path.Combine(_folder, "disc1.cue"))!.Data, GameSystem.PlayStation), RomHasher.Hash(rom.Data, GameSystem.PlayStation));
    }

    [Fact]
    public void Only_ps_exe_files_count_among_exe_files()
    {
        var exe = new byte[0x900];
        "PS-X EXE"u8.CopyTo(exe);
        File.WriteAllBytes(Path.Combine(_folder, "demo.exe"), exe);
        File.WriteAllBytes(Path.Combine(_folder, "setup.exe"), "MZ\u0090\0"u8.ToArray().Concat(new byte[0x900]).ToArray());

        Assert.Equal(GameSystem.PlayStation, RomFiles.Read(Path.Combine(_folder, "demo.exe"))!.System);
        Assert.Null(RomFiles.Read(Path.Combine(_folder, "setup.exe")));
    }

    [Fact]
    public void Redump_dats_match_discs_on_track_one()
    {
        const string dat = """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sony - PlayStation</name></header>
              <game name="Quest Saga (USA)">
                <rom name="Quest Saga (USA).cue" size="100" crc="11111111" sha1="1111111111111111111111111111111111111111"/>
                <rom name="Quest Saga (USA) (Track 1).bin" size="2352" crc="22222222" sha1="2222222222222222222222222222222222222222"/>
                <rom name="Quest Saga (USA) (Track 2).bin" size="2352" crc="33333333" sha1="3333333333333333333333333333333333333333"/>
              </game>
            </datafile>
            """;

        var (games, skipped) = CatalogImporter.Parse(new MemoryStream(Encoding.UTF8.GetBytes(dat)));

        Assert.Equal(0, skipped);
        var game = Assert.Single(games);
        Assert.Equal("22222222", game.Crc32);
        Assert.Equal("2222222222222222222222222222222222222222", game.Sha1);
        Assert.Equal(GameSystem.PlayStation, game.System);
    }
}
