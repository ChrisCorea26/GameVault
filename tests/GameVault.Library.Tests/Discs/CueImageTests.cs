using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Library.Discs;

namespace GameVault.Library.Tests.Discs;

/// <summary>Cue sheets over .bin files, and .m3u playlists (SPEC §7.11, Phase 15).</summary>
public sealed class CueImageTests
{
    private static byte[] Read(ICdImage image, int lba)
    {
        var sector = new byte[ICdImage.SectorSize];
        image.ReadSector(lba, sector);
        return sector;
    }

    private static void ShouldMatchMixedDisc(ICdImage image)
    {
        for (var lba = 140; lba < SyntheticDisc.Track3Start + SyntheticDisc.Track3Sectors + 5; lba++)
        {
            Read(image, lba).Should().Equal(SyntheticDisc.MixedSector(lba), $"disc address {lba}");
        }
    }

    [Fact]
    public void A_single_bin_cue_maps_tracks_pregaps_and_gaps()
    {
        using var folder = new TempFolder();
        folder.Write("mixed disc.bin", SyntheticDisc.MixedBin());
        var cue = folder.Write("mixed.cue", SyntheticDisc.MixedCue);

        using var image = (CueImage)DiscImages.Open(cue);

        image.Tracks.Should().Equal(
            new CdTrack(1, CdTrackType.Mode1, 150, 16, 0),
            new CdTrack(2, CdTrackType.Audio, 176, 22, 10),
            new CdTrack(3, CdTrackType.Mode2, 203, 8, 5));
        image.LeadOut.Should().Be(211);
        ShouldMatchMixedDisc(image);
        image.Files.Select(Path.GetFileName).Should().Equal("mixed.cue", "mixed disc.bin");
    }

    [Fact]
    public void A_sub_file_named_like_the_cue_sheet_gives_each_sectors_subcode()
    {
        using var folder = new TempFolder();
        folder.Write("mixed disc.bin", SyntheticDisc.MixedBin());
        var cue = folder.Write("mixed.cue", SyntheticDisc.MixedCue);
        using (var plain = (CueImage)DiscImages.Open(cue))
        {
            plain.HasSubcode.Should().BeFalse("most images come without subcode");
        }

        // CloneCD's layout: per sector, 12 bytes of P, then Q, R … W, each MSB first.
        var sub = new byte[3 * 96];
        sub[0] = 0x80;                        // sector 0: P's first bit (symbol 0)
        sub.AsSpan(2 * 12, 12).Fill(0xFF);   // sector 0: R in every symbol
        sub[96 + 7 * 12 + 11] = 0x01;         // sector 1: W's last bit (symbol 95)
        folder.Write("MIXED.SUB", sub);

        using var image = (CueImage)DiscImages.Open(cue);
        image.HasSubcode.Should().BeTrue();
        var symbols = new byte[96];

        image.ReadSubcode(150, symbols);
        symbols[0].Should().Be(0xA0, "P (bit 7) and R (bit 5)");
        symbols[1..].Should().AllBeEquivalentTo((byte)0x20);

        image.ReadSubcode(151, symbols);
        symbols[95].Should().Be(0x01, "W is bit 0");
        symbols[..95].Should().AllBeEquivalentTo((byte)0);

        image.ReadSubcode(149, symbols);
        symbols.Should().AllBeEquivalentTo((byte)0, "the lead-in pregap has none in the file");
        image.ReadSubcode(400, symbols);
        symbols.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void One_bin_per_track_with_index_00_in_each_file()
    {
        using var folder = new TempFolder();
        var bin = SyntheticDisc.MixedBin();
        folder.Write("Track 1.bin", bin[..(16 * 2352)]);
        folder.Write("Track 2.bin", bin[(16 * 2352)..(48 * 2352)]);
        folder.Write("Track 3.bin", bin[(48 * 2352)..]);
        var cue = folder.Write("split.cue",
            "FILE \"Track 1.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n" +
            "FILE \"Track 2.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:10\n" +
            "FILE \"Track 3.bin\" BINARY\n  TRACK 03 MODE2/2352\n    PREGAP 00:00:05\n    INDEX 01 00:00:00\n");

        using var image = (ExtentCdImage)DiscImages.Open(cue);

        image.Tracks.Select(t => t.Start).Should().Equal(150, 176, 203);
        ShouldMatchMixedDisc(image);
        DiscImages.Files(cue).Should().HaveCount(4);
    }

    [Fact]
    public void Cooked_2048_byte_sectors_are_rebuilt_with_header_edc_and_ecc()
    {
        using var folder = new TempFolder();
        folder.Write("cooked.bin", SyntheticDisc.CookedBin());
        var cue = folder.Write("cooked.cue", SyntheticDisc.CookedCue);

        using var image = (ExtentCdImage)DiscImages.Open(cue);

        for (var i = 0; i < SyntheticDisc.CookedSectors; i++)
        {
            Read(image, 150 + i).Should().Equal(SyntheticDisc.Mode1Raw(150 + i));
        }
    }

    [Fact]
    public void Mode2_2336_sectors_get_their_sync_and_header()
    {
        using var folder = new TempFolder();
        var expected = SyntheticDisc.Mode2Raw(150);
        folder.Write("m2.bin", expected[16..]);
        var cue = folder.Write("m2.cue", "FILE m2.bin BINARY\nTRACK 01 MODE2/2336\nINDEX 01 00:00:00\n");

        using var image = (ExtentCdImage)DiscImages.Open(cue);

        Read(image, 150).Should().Equal(expected);
    }

    [Fact]
    public void Motorola_audio_is_byte_swapped_and_file_names_match_ignoring_case()
    {
        using var folder = new TempFolder();
        var audio = SyntheticDisc.Audio(3, silent: false);
        var swapped = (byte[])audio.Clone();
        for (var i = 0; i < swapped.Length; i += 2)
        {
            (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        }

        folder.Write("SONG.BIN", swapped);
        var cue = folder.Write("song.cue", "FILE \"song.bin\" MOTOROLA\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n");

        using var image = (ExtentCdImage)DiscImages.Open(cue);

        Read(image, 150).Should().Equal(audio);
    }

    [Fact]
    public void Unsupported_and_broken_cue_sheets_give_clear_messages()
    {
        using var folder = new TempFolder();
        var wave = folder.Write("wave.cue", "FILE \"a.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n");
        var missing = folder.Write("missing.cue", "FILE \"gone.bin\" BINARY\nTRACK 01 MODE2/2352\nINDEX 01 00:00:00\n");
        folder.Write("x.bin", new byte[2352]);
        var noIndex = folder.Write("noindex.cue", "FILE x.bin BINARY\nTRACK 01 MODE2/2352\n");

        FluentActions.Invoking(() => DiscImages.Open(wave)).Should().Throw<InvalidDataException>().WithMessage("*WAVE*");
        FluentActions.Invoking(() => DiscImages.Open(missing)).Should().Throw<InvalidDataException>().WithMessage("*gone.bin*");
        FluentActions.Invoking(() => DiscImages.Open(noIndex)).Should().Throw<InvalidDataException>().WithMessage("*INDEX 01*");
        FluentActions.Invoking(() => DiscImages.Open(Path.Combine(folder.Path, "x.bin"))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Playlists_resolve_relative_paths_and_skip_comments()
    {
        using var folder = new TempFolder();
        folder.Write("Disc 1.cue", "x");
        folder.Write("discs/Disc 2.chd", "x");
        var m3u = folder.Write("game.m3u", "﻿# a two-disc game\n\nDisc 1.cue\ndiscs\\Disc 2.chd\n");

        var discs = DiscImages.ReadPlaylist(m3u);

        discs.Should().Equal(Path.Combine(folder.Path, "Disc 1.cue"), Path.Combine(folder.Path, "discs", "Disc 2.chd"));
        DiscImages.IsDiscImage(m3u).Should().BeTrue();
        DiscImages.IsDiscImage("game.CHD").Should().BeTrue();
        DiscImages.IsDiscImage("game.bin").Should().BeFalse();
    }
}
