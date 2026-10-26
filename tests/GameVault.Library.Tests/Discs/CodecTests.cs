using FluentAssertions;
using GameVault.Library.Discs;

namespace GameVault.Library.Tests.Discs;

/// <summary>The LZMA and FLAC decoders behind CHD's cdlz and cdfl codecs, on streams from the reference encoders.</summary>
public sealed class CodecTests
{
    [Fact]
    public void Lzma_decodes_a_raw_stream_from_python_lzma()
    {
        var expected = SyntheticDisc.LzmaInput();
        var output = new byte[expected.Length];

        new LzmaDecoder().Decode(CodecVectors.Lzma, output).Should().Be(expected.Length);

        output.Should().Equal(expected);
    }

    [Fact]
    public void Lzma_stops_at_the_end_marker_when_the_buffer_is_larger()
    {
        var output = new byte[10_000];

        new LzmaDecoder().Decode(CodecVectors.Lzma, output).Should().Be(6000);
    }

    [Fact]
    public void Lzma_rejects_a_truncated_stream()
    {
        var output = new byte[6000];
        var act = () => new LzmaDecoder().Decode(CodecVectors.Lzma.AsSpan(0, 200), output);

        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("smooth", nameof(CodecVectors.FlacSmoothBest))]
    [InlineData("smooth", nameof(CodecVectors.FlacSmoothFast))]
    [InlineData("smooth", nameof(CodecVectors.FlacSmoothLong))]
    [InlineData("noise", nameof(CodecVectors.FlacNoise))]
    [InlineData("steps", nameof(CodecVectors.FlacSteps))]
    public void Flac_decodes_frames_from_the_reference_encoder(string kind, string vector)
    {
        var frames = (byte[])typeof(CodecVectors).GetField(vector)!.GetValue(null)!;
        var expected = SyntheticDisc.FlacInput(kind);
        var output = new byte[expected.Length];

        var consumed = new FlacDecoder().DecodeStereo16(frames, expected.Length / 4, output, bigEndian: false);

        output.Should().Equal(expected);
        consumed.Should().Be(frames.Length, "the frames end where the data does");
    }

    [Fact]
    public void Flac_can_write_big_endian_samples()
    {
        var expected = SyntheticDisc.FlacInput("smooth");
        var output = new byte[expected.Length];

        new FlacDecoder().DecodeStereo16(CodecVectors.FlacSmoothBest, expected.Length / 4, output, bigEndian: true);

        output[0].Should().Be(expected[1]);
        output[1].Should().Be(expected[0]);
    }

    [Fact]
    public void Flac_rejects_a_corrupt_frame()
    {
        var frames = (byte[])CodecVectors.FlacSmoothFast.Clone();
        frames[100] ^= 0x55;
        var output = new byte[3000 * 4];

        var act = () => new FlacDecoder().DecodeStereo16(frames, 3000, output, bigEndian: false);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void The_ecc_matches_the_reference_sectors()
    {
        // The fixture generator computed these sectors' EDC and ECC independently, and chdman verified them.
        System.Security.Cryptography.SHA256.HashData(SyntheticDisc.MixedBin())
            .Should().Equal(Convert.FromHexString(ChdFixtures.MixedBinSha256));
        System.Security.Cryptography.SHA256.HashData(SyntheticDisc.CookedBin())
            .Should().Equal(Convert.FromHexString(ChdFixtures.CookedBinSha256));
    }
}
