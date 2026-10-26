using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>The MDEC (SPEC §7.11, Phase 15), checked against hardware logs from ps1-tests where they exist.</summary>
public sealed class PsMdecTests
{
    // ---- Test vectors from ps1-tests (Jakub Czekański, MIT): mdec/4bit and mdec/8bit ----

    /// <summary>The standard scale table (common/mdec.cpp).</summary>
    private static readonly short[] ScaleTable =
    [
        23170, 23170, 23170, 23170, 23170, 23170, 23170, 23170, 32138, 27245, 18204, 6392, -6393, -18205, -27246, -32139,
        30273, 12539, -12540, -30274, -30274, -12540, 12539, 30273, 27245, -6393, -32139, -18205, 18204, 32138, 6392, -27246,
        23170, -23171, -23171, 23170, 23170, -23171, -23171, 23170, 18204, -32139, 6392, 27245, -27246, -6393, 32138, -18205,
        12539, -30274, 30273, -12540, -12540, 30273, -30274, 12539, 6392, -18205, 27245, -32139, 32138, -27246, 18204, -6393,
    ];

    /// <summary>The quant table, used for luminance and colour (common/mdec.cpp).</summary>
    private static readonly byte[] QuantTable =
    [
        0x02, 0x10, 0x10, 0x13, 0x10, 0x13, 0x16, 0x16, 0x16, 0x16, 0x16, 0x16, 0x1a, 0x18, 0x1a, 0x1b,
        0x1b, 0x1b, 0x1a, 0x1a, 0x1a, 0x1a, 0x1b, 0x1b, 0x1b, 0x1d, 0x1d, 0x1d, 0x22, 0x22, 0x22, 0x1d,
        0x1d, 0x1d, 0x1b, 0x1b, 0x1d, 0x1d, 0x20, 0x20, 0x22, 0x22, 0x25, 0x26, 0x25, 0x23, 0x23, 0x22,
        0x23, 0x26, 0x26, 0x28, 0x28, 0x28, 0x30, 0x30, 0x2e, 0x2e, 0x38, 0x38, 0x3a, 0x45, 0x45, 0x53,
    ];

    /// <summary>The "heart" block: one 8×8 block of run-length data (heart.h).</summary>
    private static readonly byte[] Heart =
    [
        0x1e, 0x04, 0x74, 0x00, 0xbd, 0x00, 0x93, 0x03, 0x24, 0x00, 0x2b, 0x03, 0x15, 0x00, 0xf9, 0x03, 0xe9, 0x03, 0xda, 0x03,
        0xee, 0x03, 0xfe, 0x03, 0x2b, 0x00, 0x3f, 0x00, 0xc0, 0x03, 0xfe, 0x03, 0xbe, 0x03, 0xfe, 0x03, 0xca, 0x03, 0xfd, 0x03,
        0xf3, 0x03, 0xf3, 0x03, 0xfd, 0x03, 0x07, 0x00, 0xd7, 0x03, 0x00, 0x00, 0xe0, 0x03, 0xf5, 0x03, 0x05, 0x00, 0x02, 0x00,
        0xec, 0x03, 0x07, 0x00, 0x0c, 0x00, 0x08, 0x00, 0xfe, 0x03, 0xf7, 0x03, 0xff, 0x03, 0xf9, 0x03, 0x01, 0x00, 0xdd, 0x03,
        0xd8, 0x03, 0x09, 0x00, 0x16, 0x00, 0x01, 0x00, 0x13, 0x00, 0xca, 0x03, 0x04, 0x00, 0xfa, 0x03, 0xfc, 0x03, 0xfe, 0x03,
        0x00, 0x00, 0x09, 0x00, 0x0f, 0x00, 0x06, 0x00, 0xfe, 0x03, 0xfc, 0x03, 0xf2, 0x03, 0xfa, 0x03, 0xf6, 0x03, 0x08, 0x00,
        0xfa, 0x03, 0x15, 0x00, 0x01, 0x00, 0xf5, 0x03,
    ];

    /// <summary>The heart decoded in unsigned 8-bit mode on a PlayStation (mdec/8bit/psx.log).</summary>
    private static readonly byte[] Heart8Bit = Convert.FromHexString(
        "00FFFF00FFFF0400C9ECEFEDEFFCF200D5DBFAE8FEE8FF00B7F3ECEFEBFFE300" +
        "00FBFFF5F2FF03000005FFFCFF081A000F281EFF052A2300103840293232160F");

    /// <summary>The heart decoded in unsigned 4-bit mode on a PlayStation (mdec/4bit/psx.log).</summary>
    private static readonly byte[] Heart4Bit = Convert.FromHexString(
        "F00FFF00FDFFFF0FEDFFFF0FFBFFFF0EF0FFFF0000FF1F0231F2300241343311");

    private static PsMdec Mdec()
    {
        var mdec = new PsMdec();
        mdec.WriteControl(0x80000000);
        mdec.WriteControl(0x60000000);
        mdec.WriteCommand(0x40000001); // MDEC(2): luminance and colour quant tables
        for (var t = 0; t < 2; t++)
        {
            for (var i = 0; i < 64; i += 4)
            {
                mdec.WriteCommand(BitConverter.ToUInt32(QuantTable, i));
            }
        }

        mdec.WriteCommand(0x60000000); // MDEC(3): scale table
        for (var i = 0; i < 64; i += 2)
        {
            mdec.WriteCommand((ushort)ScaleTable[i] | (uint)(ushort)ScaleTable[i + 1] << 16);
        }

        return mdec;
    }

    /// <summary>MDEC(1) with the given depth (0 = 4 bit, 1 = 8 bit, 2 = 24 bit, 3 = 15 bit) and its data.</summary>
    private static void Decode(PsMdec mdec, int depth, byte[] data, bool signed = false, bool bit15 = false)
    {
        var words = data.Length / 4;
        mdec.WriteCommand(0x20000000u | (uint)depth << 27 | (signed ? 1u << 26 : 0) | (bit15 ? 1u << 25 : 0) | (uint)words);
        for (var i = 0; i < words; i++)
        {
            mdec.WriteCommand(BitConverter.ToUInt32(data, i * 4));
        }
    }

    private static byte[] Read(PsMdec mdec, int words)
    {
        var bytes = new byte[words * 4];
        for (var i = 0; i < words; i++)
        {
            BitConverter.GetBytes(mdec.ReadData()).CopyTo(bytes, i * 4);
        }

        return bytes;
    }

    [Fact]
    public void An_8_bit_block_matches_the_hardware_log_to_within_one()
    {
        var mdec = Mdec();
        Decode(mdec, 1, Heart);
        var output = Read(mdec, 16);

        for (var i = 0; i < 64; i++)
        {
            Math.Abs(output[i] - Heart8Bit[i]).Should().BeLessThanOrEqualTo(1, $"pixel {i}");
        }

        output.Zip(Heart8Bit).Count(p => p.First == p.Second).Should().BeGreaterThanOrEqualTo(56,
            "the IDCT's rounding is tuned to this log; psx-spx documents the exact hardware rounding as unknown");
    }

    [Fact]
    public void A_4_bit_block_matches_the_hardware_log_to_within_one()
    {
        var mdec = Mdec();
        Decode(mdec, 0, Heart);
        var output = Read(mdec, 8);

        for (var i = 0; i < 64; i++)
        {
            var nibble = (output[i / 2] >> (i % 2 * 4)) & 0xF;
            var expected = (Heart4Bit[i / 2] >> (i % 2 * 4)) & 0xF;
            Math.Abs(nibble - expected).Should().BeLessThanOrEqualTo(1, $"pixel {i}");
        }

        output.Zip(Heart4Bit).Count(p => p.First == p.Second).Should().BeGreaterThanOrEqualTo(28);
    }

    /// <summary>A colour macroblock where every block holds only its DC value: Cr, Cb, then Y1–Y4.</summary>
    private static byte[] FlatMacroblock(int cr, int cb, int y)
    {
        var data = new List<byte>();
        foreach (var dc in new[] { cr, cb, y, y, y, y })
        {
            var first = (ushort)(1 << 10 | (dc & 0x3FF)); // q_scale 1
            data.AddRange(BitConverter.GetBytes(first));
            data.AddRange(BitConverter.GetBytes((ushort)0xFE00)); // end of block
        }

        return [.. data];
    }

    [Fact]
    public void A_flat_colour_macroblock_decodes_to_that_colour_at_15_and_24_bits()
    {
        // With quant[0] = 2 a DC-only block decodes to DC / 4: Cr = 40, Cb = −20, Y = 0.
        var data = FlatMacroblock(160, -80, 0);
        const double cr = 40, cb = -20;
        var r = (int)Math.Round(128 + 1.402 * cr);
        var g = (int)Math.Round(128 - 0.3437 * cb - 0.7143 * cr);
        var b = (int)Math.Round(128 + 1.772 * cb);

        var mdec = Mdec();
        Decode(mdec, 2, data);
        var rgb24 = Read(mdec, 192);
        for (var i = 0; i < 256; i++)
        {
            rgb24[i * 3].Should().BeCloseTo((byte)r, 1);
            rgb24[i * 3 + 1].Should().BeCloseTo((byte)g, 1);
            rgb24[i * 3 + 2].Should().BeCloseTo((byte)b, 1);
        }

        Decode(mdec, 3, data, bit15: true);
        var rgb15 = Read(mdec, 128);
        var first = BitConverter.ToUInt16(rgb15, 0);
        (first & 0x8000).Should().NotBe(0);
        (first & 0x1F).Should().Be(rgb24[0] >> 3);
        ((first >> 5) & 0x1F).Should().Be(rgb24[1] >> 3);
        ((first >> 10) & 0x1F).Should().Be(rgb24[2] >> 3);
        for (var i = 0; i < 256; i++)
        {
            BitConverter.ToUInt16(rgb15, i * 2).Should().Be(first, "every pixel of a flat macroblock is the same");
        }

        (mdec.Status & (1u << 31)).Should().NotBe(0, "all output has been read");
    }

    [Fact]
    public void Signed_output_leaves_the_values_centred_on_zero()
    {
        var mdec = Mdec();
        Decode(mdec, 1, FlatMacroblock(0, 0, 0)[8..12], signed: true); // the Y block only, mono
        Read(mdec, 16).Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void The_status_register_follows_a_command()
    {
        var mdec = new PsMdec();
        mdec.WriteControl(0x80000000);
        mdec.Status.Should().Be(0x80040000, "psx-spx: reset sets the status to 80040000h");

        mdec = Mdec();
        mdec.WriteCommand(0x28000000u | (uint)(Heart.Length / 4)); // 8 bit, 32 words
        var status = mdec.Status;
        (status & 0xFFFF).Should().Be(31, "words remaining minus one");
        (status & (1u << 29)).Should().NotBe(0, "busy");
        (status & (1u << 28)).Should().NotBe(0, "DMA0 is enabled and data is wanted");
        mdec.DataInRequest.Should().BeTrue();
        ((status >> 25) & 3).Should().Be(1, "the output depth");

        for (var i = 0; i < Heart.Length / 4; i++)
        {
            mdec.WriteCommand(BitConverter.ToUInt32(Heart, i * 4));
        }

        status = mdec.Status;
        (status & 0xFFFF).Should().Be(0xFFFF, "no words remain");
        (status & (1u << 31)).Should().Be(0, "a block is waiting");
        (status & (1u << 27)).Should().NotBe(0, "DMA1 may read");
        mdec.DataInRequest.Should().BeFalse();
        mdec.DataOutRequest.Should().BeTrue();

        Read(mdec, 16);
        status = mdec.Status;
        (status & (1u << 31)).Should().NotBe(0);
        (status & (1u << 29)).Should().Be(0, "the command has finished");
        mdec.DataOutRequest.Should().BeFalse();
    }

    [Fact]
    public void A_whole_stream_can_arrive_before_any_output_is_read()
    {
        // As with an instant DMA0 transfer: twenty macroblocks are written before DMA1 reads them all.
        var one = FlatMacroblock(160, -80, 40);
        var data = Enumerable.Repeat(one, 20).SelectMany(d => d).ToArray();
        var mdec = Mdec();
        Decode(mdec, 2, data);

        var all = Read(mdec, 192 * 20);
        all.AsSpan(0, 768).SequenceEqual(all.AsSpan(768 * 19, 768)).Should().BeTrue();
        (mdec.Status & (1u << 31)).Should().NotBe(0);
    }

    [Fact]
    public void Save_states_round_trip_mid_command()
    {
        var mdec = Mdec();
        mdec.WriteCommand(0x28000000u | (uint)(Heart.Length / 4));
        for (var i = 0; i < 10; i++)
        {
            mdec.WriteCommand(BitConverter.ToUInt32(Heart, i * 4));
        }

        var writer = new StateWriter();
        mdec.SaveState(writer);
        var copy = new PsMdec();
        copy.LoadState(new StateReader(writer.ToArray()));

        foreach (var m in new[] { mdec, copy })
        {
            for (var i = 10; i < Heart.Length / 4; i++)
            {
                m.WriteCommand(BitConverter.ToUInt32(Heart, i * 4));
            }
        }

        Read(copy, 16).Should().Equal(Read(mdec, 16));
    }
}
