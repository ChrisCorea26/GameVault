using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>Host-to-local uploads in each format, local-to-local copies and local-to-host downloads.</summary>
public sealed class Ps2GsTransferTests
{
    [Theory]
    [InlineData(Ps2Gs.PsmCt32)]
    [InlineData(Ps2Gs.PsmCt24)]
    [InlineData(Ps2Gs.PsmCt16)]
    [InlineData(Ps2Gs.PsmCt16S)]
    [InlineData(Ps2Gs.PsmZ32)]
    [InlineData(Ps2Gs.PsmZ24)]
    [InlineData(Ps2Gs.PsmZ16)]
    [InlineData(Ps2Gs.PsmZ16S)]
    [InlineData(Ps2Gs.PsmT8)]
    [InlineData(Ps2Gs.PsmT4)]
    [InlineData(Ps2Gs.PsmT8H)]
    [InlineData(Ps2Gs.PsmT4HL)]
    [InlineData(Ps2Gs.PsmT4HH)]
    public void Uploads_place_each_pixel_of_the_rectangle(int psm)
    {
        var f = new GsFixture();
        const int w = 37;
        const int h = 9;
        var bits = Ps2Gs.BitsPerPixel(psm);
        var mask = bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1;
        var pixels = Enumerable.Range(0, w * h).Select(i => (uint)(i * 0x01030507) & mask).ToList();
        f.Upload(0x100, 4, psm, 70, 33, w, h, pixels);

        f.Gs.UploadActive.Should().BeFalse("the whole rectangle has arrived");
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                f.Gs.ReadPixel(psm, 0x100, 4, 70 + x, 33 + y).Should().Be(pixels[(y * w) + x], $"pixel ({x}, {y})");
            }
        }
    }

    [Fact]
    public void Psmt8_upload_reads_back_as_psmct32_in_the_hardware_layout()
    {
        // A 16×4 PSMT8 rectangle holding its own column byte offsets reads back as bytes 0..63 in PSMCT32 words.
        var f = new GsFixture();
        var pixels = new List<uint>();
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                pixels.Add((uint)Ps2Gs.PixelAddress(Ps2Gs.PsmT8, 0, 2, x, y));
            }
        }

        f.Upload(0, 2, Ps2Gs.PsmT8, 0, 0, 16, 4, pixels);
        f.Gs.LocalMemory.AsSpan(0, 64).ToArray().Should().Equal(Enumerable.Range(0, 64).Select(i => (byte)i));
    }

    [Fact]
    public void Psmt4_upload_takes_the_low_nibble_first()
    {
        var f = new GsFixture();
        f.Upload(0, 2, Ps2Gs.PsmT4, 0, 0, 32, 1, Enumerable.Range(0, 32).Select(i => (uint)((i & 15) + 1) & 15).ToList());
        f.Gs.LocalMemory[0].Should().Be(0x01, "pixel 0 lands in byte 0's low nibble");
        f.Gs.LocalMemory[1].Should().Be(0x09, "pixel 8 is nibble 2 of the column");
        f.Gs.LocalMemory[4].Should().Be(0x02, "pixel 1 is nibble 8 of the column");
        f.Gs.ReadPixel(Ps2Gs.PsmT4, 0, 2, 17, 0).Should().Be(2);
    }

    [Fact]
    public void Upload_rows_wrap_at_2048()
    {
        var f = new GsFixture();
        f.Upload(0, 32, Ps2Gs.PsmCt32, 2046, 0, 4, 1, [1, 2, 3, 4]);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, 32, 2047, 0).Should().Be(2);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, 32, 0, 0).Should().Be(3);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, 32, 1, 0).Should().Be(4);
    }

    [Fact]
    public void Data_after_the_rectangle_is_ignored()
    {
        var f = new GsFixture();
        f.Upload(0, 1, Ps2Gs.PsmCt32, 0, 0, 2, 1, [7, 8]);
        f.Gs.WriteHwReg(0xDEAD_BEEF_DEAD_BEEF);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 2, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Local_to_local_copies_in_each_direction(int dir)
    {
        var f = new GsFixture();
        var pixels = Enumerable.Range(0, 20 * 10).Select(i => (uint)(i + 1)).ToList();
        f.Upload(0, 4, Ps2Gs.PsmCt32, 0, 0, 20, 10, pixels);
        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 4, Ps2Gs.PsmCt32, 0x400, 2, Ps2Gs.PsmCt32));
        f.W(Ps2Gs.RegTrxPos, GsRegs.TrxPos(0, 0, 5, 3, dir));
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(20, 10));
        f.W(Ps2Gs.RegTrxDir, 2);
        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0x400, 2, 5 + x, 3 + y).Should().Be(pixels[(y * 20) + x]);
            }
        }
    }

    [Fact]
    public void Overlapping_copy_downwards_needs_the_bottom_up_order()
    {
        var f = new GsFixture();
        f.Upload(0, 1, Ps2Gs.PsmCt32, 0, 0, 1, 4, [1, 2, 3, 4]);
        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 1, Ps2Gs.PsmCt32, 0, 1, Ps2Gs.PsmCt32));
        f.W(Ps2Gs.RegTrxPos, GsRegs.TrxPos(0, 0, 0, 1, dir: 1));
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(1, 4));
        f.W(Ps2Gs.RegTrxDir, 2);
        Enumerable.Range(0, 5).Select(y => f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, 1, 0, y)).Should().Equal(1u, 1u, 2u, 3u, 4u);
    }

    [Fact]
    public void Local_to_local_converts_between_layouts_of_the_same_size()
    {
        var f = new GsFixture();
        var pixels = Enumerable.Range(0, 64).Select(i => (uint)(i * 3)).ToList();
        f.Upload(0, 2, Ps2Gs.PsmT8, 0, 0, 16, 4, pixels);
        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 2, Ps2Gs.PsmT8, 0x200, 2, Ps2Gs.PsmT8));
        f.W(Ps2Gs.RegTrxPos, GsRegs.TrxPos(0, 0, 100, 50));
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(16, 4));
        f.W(Ps2Gs.RegTrxDir, 2);
        f.Gs.ReadPixel(Ps2Gs.PsmT8, 0x200, 2, 100 + 7, 50 + 3).Should().Be(pixels[(3 * 16) + 7]);
    }

    [Theory]
    [InlineData(Ps2Gs.PsmCt32)]
    [InlineData(Ps2Gs.PsmCt24)]
    [InlineData(Ps2Gs.PsmCt16)]
    [InlineData(Ps2Gs.PsmT8)]
    [InlineData(Ps2Gs.PsmT4)]
    [InlineData(Ps2Gs.PsmT8H)]
    public void Downloads_return_the_uploaded_stream(int psm)
    {
        var f = new GsFixture();
        const int w = 24;
        const int h = 6;
        var bits = Ps2Gs.BitsPerPixel(psm);
        var mask = bits == 32 ? 0xFFFFFFFFu : (1u << bits) - 1;
        var pixels = Enumerable.Range(0, w * h).Select(i => (uint)(i * 0x0705_0301 + 9) & mask).ToList();
        f.Upload(0x80, 4, psm, 3, 2, w, h, pixels);

        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0x80, 4, psm, 0, 0, 0));
        f.W(Ps2Gs.RegTrxPos, GsRegs.TrxPos(3, 2, 0, 0));
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(w, h));
        f.Gs.WritePrivileged(Ps2Gs.PrivBusDir, 1);
        f.W(Ps2Gs.RegTrxDir, 1);

        var expected = GsFixture.Pack(bits, pixels);
        var buffer = new UInt128[expected.Count / 2 + 4];
        var gif = new Ps2Gif(f.Gs);
        var read = gif.ReadDownload(buffer.AsSpan(0, 3));
        read += gif.ReadDownload(buffer.AsSpan(read));
        read.Should().Be(expected.Count / 2);
        for (var i = 0; i < read; i++)
        {
            ((ulong)buffer[i]).Should().Be(expected[i * 2], $"quadword {i} low");
            ((ulong)(buffer[i] >> 64)).Should().Be(expected[(i * 2) + 1], $"quadword {i} high");
        }

        f.Gs.DownloadQwordsLeft.Should().Be(0);
    }

    [Fact]
    public void Downloads_wait_for_busdir()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 1, Ps2Gs.PsmCt32, 0, 0, 0));
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(4, 1));
        f.W(Ps2Gs.RegTrxDir, 1);
        f.Gs.ReadDownload(new UInt128[1]).Should().Be(0);
        f.Gs.WritePrivileged32(Ps2Gs.PrivBusDir, 1);
        f.Gs.ReadDownload(new UInt128[4]).Should().Be(1);
    }
}
