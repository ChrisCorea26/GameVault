using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Platform.Emulation.Discs;

namespace GameVault.Platform.Tests.Discs;

/// <summary>
/// CHD discs (SPEC §7.11, Phase 15). The fixtures are chdman's own output for a generated disc with a mode 1 track
/// (ECC that chdman strips), an audio track with a stored pregap and silent hunks (copied hunks in the map) and a
/// mode 2 track behind a PREGAP, compressed with each CD codec in turn.
/// </summary>
public sealed class ChdImageTests
{
    private static byte[] Read(ICdImage image, int lba)
    {
        var sector = new byte[ICdImage.SectorSize];
        image.ReadSector(lba, sector);
        return sector;
    }

    private static ChdImage Open(TempFolder folder, byte[] chd) => (ChdImage)DiscImages.Open(folder.Write("disc.chd", chd));

    private static void ShouldMatchMixedDisc(ChdImage image)
    {
        image.Tracks.Should().Equal(
            new CdTrack(1, CdTrackType.Mode1, 150, 16, 0),
            new CdTrack(2, CdTrackType.Audio, 176, 22, 10),
            new CdTrack(3, CdTrackType.Mode2, 203, 8, 5));
        image.LeadOut.Should().Be(211);
        for (var lba = 140; lba < 216; lba++)
        {
            Read(image, lba).Should().Equal(SyntheticDisc.MixedSector(lba), $"disc address {lba}");
        }
    }

    [Fact]
    public void Cdzl_hunks_decode_and_stripped_ecc_is_regenerated()
    {
        using var folder = new TempFolder();
        using var image = Open(folder, ChdFixtures.MixedCdzl);

        ShouldMatchMixedDisc(image);
        image.DecodedHunks.Should().ContainKey("cdzl");
        image.RegeneratedSectors.Should().BeGreaterThan(0, "chdman strips the ECC of mode 1 sectors whose ECC checks out");
    }

    [Fact]
    public void Cdlz_hunks_decode()
    {
        using var folder = new TempFolder();
        using var image = Open(folder, ChdFixtures.MixedCdlz);

        ShouldMatchMixedDisc(image);
        image.DecodedHunks.Should().ContainKey("cdlz");
        image.RegeneratedSectors.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Cdfl_hunks_decode_with_audio_stored_big_endian()
    {
        using var folder = new TempFolder();
        using var image = Open(folder, ChdFixtures.MixedCdfl);

        ShouldMatchMixedDisc(image);
        image.DecodedHunks.Should().ContainKey("cdfl");
    }

    [Fact]
    public void Chdmans_default_mix_of_codecs_with_copied_hunks_decodes()
    {
        using var folder = new TempFolder();
        using var image = Open(folder, ChdFixtures.MixedDefault);

        ShouldMatchMixedDisc(image);
        image.DecodedHunks.Should().ContainKeys("cdlz", "cdfl");
        image.DecodedHunks.Should().ContainKey("self", "the silent audio hunks repeat");
    }

    [Fact]
    public void Cooked_mode1_tracks_are_rebuilt_as_raw_sectors()
    {
        using var folder = new TempFolder();
        using var image = Open(folder, ChdFixtures.CookedCdzl);

        image.Tracks.Should().Equal(new CdTrack(1, CdTrackType.Mode1, 150, SyntheticDisc.CookedSectors, 0));
        for (var i = 0; i < SyntheticDisc.CookedSectors; i++)
        {
            Read(image, 150 + i).Should().Equal(SyntheticDisc.Mode1Raw(150 + i));
        }
    }

    [Fact]
    public void Uncompressed_chds_read_through_their_plain_map()
    {
        // Two tracks: 3 mode 2 sectors (padded to 4 frames), then 2 audio frames, in hunks of 4 frames.
        var frames = new List<byte[]>
        {
            SyntheticDisc.Mode2Raw(150), SyntheticDisc.Mode2Raw(151), SyntheticDisc.Mode2Raw(152), new byte[2352],
            BigEndian(SyntheticDisc.Audio(1, silent: false)), BigEndian(SyntheticDisc.Audio(2, silent: false)),
        };
        var chd = UncompressedChd(frames, 4,
            "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:3 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:2 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0");
        using var folder = new TempFolder();
        using var image = Open(folder, chd);

        image.Tracks.Select(t => (t.Start, t.Length)).Should().Equal((150, 3), (153, 2));
        Read(image, 152).Should().Equal(SyntheticDisc.Mode2Raw(152));
        Read(image, 154).Should().Equal(SyntheticDisc.Audio(2, silent: false));
        image.DecodedHunks.Should().ContainKey("none");
    }

    [Fact]
    public void Damaged_and_unsupported_chds_give_clear_messages()
    {
        using var folder = new TempFolder();
        var corrupt = (byte[])ChdFixtures.MixedCdzl.Clone();
        var mapOffset = (int)BinaryPrimitives.ReadInt64BigEndian(corrupt.AsSpan(40));
        corrupt[mapOffset + 20] ^= 0xFF;
        var old = (byte[])ChdFixtures.MixedCdzl.Clone();
        old[15] = 4;

        FluentActions.Invoking(() => DiscImages.Open(folder.Write("corrupt.chd", corrupt))).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => DiscImages.Open(folder.Write("old.chd", old))).Should().Throw<InvalidDataException>().WithMessage("*version 4*");
        FluentActions.Invoking(() => DiscImages.Open(folder.Write("text.chd", "not a chd"))).Should().Throw<InvalidDataException>();
    }

    private static byte[] BigEndian(byte[] audio)
    {
        var swapped = (byte[])audio.Clone();
        for (var i = 0; i < swapped.Length; i += 2)
        {
            (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        }

        return swapped;
    }

    /// <summary>A minimal uncompressed CHD v5: header, plain map, CHT2 metadata, and hunks at hunk-sized offsets.</summary>
    private static byte[] UncompressedChd(List<byte[]> sectors, int framesPerHunk, params string[] tracks)
    {
        const int frameSize = 2448;
        var hunkBytes = frameSize * framesPerHunk;
        var hunks = (sectors.Count + framesPerHunk - 1) / framesPerHunk;
        var file = new MemoryStream();

        var header = new byte[124];
        "MComprHD"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 5);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32), (long)hunks * hunkBytes);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(40), 124);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), frameSize);
        file.Write(header);

        // The map: hunk n is at block n + 1 (block 0 would mean "all zeros").
        for (var n = 0; n < hunks; n++)
        {
            var entry = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)(n + 1));
            file.Write(entry);
        }

        // The metadata chain.
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(48), file.Position);
        for (var t = 0; t < tracks.Length; t++)
        {
            var text = Encoding.ASCII.GetBytes(tracks[t] + "\0");
            var entry = new byte[16];
            "CHT2"u8.CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), (uint)(0x01 << 24 | text.Length));
            var next = t + 1 < tracks.Length ? file.Position + 16 + text.Length : 0;
            BinaryPrimitives.WriteInt64BigEndian(entry.AsSpan(8), next);
            file.Write(entry);
            file.Write(text);
        }

        for (var n = 0; n < hunks; n++)
        {
            file.SetLength((n + 1L) * hunkBytes);
            file.Position = (n + 1L) * hunkBytes;
            for (var f = 0; f < framesPerHunk; f++)
            {
                var index = n * framesPerHunk + f;
                var frame = new byte[frameSize];
                if (index < sectors.Count)
                {
                    sectors[index].CopyTo(frame, 0);
                }

                file.Write(frame);
            }
        }

        var bytes = file.ToArray();
        header.CopyTo(bytes, 0);
        return bytes;
    }
}
