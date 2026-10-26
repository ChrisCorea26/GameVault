using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.Systems;
using GameVault.Library.Catalog;
using GameVault.Library.Discs;
using GameVault.Library.Hashing;
using GameVault.Library.Scanning;

namespace GameVault.Library.Tests.Discs;

/// <summary>PlayStation 2 discs and ELFs in the library (SPEC §7.11, Phase 17), on synthetic ISO images built by the tests.</summary>
public sealed class PlayStation2LibraryTests
{
    private static byte[] Sector(IPs2Disc disc, long lba)
    {
        var sector = new byte[IPs2Disc.SectorSize];
        disc.ReadSector(lba, sector);
        return sector;
    }

    private static void ShouldReadAs(IPs2Disc disc, byte[] iso)
    {
        disc.SectorCount.Should().Be(iso.Length / 2048);
        for (var lba = 0; lba < disc.SectorCount; lba++)
        {
            Sector(disc, lba).Should().Equal(iso.AsSpan(lba * 2048, 2048).ToArray(), $"sector {lba}");
        }

        Sector(disc, disc.SectorCount).Should().OnlyContain(b => b == 0, "past the end reads as zeros");
        Sector(disc, -1).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void An_iso_of_2048_byte_sectors_is_a_dvd()
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso();
        var path = folder.Write("game.iso", iso);

        using var disc = (IsoPs2Disc)Ps2Discs.Open(path);

        disc.Type.Should().Be(Ps2DiscType.Dvd);
        disc.Cd.Should().BeNull();
        disc.Layer1Start.Should().Be(0);
        ShouldReadAs(disc, iso);
    }

    [Fact]
    public void A_dual_layer_iso_reports_where_its_second_layer_starts()
    {
        using var folder = new TempFolder();
        var layer0 = Ps2TestImages.Iso();
        var path = folder.Write("game.iso", Ps2TestImages.DualLayer(layer0));

        using var disc = Ps2Discs.Open(path);

        disc.Type.Should().Be(Ps2DiscType.DvdDualLayer);
        disc.Layer1Start.Should().Be(layer0.Length / 2048);
    }

    [Theory]
    [InlineData("game.iso")]
    [InlineData("game.bin")]
    public void A_raw_image_of_2352_byte_sectors_is_a_cd(string name)
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso();
        var path = folder.Write(name, Ps2TestImages.Raw(iso));

        using var disc = Ps2Discs.Open(path);

        disc.Type.Should().Be(Ps2DiscType.Cd);
        disc.Cd.Should().NotBeNull();
        disc.Cd!.Tracks[0].Start.Should().Be(150);
        ShouldReadAs(disc, iso);
    }

    [Fact]
    public void A_cue_bin_is_read_through_the_cd_reader_as_mode_2_form_1_user_data()
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso();
        folder.Write("game.bin", Ps2TestImages.Raw(iso));
        var cue = folder.Write("game.cue", Ps2TestImages.Cue("game.bin"));

        using var disc = Ps2Discs.Open(cue);

        disc.Type.Should().Be(Ps2DiscType.Cd);
        ShouldReadAs(disc, iso);
    }

    [Fact]
    public void Reads_from_several_threads_agree()
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso(padSectors: 200);
        folder.Write("game.bin", Ps2TestImages.Raw(iso));
        using var disc = Ps2Discs.Open(folder.Write("game.cue", Ps2TestImages.Cue("game.bin")));

        Parallel.For(0, 8, worker =>
        {
            var sector = new byte[2048];
            for (var i = 0; i < 400; i++)
            {
                var lba = (i * 7 + worker * 13) % disc.SectorCount;
                disc.ReadSector(lba, sector);
                sector.AsSpan().SequenceEqual(iso.AsSpan((int)lba * 2048, 2048)).Should().BeTrue();
            }
        });
    }

    [Fact]
    public void System_cnf_gives_the_boot_path_serial_and_video_mode()
    {
        var cnf = Ps2SystemCnf.Parse("BOOT2 = cdrom0:\\SLES_123.45;1\r\nVER = 1.01\r\nVMODE = PAL\r\n");

        cnf.IsPlayStation2.Should().BeTrue();
        cnf.Boot2.Should().Be(@"cdrom0:\SLES_123.45;1");
        cnf.BootFile.Should().Be("SLES_123.45");
        cnf.Serial.Should().Be("SLES_123.45");
        cnf.Version.Should().Be("1.01");
        cnf.VideoMode.Should().Be(Ps2VideoMode.Pal);

        var ps1 = Ps2SystemCnf.Parse("BOOT = cdrom:\\SCUS_944.26;1 arg\nTCB = 4\n");
        ps1.IsPlayStation2.Should().BeFalse();
        ps1.Boot.Should().Be(@"cdrom:\SCUS_944.26;1");
        ps1.Serial.Should().BeNull();
        ps1.VideoMode.Should().BeNull();

        var nested = Ps2SystemCnf.Parse("boot2=cdrom0:\\DATA\\MAIN.ELF;1\nvmode=ntsc");
        nested.BootFile.Should().Be(@"DATA\MAIN.ELF");
        nested.Serial.Should().Be("MAIN.ELF");
        nested.VideoMode.Should().Be(Ps2VideoMode.Ntsc);
    }

    [Fact]
    public void System_cnf_and_files_are_found_through_the_iso_9660_root_and_subdirectories()
    {
        var extra = Encoding.ASCII.GetBytes(new string('x', 5000));
        var disc = new MemoryPs2Disc(Ps2TestImages.Iso(Ps2TestImages.SystemCnf(@"cdrom0:\DATA\MAIN.ELF;1", "PAL"), new() { [@"DATA\MAIN.ELF"] = extra }));

        var cnf = Ps2SystemCnf.Read(disc)!;

        cnf.BootFile.Should().Be(@"DATA\MAIN.ELF");
        cnf.VideoMode.Should().Be(Ps2VideoMode.Pal);
        Iso9660.ReadFile(disc, cnf.Boot2!).Should().Equal(extra);
        Iso9660.ReadFile(disc, "data/main.elf").Should().Equal(extra, "names compare case-insensitively");
        Iso9660.ReadFile(disc, Ps2TestImages.Serial).Should().Equal(Ps2TestImages.Elf());
        Iso9660.FindFile(disc, "MISSING.BIN").Should().BeNull();
        Ps2SystemCnf.Read(new MemoryPs2Disc(new byte[40 * 2048])).Should().BeNull("there's no file system");
    }

    [Fact]
    public void The_library_tells_ps2_discs_from_playstation_discs_by_system_cnf()
    {
        using var folder = new TempFolder();
        var ps2Iso = folder.Write("ps2.iso", Ps2TestImages.Iso());
        var ps1Iso = folder.Write("ps1.iso", Ps2TestImages.Iso("BOOT = cdrom:\\SLUS_000.01;1\r\n", ps2: false));
        var dataIso = folder.Write("data.iso", Ps2TestImages.Iso(ps2: false));
        folder.Write("ps2.bin", Ps2TestImages.Raw(Ps2TestImages.Iso()));
        var ps2Cue = folder.Write("ps2.cue", Ps2TestImages.Cue("ps2.bin"));
        folder.Write("ps1.bin", Ps2TestImages.Raw(Ps2TestImages.Iso("BOOT = cdrom:\\SLUS_000.01;1\r\n", ps2: false)));
        var ps1Cue = folder.Write("ps1.cue", Ps2TestImages.Cue("ps1.bin"));
        var m3u = folder.Write("ps2.m3u", "ps2.cue\n");

        var rom = RomFiles.Read(ps2Iso)!;
        rom.System.Should().Be(GameSystem.PlayStation2);
        Ps2Discs.TryGetPath(rom.Data, out var path).Should().BeTrue();
        path.Should().Be(Path.GetFullPath(ps2Iso));
        PlayStationGames.TryGetPath(rom.Data, out _).Should().BeFalse();

        RomFiles.Read(ps2Cue)!.System.Should().Be(GameSystem.PlayStation2);
        RomFiles.Read(m3u)!.System.Should().Be(GameSystem.PlayStation2);
        var ps1 = RomFiles.Read(ps1Cue)!;
        ps1.System.Should().Be(GameSystem.PlayStation);
        PlayStationGames.TryGetPath(ps1.Data, out _).Should().BeTrue();
        RomFiles.Read(ps1Iso)!.System.Should().Be(GameSystem.PlayStation, "BOOT (not BOOT2) is a PlayStation disc");
        RomFiles.Read(dataIso).Should().BeNull("an .iso with no SYSTEM.CNF or PSX.EXE isn't a game");
    }

    [Fact]
    public void A_playstation_iso_opens_as_a_cd_with_rebuilt_mode_2_sectors()
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso("BOOT = cdrom:\\SLUS_000.01;1\r\n", ps2: false);
        var path = folder.Write("ps1.iso", iso);

        using var cd = (IsoCdImage)DiscImages.Open(path);

        cd.Tracks[0].Start.Should().Be(150);
        var raw = new byte[2352];
        cd.ReadSector(150 + 16, raw);
        raw[..12].Should().Equal(CdSector.Sync.ToArray());
        raw[15].Should().Be(2, "mode 2");
        raw.AsSpan(24, 2048).ToArray().Should().Equal(iso.AsSpan(16 * 2048, 2048).ToArray());
        GameVault.Core.PlayStation.PsDisc.BootPath(cd).Should().Be(@"cdrom:\SLUS_000.01;1");
    }

    [Fact]
    public void Only_little_endian_mips_elfs_count_among_elf_files()
    {
        using var folder = new TempFolder();
        var good = folder.Write("demo.elf", Ps2TestImages.Elf());
        var x86 = Ps2TestImages.Elf();
        x86[18] = 3;
        var bigEndian = Ps2TestImages.Elf();
        bigEndian[5] = 2;

        RomFiles.Read(good)!.System.Should().Be(GameSystem.PlayStation2);
        RomFiles.Read(good)!.Data.Should().Equal(Ps2TestImages.Elf());
        RomFiles.Read(folder.Write("x86.elf", x86)).Should().BeNull();
        RomFiles.Read(folder.Write("be.elf", bigEndian)).Should().BeNull();
        RomFiles.Extensions.Should().Contain([".iso", ".elf"]);
    }

    [Fact]
    public void Dvds_hash_their_whole_image_and_cds_their_first_track()
    {
        using var folder = new TempFolder();
        var iso = Ps2TestImages.Iso(padSectors: 50);
        var dvd = RomFiles.Read(folder.Write("dvd.iso", iso))!;
        var raw = Ps2TestImages.Raw(iso);
        folder.Write("cd.bin", raw);
        var cd = RomFiles.Read(folder.Write("cd.cue", Ps2TestImages.Cue("cd.bin")))!;

        var dvdHashes = RomHasher.Hash(dvd.Data, GameSystem.PlayStation2);
        dvdHashes.Sha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(iso)));
        dvdHashes.Crc32.Should().Be(Crc32.HashToUInt32(iso).ToString("x8"));
        using (var disc = Ps2Discs.Open(folder.Write("again.iso", iso)))
        {
            Ps2Discs.Hash(disc).Md5.Should().Be(Convert.ToHexStringLower(MD5.HashData(iso)));
        }

        RomHasher.Hash(cd.Data, GameSystem.PlayStation2).Sha1.Should().Be(Convert.ToHexStringLower(SHA1.HashData(raw)), "Redump lists a CD's track 1 raw");
    }

    [Fact]
    public void Dvd_chds_decode_zlib_lzma_huff_and_flac_hunks_and_copies()
    {
        // 24 sectors of ISO (12 hunks of two sectors), then a hunk each of LZMA and FLAC data from the reference
        // encoders' vectors, then a copy of hunk 0.
        var iso = Ps2TestImages.Iso(padSectors: 1);
        iso.Length.Should().Be(12 * 4096);
        var lzma = SyntheticDisc.LzmaInput()[..4096];
        var flac = SyntheticDisc.FlacInput("smooth")[..4096];
        var isoHunks = iso.Length / 4096;
        var image = iso.Concat(lzma).Concat(flac).Concat(iso[..4096]).ToArray();
        var chd = Ps2TestImages.DvdChd(image, 4096, ["zlib", "lzma", "huff", "flac"], (n, data) =>
            n < isoHunks ? (n % 3) switch
            {
                0 => new Ps2TestImages.Hunk(0, Ps2TestImages.Deflate(data)),
                1 => new Ps2TestImages.Hunk(2, Ps2TestImages.Huff(data)),
                _ => new Ps2TestImages.Hunk(4, data),
            }
            : n == isoHunks ? new Ps2TestImages.Hunk(1, CodecVectors.Lzma)
            : n == isoHunks + 1 ? new Ps2TestImages.Hunk(3, Ps2TestImages.Flac(CodecVectors.FlacSmoothBest))
            : new Ps2TestImages.Hunk(5, [], CopyOf: 0));
        using var folder = new TempFolder();
        var path = folder.Write("game.chd", chd);

        using var disc = (ChdDvd)Ps2Discs.Open(path);

        disc.Type.Should().Be(Ps2DiscType.Dvd);
        ShouldReadAs(disc, image);
        disc.DecodedHunks.Should().ContainKeys("zlib", "lzma", "huff", "flac", "none", "self");
        Ps2SystemCnf.Read(disc)!.Serial.Should().Be(Ps2TestImages.Serial);
        RomFiles.Read(path)!.System.Should().Be(GameSystem.PlayStation2);
        FluentActions.Invoking(() => DiscImages.Open(path)).Should().Throw<InvalidDataException>().WithMessage("*DVD*");
    }

    [Fact]
    public void Ps2_dats_are_matched_on_the_dvd_image_or_cd_track_1()
    {
        const string dat = """
            <?xml version="1.0"?>
            <datafile>
              <header><name>Sony - PlayStation 2</name></header>
              <game name="Some DVD Game (USA)">
                <rom name="Some DVD Game (USA).iso" size="1000" crc="11111111" sha1="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"/>
              </game>
              <game name="Some CD Game (USA)">
                <rom name="Some CD Game (USA).cue" size="100" crc="22222222" sha1="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"/>
                <rom name="Some CD Game (USA) (Track 1).bin" size="1000" crc="33333333" sha1="cccccccccccccccccccccccccccccccccccccccc"/>
              </game>
            </datafile>
            """;

        var (games, _) = CatalogImporter.Parse(new MemoryStream(Encoding.UTF8.GetBytes(dat)));

        games.Select(g => (g.System, g.Sha1)).Should().Equal(
            (GameSystem.PlayStation2, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            (GameSystem.PlayStation2, "cccccccccccccccccccccccccccccccccccccccc"));
    }
}
