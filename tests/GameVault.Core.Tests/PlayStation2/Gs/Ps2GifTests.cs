using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>GIFtags in each mode, the three paths' arbitration, PATH3's FIFO and mask, and the GIF's registers.</summary>
public sealed class Ps2GifTests
{
    private readonly GsFixture _f = new();
    private readonly Ps2Gif _gif;

    public Ps2GifTests()
    {
        _gif = new Ps2Gif(_f.Gs);
    }

    internal static UInt128 Tag(int nloop, bool eop, int flg, int nreg, ulong regs, bool pre = false, ulong prim = 0) =>
        new(regs, (ulong)nloop | (eop ? 1UL << 15 : 0) | (pre ? 1UL << 46 : 0) | (prim << 47) | ((ulong)flg << 58) | ((ulong)(nreg & 15) << 60));

    internal static UInt128 Ad(int register, ulong value) => new((ulong)register, value);

    private static UInt128 Q(ulong hi, ulong lo) => new(hi, lo);

    [Fact]
    public void Packed_a_plus_d_writes_any_register()
    {
        _gif.WritePath3([Tag(2, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 0x123456), Ad(Ps2Gs.RegTexA, 0x8000_0000_0080)]).Should().Be(3);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(0x123456);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegTexA).Should().Be(0x8000_0000_0080);
        _gif.Path3Active.Should().BeFalse();
    }

    [Fact]
    public void Packed_prim_rgba_and_xyz2_draw_a_sprite()
    {
        // PRE sets PRIM; registers RGBA, XYZ2, XYZ2.
        var packet = new[]
        {
            Tag(1, true, 0, 3, 0x551, pre: true, prim: Ps2Gs.PrimSprite),
            Q(0x80UL << 32 | 0x33, 0x22UL << 32 | 0x11), // R 0x11, G 0x22, B 0x33, A 0x80
            Q(0, (2UL * 16 << 32) | (2 * 16)),
            Q(0, (6UL * 16 << 32) | (5 * 16)),
        };
        _gif.WritePath3(packet).Should().Be(4);
        _f.Pixel(2, 2).Should().Be(0x8033_2211);
        _f.Pixel(4, 5).Should().Be(0x8033_2211);
        _f.Pixel(5, 5).Should().Be(0);
    }

    [Fact]
    public void Packed_rgba_takes_q_from_the_last_stq_and_a_new_tag_resets_it()
    {
        var q = BitConverter.SingleToUInt32Bits(2.5f);
        _gif.WritePath2([Tag(1, false, 0, 2, 0x12), Q(q, 0x3F80_0000UL << 32), Q(0, 0)]);
        (_f.Gs.ReadGeneralRegister(Ps2Gs.RegRgbaq) >> 32).Should().Be(q);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegSt).Should().Be(0x3F80_0000UL << 32);
        _gif.WritePath2([Tag(1, true, 0, 1, 0x1), Q(0, 0)]);
        (_f.Gs.ReadGeneralRegister(Ps2Gs.RegRgbaq) >> 32).Should().Be(BitConverter.SingleToUInt32Bits(1f));
    }

    [Fact]
    public void Packed_xyzf_uv_and_fog_fields_and_the_adc_bit()
    {
        var x = 0x1234UL;
        var y = 0x0567UL;
        var z = 0xABCDEFUL;
        var fog = 0x9AUL;
        var xyzf = Q((1UL << 47) | (fog << 36) | (z << 4), (y << 32) | x);
        _gif.WritePath3([Tag(1, true, 0, 3, 0xA43), Q(0, (0x2345UL << 32) | 0x1ABC), xyzf, Q(0x77UL << 36, 0)]);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegUv).Should().Be(0x1ABC | (0x2345UL << 16));
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegXyzf3).Should().Be(x | (y << 16) | (z << 32) | (fog << 56), "ADC selects XYZF3");
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegXyzf2).Should().Be(0);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFog).Should().Be(0x77UL << 56);

        _gif.WritePath3([Tag(1, true, 0, 1, 0x5), Q(0xFEDC_BA98, (y << 32) | x)]);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegXyz2).Should().Be(x | (y << 16) | (0xFEDC_BA98UL << 32));
    }

    [Fact]
    public void Reglist_sends_two_registers_per_quadword_and_drops_the_odd_one()
    {
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegPrim, Ps2Gs.PrimSprite)]);
        // RGBAQ, XYZ2, XYZ2: three doublewords, the fourth is padding.
        _gif.WritePath3([
            Tag(1, true, 1, 3, 0x551),
            Q(GsRegs.Xyz(0, 0), GsRegs.Rgbaq(0x40, 0, 0)),
            Q(GsRegs.Rgbaq(0xFF, 0xFF, 0xFF), GsRegs.Xyz(3 * 16, 3 * 16)),
        ]).Should().Be(3);
        _f.Pixel(1, 1).Should().Be(0x8000_0040);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegRgbaq).Should().Be(GsRegs.Rgbaq(0x40, 0, 0), "the padding is discarded");
        _gif.Path3Active.Should().BeFalse();
    }

    [Fact]
    public void Image_mode_feeds_hwreg()
    {
        _gif.WritePath3([
            Tag(4, false, 0, 1, 0xE),
            Ad(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 0, 0, 0, GsFixture.Fbw, Ps2Gs.PsmCt32)),
            Ad(Ps2Gs.RegTrxPos, GsRegs.TrxPos(0, 0, 10, 20)),
            Ad(Ps2Gs.RegTrxReg, GsRegs.TrxReg(4, 2)),
            Ad(Ps2Gs.RegTrxDir, 0),
            Tag(2, true, 2, 0, 0),
            Q(0x4444_4444_3333_3333, 0x2222_2222_1111_1111),
            Q(0x8888_8888_7777_7777, 0x6666_6666_5555_5555),
        ]).Should().Be(8);
        _f.Pixel(10, 20).Should().Be(0x1111_1111);
        _f.Pixel(13, 20).Should().Be(0x4444_4444);
        _f.Pixel(13, 21).Should().Be(0x8888_8888);
    }

    [Fact]
    public void Nloop_zero_tags_only_end_the_packet_on_eop()
    {
        _gif.WritePath2([Tag(0, false, 0, 1, 0xE)]);
        _gif.Path2Active.Should().BeTrue();
        _gif.WritePath2([Tag(1, false, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 5), Tag(0, true, 0, 0, 0)]);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(5);
        _gif.Path2Active.Should().BeFalse();
    }

    [Fact]
    public void A_path_keeps_the_gif_until_its_packet_ends()
    {
        _gif.WritePath3([Tag(2, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 1)]).Should().Be(2);
        _gif.Path3Active.Should().BeTrue();
        _gif.ActivePath.Should().Be(3);
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 2)]).Should().Be(0, "PATH3 holds the GIF");
        _gif.WritePath3([Ad(Ps2Gs.RegTexA, 3)]).Should().Be(1);
        _gif.ActivePath.Should().Be(0);
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 2)]).Should().Be(2);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(2);
    }

    [Fact]
    public void Path3_waits_in_its_fifo_while_another_path_holds_the_gif()
    {
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE)]);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegTexA, 9)]).Should().Be(2, "into the FIFO");
        _gif.Path3FifoCount.Should().Be(2);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegTexA).Should().Be(0);
        _gif.WritePath2([Ad(Ps2Gs.RegFogCol, 4)]);
        _gif.Path3FifoCount.Should().Be(0, "the FIFO drains when PATH2's packet ends");
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegTexA).Should().Be(9);
    }

    [Fact]
    public void Masked_path3_fills_its_fifo_and_drains_when_unmasked()
    {
        _gif.Path3Masked = true;
        var data = new List<UInt128> { Tag(19, true, 0, 1, 0xE) };
        for (var i = 0; i < 19; i++)
        {
            data.Add(Ad(Ps2Gs.RegFogCol, (ulong)i + 1));
        }

        _gif.WritePath3(data.ToArray()).Should().Be(Ps2Gif.Path3FifoSize);
        _gif.Path3FifoCount.Should().Be(16);
        _gif.Path3Active.Should().BeFalse("a masked PATH3 doesn't hold up FLUSHA");
        (_gif.ReadRegister(Ps2Gif.RegStat) >> 24).Should().Be(16u, "GIF_STAT.FQC");
        ((_gif.ReadRegister(Ps2Gif.RegStat) >> 1) & 1).Should().Be(1u, "GIF_STAT.M3P");
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(0);

        _gif.Path3Masked = false;
        _gif.Path3FifoCount.Should().Be(0);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(15);
        _gif.WritePath3(data.Skip(16).ToArray()).Should().Be(4);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(19);
    }

    [Fact]
    public void The_mask_waits_for_the_end_of_a_packet()
    {
        _gif.WritePath3([Tag(2, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 1)]);
        _gif.Path3Masked = true;
        _gif.WritePath3([Ad(Ps2Gs.RegFogCol, 2)]).Should().Be(1);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(2);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 3)]);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(2, "the next packet waits");
    }

    [Fact]
    public void Gif_mode_m3r_masks_path3_too()
    {
        _gif.WriteRegister(Ps2Gif.RegMode, 1);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 1)]);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(0);
        (_gif.ReadRegister(Ps2Gif.RegStat) & 1).Should().Be(1u);
        _gif.WriteRegister(Ps2Gif.RegMode, 0);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(1);
    }

    [Fact]
    public void Path1_waits_for_the_path_holding_the_gif()
    {
        var vu1 = new byte[16 * 1024];
        WriteQword(vu1, 1023, Tag(1, true, 0, 1, 0xE)); // wraps to the start of VU memory
        WriteQword(vu1, 0, Ad(Ps2Gs.RegTexA, 0x55));

        _gif.WritePath2([Tag(2, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 1)]);
        _gif.KickPath1(vu1, 1023);
        _gif.Path1Queued.Should().BeTrue();
        ((_gif.ReadRegister(Ps2Gif.RegStat) >> 8) & 1).Should().Be(1u, "GIF_STAT.P1Q");
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegTexA).Should().Be(0);
        _gif.WritePath2([Ad(Ps2Gs.RegFogCol, 2)]);
        _gif.Path1Queued.Should().BeFalse();
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegTexA).Should().Be(0x55);

        _gif.KickPath1(vu1, 1023);
        _gif.Path1Queued.Should().BeFalse("an idle GIF runs PATH1 at once");
    }

    [Fact]
    public void Intermittent_mode_lets_other_paths_in_between_image_slices()
    {
        var image = new List<UInt128> { Tag(16, true, 2, 0, 0) };
        image.AddRange(Enumerable.Range(0, 16).Select(i => (UInt128)(ulong)i));
        _gif.WriteRegister(Ps2Gif.RegMode, 4);
        _gif.WritePath3(image.ToArray()).Should().Be(9, "the tag and the first 8-quadword slice; the DMA retries the rest");
        _gif.ActivePath.Should().Be(0);
        ((_gif.ReadRegister(Ps2Gif.RegStat) >> 5) & 1).Should().Be(1u, "GIF_STAT.IP3: PATH3 is interrupted");
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegFogCol, 7)]).Should().Be(2);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(7);
        _gif.Path3Active.Should().BeTrue();
        _gif.WritePath3(image.Skip(9).ToArray()).Should().Be(8);
        _gif.Path3Active.Should().BeFalse();

        _gif.WriteRegister(Ps2Gif.RegMode, 0);
        _gif.WritePath3(image.ToArray()).Should().Be(17);
        _gif.Path3FifoCount.Should().Be(0, "without IMT PATH3 keeps the GIF to the end of its packet");
    }

    [Fact]
    public void Gif_ctrl_pauses_and_resets()
    {
        _gif.WriteRegister(Ps2Gif.RegCtrl, 8);
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE)]).Should().Be(0);
        ((_gif.ReadRegister(Ps2Gif.RegStat) >> 3) & 1).Should().Be(1u);
        _gif.WriteRegister(Ps2Gif.RegCtrl, 0);
        _gif.WritePath2([Tag(2, false, 0, 1, 0xE)]).Should().Be(1);
        _gif.ReadRegister(Ps2Gif.RegTag0).Should().Be(0x2u);
        _gif.ReadRegister(Ps2Gif.RegTag2).Should().Be(0xEu);
        (_gif.ReadRegister(Ps2Gif.RegStat) >> 10 & 3).Should().Be(2u, "GIF_STAT.APATH");
        _gif.ReadRegister(Ps2Gif.RegCnt).Should().Be(2u);
        _gif.WriteRegister(Ps2Gif.RegCtrl, 1);
        _gif.Path2Active.Should().BeFalse();
        _gif.ActivePath.Should().Be(0);
    }

    // ---- SIGNAL, FINISH and LABEL through the GIF -------------------------------------------------------------

    [Fact]
    public void Signal_sets_masked_id_bits_as_ps2autotests_expects()
    {
        // ps2autotests gs/signal.expected.
        _f.Gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrSignal);
        _f.Gs.WritePrivileged(Ps2Gs.PrivSigLblId, 0x7654_3210_0000_0000);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegSignal, 0xF0 | (0xF0UL << 32))]);
        (_f.Gs.ReadPrivileged(Ps2Gs.PrivCsr) & Ps2Gs.CsrSignal).Should().Be(1);
        _f.Gs.ReadPrivileged(Ps2Gs.PrivSigLblId).Should().Be(0x7654_3210_0000_00F0);

        _f.Gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrSignal);
        _f.Gs.WritePrivileged(Ps2Gs.PrivSigLblId, 0x3333_3333_3333_3333);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegSignal, 0x00 | (0xF0UL << 32))]);
        (_f.Gs.ReadPrivileged(Ps2Gs.PrivCsr) & Ps2Gs.CsrSignal).Should().Be(1);
        _f.Gs.ReadPrivileged(Ps2Gs.PrivSigLblId).Should().Be(0x3333_3333_3333_3303);
    }

    [Fact]
    public void Label_sets_masked_bits_of_lblid_as_ps2autotests_expects()
    {
        // ps2autotests gs/label.expected.
        _f.Gs.WritePrivileged(Ps2Gs.PrivSigLblId, 0x0000_0000_7654_3210);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegLabel, 0xF0 | (0xF0UL << 32))]);
        _f.Gs.ReadPrivileged(Ps2Gs.PrivSigLblId).Should().Be(0x0000_00F0_7654_3210);

        _f.Gs.WritePrivileged(Ps2Gs.PrivSigLblId, 0x3333_3333_3333_3333);
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegLabel, 0x00 | (0xF0UL << 32))]);
        _f.Gs.ReadPrivileged(Ps2Gs.PrivSigLblId).Should().Be(0x3333_3303_3333_3333);
    }

    [Fact]
    public void A_second_signal_stalls_the_gif_until_csr_signal_is_cleared()
    {
        var packet = new[]
        {
            Tag(3, true, 0, 1, 0xE),
            Ad(Ps2Gs.RegSignal, 1 | (0xFFUL << 32)),
            Ad(Ps2Gs.RegSignal, 2 | (0xFFUL << 32)),
            Ad(Ps2Gs.RegFogCol, 0x42),
        };
        _gif.WritePath3(packet).Should().Be(4, "the rest waits in the FIFO");
        _f.Gs.Stalled.Should().BeTrue();
        _f.Gs.SigLblId.Should().Be(1);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(0);
        _gif.WritePath2([Tag(1, true, 0, 1, 0xE)]).Should().Be(0);

        _f.Gs.WritePrivileged32(Ps2Gs.PrivCsr, (uint)Ps2Gs.CsrSignal);
        _f.Gs.Stalled.Should().BeFalse();
        _f.Gs.SigLblId.Should().Be(2, "the held SIGNAL takes effect");
        (_f.Gs.Csr & Ps2Gs.CsrSignal).Should().Be(1);
        _f.Gs.ReadGeneralRegister(Ps2Gs.RegFogCol).Should().Be(0x42, "the GIF carries on");
    }

    // ---- Save states ------------------------------------------------------------------------------------------

    [Fact]
    public void Save_state_round_trips_a_packet_in_progress()
    {
        _gif.Path3Masked = false;
        _gif.WritePath2([Tag(2, true, 0, 2, 0x12), Q(BitConverter.SingleToUInt32Bits(3f), 0)]);
        _gif.WriteRegister(Ps2Gif.RegMode, 4);
        _gif.Path3Masked = true;
        _gif.WritePath3([Tag(1, true, 0, 1, 0xE), Ad(Ps2Gs.RegTexA, 1)]);

        var writer = new StateWriter();
        _gif.SaveState(writer);
        _f.Gs.SaveState(writer);
        var saved = writer.ToArray();

        _gif.WritePath2([Q(0, 0x10), Q(0, 0), Q(0, 0x20)]);
        var expectedRgbaq = _f.Gs.ReadGeneralRegister(Ps2Gs.RegRgbaq);
        var expectedStat = _gif.ReadRegister(Ps2Gif.RegStat);

        var gs = new Ps2Gs();
        var gif = new Ps2Gif(gs);
        var reader = new StateReader(saved);
        gif.LoadState(reader);
        gs.LoadState(reader);
        reader.IsAtEnd.Should().BeTrue();
        gif.Path2Active.Should().BeTrue();
        gif.Path3FifoCount.Should().Be(2);
        gif.WritePath2([Q(0, 0x10), Q(0, 0), Q(0, 0x20)]);
        gs.ReadGeneralRegister(Ps2Gs.RegRgbaq).Should().Be(expectedRgbaq);
        gif.ReadRegister(Ps2Gif.RegStat).Should().Be(expectedStat);
    }

    private static void WriteQword(byte[] memory, int index, UInt128 value)
    {
        BitConverter.TryWriteBytes(memory.AsSpan(index * 16), (ulong)value);
        BitConverter.TryWriteBytes(memory.AsSpan((index * 16) + 8), (ulong)(value >> 64));
    }
}
