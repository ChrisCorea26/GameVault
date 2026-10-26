using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Dma;

/// <summary>
/// VIF0/VIF1 (ps2tek: "VIF Commands", "VIF UNPACK"). Expected values are hand-computed from ps2tek, or copied from
/// ps2autotests' dma/vif .expected files (hardware) where marked, so they hold without the downloads too.
/// </summary>
public sealed class Ps2VifTests
{
    private const uint Stat = 0x00;
    private const uint Fbrst = 0x10;
    private const uint Err = 0x20;
    private const uint Mark = 0x30;
    private const uint Num = 0x60;
    private const uint Code = 0x80;
    private const uint Itops = 0x90;
    private const uint Base = 0xA0;
    private const uint Ofst = 0xB0;
    private const uint Tops = 0xC0;
    private const uint Itop = 0xD0;
    private const uint Top = 0xE0;

    private const uint I = 0x8000_0000;

    private static uint Op(uint cmd, uint imm = 0, uint num = 0) => (cmd << 24) | (num << 16) | imm;

    private static uint Unpack(uint format, uint num, uint address, bool mask = false, bool unsigned = false, bool flg = false) =>
        Op(0x60 | format | (mask ? 0x10u : 0), address | (unsigned ? 0x4000u : 0) | (flg ? 0x8000u : 0), num & 0xFF);

    /// <summary>Feeds words (padded with NOPs to a quadword) into the VIF's FIFO; returns the quadwords taken.</summary>
    private static int Feed(Ps2Vif vif, params uint[] words)
    {
        var count = (words.Length + 3) / 4;
        var data = new UInt128[count];
        for (var i = 0; i < words.Length; i++)
        {
            data[i / 4] |= (UInt128)words[i] << (32 * (i % 4));
        }

        return vif.Write(data);
    }

    private static (Ps2Vif Vif, FakeVu Vu, Ps2Intc Intc, FakeGif Gif) NewVif(int index)
    {
        var vu = new FakeVu(index);
        var intc = new Ps2Intc();
        var gif = new FakeGif();
        return (new Ps2Vif(index, vu, intc, index == 1 ? gif : null), vu, intc, gif);
    }

    // ---- UNPACK ----

    // From ps2autotests dma/vif/unpack.expected (hardware): one vector of each format, sign- and zero-extended.
    [Theory]
    [InlineData(0x2u, false, new uint[] { 0x88 }, new uint[] { 0xffffff88, 0xffffff88, 0xffffff88, 0xffffff88 })]
    [InlineData(0x2u, true, new uint[] { 0x88 }, new uint[] { 0x88, 0x88, 0x88, 0x88 })]
    [InlineData(0x1u, false, new uint[] { 0x8888 }, new uint[] { 0xffff8888, 0xffff8888, 0xffff8888, 0xffff8888 })]
    [InlineData(0x1u, true, new uint[] { 0x8888 }, new uint[] { 0x8888, 0x8888, 0x8888, 0x8888 })]
    [InlineData(0x0u, false, new uint[] { 0x88888888 }, new uint[] { 0x88888888, 0x88888888, 0x88888888, 0x88888888 })]
    [InlineData(0x6u, false, new uint[] { 0x9988 }, new uint[] { 0xffffff88, 0xffffff99, 0xffffff88, 0xffffff99 })]
    [InlineData(0x5u, true, new uint[] { 0x99998888 }, new uint[] { 0x8888, 0x9999, 0x8888, 0x9999 })]
    [InlineData(0x4u, false, new uint[] { 0x88888888, 0x99999999 }, new uint[] { 0x88888888, 0x99999999, 0x88888888, 0x99999999 })]
    [InlineData(0xAu, false, new uint[] { 0xAA9988 }, new uint[] { 0xffffff88, 0xffffff99, 0xffffffaa, 0 })]
    [InlineData(0x9u, true, new uint[] { 0x99998888, 0xAAAA }, new uint[] { 0x8888, 0x9999, 0xaaaa, 0 })]
    [InlineData(0x8u, false, new uint[] { 0x88888888, 0x99999999, 0xAAAAAAAA }, new uint[] { 0x88888888, 0x99999999, 0xaaaaaaaa, 0 })]
    [InlineData(0xEu, false, new uint[] { 0xBBAA9988 }, new uint[] { 0xffffff88, 0xffffff99, 0xffffffaa, 0xffffffbb })]
    [InlineData(0xDu, false, new uint[] { 0x99998888, 0xBBBBAAAA }, new uint[] { 0xffff8888, 0xffff9999, 0xffffaaaa, 0xffffbbbb })]
    [InlineData(0xCu, false, new uint[] { 0x88888888, 0x99999999, 0xAAAAAAAA, 0xBBBBBBBB }, new uint[] { 0x88888888, 0x99999999, 0xaaaaaaaa, 0xbbbbbbbb })]
    [InlineData(0xFu, false, new uint[] { 0xFFFF }, new uint[] { 0xf8, 0xf8, 0xf8, 0x80 })]
    public void UnpackDecodesEveryFormat(uint format, bool unsigned, uint[] data, uint[] expected)
    {
        foreach (var index in new[] { 0, 1 })
        {
            var (vif, vu, _, _) = NewVif(index);
            vu.DataMemory.AsSpan(0, 16).Fill(0xFF);
            var words = new List<uint> { Op(0x01, 0x0404), Unpack(format, 1, 0, unsigned: unsigned) };
            words.AddRange(data);
            while (words.Count < 6)
            {
                words.Add(0);
            }

            Feed(vif, [.. words]);
            vu.Vector(0).Should().Equal(expected);
        }
    }

    [Fact]
    public void V45UnpacksEachFieldToTheTopOfAByte()
    {
        var (vif, vu, _, _) = NewVif(0);
        // R = 1, G = 2, B = 3, A = 1, then R = 31, G = 0, B = 16, A = 0.
        var first = 1u | (2u << 5) | (3u << 10) | (1u << 15);
        var second = 31u | (16u << 10);
        Feed(vif, Op(0x01, 0x0404), Unpack(0xF, 2, 4), first | (second << 16), 0);
        vu.Vector(4).Should().Equal(8u, 16u, 24u, 128u);
        vu.Vector(5).Should().Equal(248u, 0u, 128u, 0u);
    }

    [Fact]
    public void S8DataEndsOnAWordBoundary()
    {
        var (vif, vu, _, _) = NewVif(0);
        // Five bytes of S-8 take two words; the MARK after them is the next command.
        Feed(vif, Op(0x01, 0x0404), Unpack(0x2, 5, 0), 0x04030201, 0x00000005, Op(0x07, 0x77), 0, 0, 0);
        vu.Vector(4).Should().Equal(5u, 5u, 5u, 5u);
        vif.ReadRegister(Mark).Should().Be(0x77);
    }

    [Fact]
    public void UnpackWaitsForItsDataAcrossWrites()
    {
        var (vif, vu, _, _) = NewVif(1);
        Feed(vif, 0, 0, Op(0x01, 0x0404), Unpack(0xC, 2, 0x10));
        (vif.ReadRegister(Stat) & 3).Should().Be(1, "VPS: waiting for data");
        vif.ReadRegister(Num).Should().Be(2);
        Feed(vif, 1, 2, 3, 4);
        vu.Vector(0x10).Should().Equal(1u, 2u, 3u, 4u);
        vif.ReadRegister(Num).Should().Be(1);
        Feed(vif, 5, 6, 7, 8);
        vu.Vector(0x11).Should().Equal(5u, 6u, 7u, 8u);
        (vif.ReadRegister(Stat) & 3).Should().Be(0);
        vif.ReadRegister(Code).Should().Be(Unpack(0xC, 2, 0x10));
    }

    // From ps2autotests dma/vif/stcycl.expected (hardware): UNPACK V4-32 ×8 with every field masked to the column
    // registers (1, 2, 3, 4), shown as the x of the first 32 quadwords.
    [Theory]
    [InlineData(2u, 1u, "10101010101010100000000000000000")]
    [InlineData(5u, 4u, "12340123400000000000000000000000")]
    [InlineData(0u, 3u, "12312312000000000000000000000000")]
    [InlineData(1u, 0u, "12344444000000000000000000000000")]
    [InlineData(7u, 6u, "12344401200000000000000000000000")]
    public void CycleSkippingAndFillingMatchesTheHardware(uint cl, uint wl, string expected)
    {
        var (vif, vu, _, _) = NewVif(0);
        var words = new List<uint>
        {
            0, Op(0x01, (wl << 8) | cl), Op(0x20), 0xAAAAAAAA,
            0, 0, 0, Op(0x31), 1, 2, 3, 4,
            0, 0, 0, Unpack(0xC, 8, 0, mask: true),
        };
        for (var i = 0u; i < 32; i++)
        {
            words.Add(i);
        }

        Feed(vif, [.. words]);
        var shown = string.Concat(Enumerable.Range(0, 32).Select(q => vu.Word(q * 16).ToString("x")));
        shown.Should().Be(expected);
    }

    [Fact]
    public void FillingWritesWithTheRowMaskWriteTheRowRegisters()
    {
        var (vif, vu, _, _) = NewVif(0);
        // CL 1, WL 2: one vector read, then one filled, per cycle. MASK rows 1-3 = row; row 0 = data.
        Feed(vif, Op(0x30), 0x10, 0x20, 0x30, 0x40, Op(0x20), 0x55555500, Op(0x01, 0x0201),
            Unpack(0x0, 4, 0, mask: true), 7, 8, 0);
        vu.Vector(0).Should().Equal(7u, 7u, 7u, 7u);
        vu.Vector(1).Should().Equal(0x10u, 0x20u, 0x30u, 0x40u);
        vu.Vector(2).Should().Equal(8u, 8u, 8u, 8u);
        vu.Vector(3).Should().Equal(0x10u, 0x20u, 0x30u, 0x40u);
    }

    // From ps2autotests dma/vif/stmod.expected (hardware): difference mode with the diagonal masked to the row.
    [Fact]
    public void DifferenceModeAccumulatesIntoTheRow()
    {
        var (vif, vu, _, _) = NewVif(0);
        var words = new List<uint>
        {
            0, 0, Op(0x01, 0x0404), Op(0x30), 0x1000, 0x1000, 0x1000, 0x1000,
            Op(0x20), 0x40100401, Op(0x05, 2), Unpack(0xC, 4, 0, mask: true),
        };
        for (var i = 0u; i < 16; i++)
        {
            words.Add(i);
        }

        Feed(vif, [.. words]);
        vu.Vector(0).Should().Equal(0x1000u, 0x1001u, 0x1002u, 0x1003u);
        vu.Vector(1).Should().Equal(0x1004u, 0x1001u, 0x1008u, 0x100au);
        vu.Vector(2).Should().Equal(0x100cu, 0x100au, 0x1008u, 0x1015u);
        vu.Vector(3).Should().Equal(0x1018u, 0x1017u, 0x1016u, 0x1015u);
        new[] { vif.ReadRegister(0x100), vif.ReadRegister(0x110), vif.ReadRegister(0x120), vif.ReadRegister(0x130) }
            .Should().Equal(0x1018u, 0x1017u, 0x1016u, 0x1015u);
    }

    [Fact]
    public void OffsetModeAddsTheRowAndMaskedFieldsCanBeSkipped()
    {
        var (vif, vu, _, _) = NewVif(0);
        vu.DataMemory.AsSpan(0, 16).Fill(0xEE);
        Feed(vif, Op(0x01, 0x0404), Op(0x30), 100, 200, 300, 400, Op(0x05, 1), Op(0x20), 0b11_10_00_00,
            Op(0x31), 9, 0, 0, 0, Unpack(0xC, 1, 0, mask: true), 1, 2, 3, 4);
        vu.Vector(0).Should().Equal(101u, 202u, 9u, 0xEEEEEEEEu);
    }

    [Fact]
    public void UnpackAddressesWrapAroundDataMemory()
    {
        var (vif, vu, _, _) = NewVif(0);
        Feed(vif, Op(0x01, 0x0404), Unpack(0x0, 2, 0xFF), 1, 2);
        vu.Vector(0xFF).Should().Equal(1u, 1u, 1u, 1u);
        vu.Vector(0).Should().Equal(2u, 2u, 2u, 2u);
    }

    // ---- Double buffering, ITOP, MSCAL ----

    [Fact]
    public void FlgAddsTopsAndMscalSwapsTheDoubleBuffer()
    {
        var (vif, vu, _, _) = NewVif(1);
        Feed(vif, Op(0x03, 0x10), Op(0x02, 0x20), Op(0x04, 0x123), Op(0x01, 0x0404));
        vif.ReadRegister(Base).Should().Be(0x10);
        vif.ReadRegister(Ofst).Should().Be(0x20);
        vif.ReadRegister(Tops).Should().Be(0x10, "OFFSET sets TOPS to BASE");
        vif.ReadRegister(Itops).Should().Be(0x123);

        Feed(vif, Unpack(0x0, 1, 1, flg: true), 0xAB, 0, 0);
        vu.Vector(0x11).Should().Equal(0xABu, 0xABu, 0xABu, 0xABu);

        Feed(vif, Op(0x14, 0x40));
        vu.Starts.Should().Equal(0x200u);
        vif.Top.Should().Be(0x10);
        vif.Itop.Should().Be(0x123);
        vif.ReadRegister(Itop).Should().Be(0x123);
        vif.ReadRegister(Tops).Should().Be(0x30, "TOPS = BASE + OFST after the first swap");
        (vif.ReadRegister(Stat) & 0x80).Should().Be(0x80, "DBF");

        Feed(vif, Op(0x17));
        vu.Continues.Should().Be(1);
        vif.ReadRegister(Top).Should().Be(0x30);
        vif.ReadRegister(Tops).Should().Be(0x10);
        (vif.ReadRegister(Stat) & 0x80).Should().Be(0);
    }

    [Fact]
    public void MscalWaitsForTheRunningMicroProgram()
    {
        var (vif, vu, _, _) = NewVif(0);
        vu.Running = true;
        Feed(vif, Op(0x14, 0x10), Op(0x07, 0x55));
        vu.Starts.Should().BeEmpty();
        vif.Stalled.Should().BeTrue();
        (vif.ReadRegister(Stat) & 0x4).Should().Be(0x4, "VEW: the VU is running");
        vif.ReadRegister(Mark).Should().Be(0);

        vu.Running = false;
        vif.Resume();
        vu.Starts.Should().Equal(0x80u);
        vif.ReadRegister(Mark).Should().Be(0x55);
        vif.Stalled.Should().BeFalse();
    }

    [Fact]
    public void FlusheHoldsTheFollowingCommandsUntilTheVuStops()
    {
        var (vif, vu, _, _) = NewVif(1);
        vu.Running = true;
        Feed(vif, Op(0x10), Op(0x01, 0x0404), Unpack(0x0, 1, 0), 0x42);
        vu.Vector(0).Should().Equal(0u, 0u, 0u, 0u);
        vu.Running = false;
        vif.Resume();
        vu.Vector(0).Should().Equal(0x42u, 0x42u, 0x42u, 0x42u);
    }

    [Fact]
    public void MpgLoadsMicroMemoryOnceTheVuStops()
    {
        var (vif, vu, _, _) = NewVif(1);
        vu.Running = true;
        Feed(vif, 0, Op(0x4A, 2, 2), 0x11111111, 0x22222222, 0x33333333, 0x44444444, 0, 0);
        vu.MicroMemory.AsSpan(16, 16).ToArray().Should().OnlyContain(b => b == 0);
        vu.Running = false;
        vif.Resume();
        BitConverter.ToUInt32(vu.MicroMemory, 16).Should().Be(0x11111111);
        BitConverter.ToUInt32(vu.MicroMemory, 28).Should().Be(0x44444444);
        vif.ReadRegister(Num).Should().Be(0);
    }

    // ---- The I bit, MARK, errors, FBRST ----

    [Fact]
    public void TheIBitRaisesTheInterruptAndStallsBeforeTheNextCommand()
    {
        var (vif, _, intc, _) = NewVif(1);
        Feed(vif, Op(0x00) | I, Op(0x07, 0x99), 0, 0);
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Vif1);
        (vif.ReadRegister(Stat) & 0x00FFFFFF).Should().Be(0xC02, "VIS, INT, VPS = decoding (ps2autotests: intstall)");
        vif.ReadRegister(Mark).Should().Be(0);

        vif.WriteRegister(Fbrst, 8); // STC
        vif.ReadRegister(Mark).Should().Be(0x99);
        (vif.ReadRegister(Stat) & 0xC00).Should().Be(0);
    }

    [Fact]
    public void MarkWithTheIBitDoesNotStall()
    {
        var (vif, _, intc, _) = NewVif(0);
        Feed(vif, Op(0x07, 0xBEEF) | I, Op(0x20), 0xCCCCCCCC, 0);
        vif.ReadRegister(0x70).Should().Be(0xCCCCCCCC);
        (vif.ReadRegister(Stat) & 0x00FFFFFF).Should().Be(0x840, "INT and MRK (ps2autotests: intstall)");
        intc.Stat.Should().Be(1u << (int)Ps2Interrupt.Vif0);
        vif.WriteRegister(Mark, 0x1234);
        vif.ReadRegister(Mark).Should().Be(0x1234);
        (vif.ReadRegister(Stat) & 0x40).Should().Be(0, "writing MARK clears MRK");
    }

    [Fact]
    public void ErrMiiDisablesTheIBit()
    {
        var (vif, _, intc, _) = NewVif(0);
        vif.WriteRegister(Err, 1);
        Feed(vif, Op(0x00) | I, Op(0x07, 0x99), 0, 0);
        intc.Stat.Should().Be(0);
        vif.ReadRegister(Mark).Should().Be(0x99);
    }

    [Fact]
    public void AnUnknownCommandSetsEr1AndStallsUnlessMe1IsSet()
    {
        var (vif, _, _, _) = NewVif(0);
        Feed(vif, Op(0x7B), Op(0x07, 1), 0, 0);
        (vif.ReadRegister(Stat) & 0x2000).Should().Be(0x2000);
        vif.ReadRegister(Mark).Should().Be(0);
        vif.WriteRegister(Fbrst, 8);
        vif.ReadRegister(Mark).Should().Be(1);

        vif.WriteRegister(Err, 4);
        Feed(vif, Op(0x50, 1), Op(0x07, 2), 0, 0); // DIRECT is VIF1-only
        (vif.ReadRegister(Stat) & 0x2000).Should().Be(0);
        vif.ReadRegister(Mark).Should().Be(2);
    }

    [Fact]
    public void FbrstForceBreakStopAndReset()
    {
        var (vif, _, _, _) = NewVif(1);
        vif.WriteRegister(Fbrst, 2); // force break
        Feed(vif, Op(0x07, 1), 0, 0, 0);
        vif.ReadRegister(Mark).Should().Be(0);
        (vif.ReadRegister(Stat) & 0x200).Should().Be(0x200);
        vif.WriteRegister(Fbrst, 8);
        vif.ReadRegister(Mark).Should().Be(1);

        vif.WriteRegister(Fbrst, 4); // stop
        Feed(vif, Op(0x07, 2), 0, 0, 0);
        (vif.ReadRegister(Stat) & 0x100).Should().Be(0x100);
        vif.ReadRegister(Mark).Should().Be(1);

        vif.WriteRegister(Fbrst, 1); // reset: the FIFO and STAT go
        vif.ReadRegister(Stat).Should().Be(0);
        vif.FifoCount.Should().Be(0);
        Feed(vif, Op(0x07, 3), 0, 0, 0);
        vif.ReadRegister(Mark).Should().Be(3);
    }

    [Fact]
    public void TheFifoFillsWhileTheVifWaits()
    {
        var (vif, vu, _, _) = NewVif(1);
        vu.Running = true;
        var data = new UInt128[20];
        data[0] = Op(0x14, 0);
        vif.Write(data).Should().Be(16, "VIF1's FIFO holds 16 quadwords");
        (vif.ReadRegister(Stat) >> 24).Should().Be(16);
        vu.Running = false;
        vif.Write(data.AsSpan(16)).Should().Be(4);
        vif.FifoCount.Should().Be(0);
    }

    [Fact]
    public void ATagsUpperHalfRunsAsTwoVifcodes()
    {
        var (vif, _, _, _) = NewVif(0);
        var tag = ((UInt128)Op(0x07, 0x4321) << 96) | ((UInt128)Op(0x01, 0x0102) << 64) | 0x10000001;
        vif.WriteTag(tag).Should().BeTrue();
        vif.ReadRegister(0x40).Should().Be(0x0102);
        vif.ReadRegister(Mark).Should().Be(0x4321);
    }

    // ---- VIF1 and the GIF ----

    [Fact]
    public void DirectSendsQuadwordsToPath2AndWaitsWhenTheGifIsBusy()
    {
        var (vif, _, _, gif) = NewVif(1);
        gif.Path2Room = 1;
        Feed(vif, 0, 0, 0, Op(0x50, 2), 0xA, 0, 0, 0, 0xB, 0, 0, 0, Op(0x07, 5), 0, 0, 0);
        gif.Path2.Should().Equal((UInt128)0xA);
        (vif.ReadRegister(Stat) & 0x8).Should().Be(0x8, "VGW");
        vif.ReadRegister(Mark).Should().Be(0);

        gif.Path2Room = int.MaxValue;
        vif.Resume();
        gif.Path2.Should().Equal((UInt128)0xA, (UInt128)0xB);
        vif.ReadRegister(Mark).Should().Be(5);
    }

    [Fact]
    public void DirecthlWaitsForPath3AndFlushaToo()
    {
        var (vif, vu, _, gif) = NewVif(1);
        gif.Path3Active = true;
        Feed(vif, 0, 0, 0, Op(0x51, 1), 0xC, 0, 0, 0);
        gif.Path2.Should().BeEmpty();
        gif.Path3Active = false;
        vif.Resume();
        gif.Path2.Should().Equal((UInt128)0xC);

        gif.Path3Active = true;
        Feed(vif, Op(0x13), Op(0x07, 9), 0, 0);
        vif.ReadRegister(Mark).Should().Be(0);
        gif.Path3Active = false;
        vu.Running = true;
        vif.Resume();
        vif.ReadRegister(Mark).Should().Be(0, "FLUSHA also waits for the VU");
        vu.Running = false;
        vif.Resume();
        vif.ReadRegister(Mark).Should().Be(9);
    }

    [Fact]
    public void Mskpath3SetsTheGifsMask()
    {
        var (vif, _, _, gif) = NewVif(1);
        Feed(vif, Op(0x06, 0x8000), 0, 0, 0);
        gif.Path3Masked.Should().BeTrue();
        Feed(vif, Op(0x06, 0), 0, 0, 0);
        gif.Path3Masked.Should().BeFalse();
    }

    [Fact]
    public void Vif1ReadsDownloadsWhenFdrIsSet()
    {
        var (vif, _, _, gif) = NewVif(1);
        gif.Download.Enqueue(7);
        gif.Download.Enqueue(8);
        var buffer = new UInt128[4];
        vif.Read(buffer).Should().Be(0);
        vif.WriteRegister(Stat, 1u << 23);
        (vif.ReadRegister(Stat) & (1u << 23)).Should().NotBe(0);
        vif.Read(buffer).Should().Be(2);
        buffer[1].Should().Be((UInt128)8);
    }

    // ---- Save states ----

    [Fact]
    public void AVifRoundTripsThroughASaveStateMidUnpack()
    {
        var (vif, vu, _, _) = NewVif(1);
        Feed(vif, Op(0x03, 0x8), Op(0x02, 0x4), Op(0x30), 1, 2, 3, 4, Op(0x05, 1), Op(0x01, 0x0102),
            Unpack(0x5, 6, 0, flg: true), 0x00020001, 0x00040003);
        var writer = new StateWriter();
        vif.SaveState(writer);

        var (copy, copyVu, _, _) = NewVif(1);
        copy.LoadState(new StateReader(writer.ToArray()));
        vu.DataMemory.CopyTo(copyVu.DataMemory, 0);
        foreach (var v in new[] { vif, copy })
        {
            Feed(v, 0x00060005, 0x00080007, 0x000A0009, 0x000C000B, Op(0x07, 0x77), 0);
        }

        copyVu.DataMemory.Should().Equal(vu.DataMemory);
        for (var offset = 0u; offset < 0x180; offset += 0x10)
        {
            copy.ReadRegister(offset).Should().Be(vif.ReadRegister(offset), $"register +{offset:x}");
        }

        copy.ReadRegister(Mark).Should().Be(0x77);
        vu.Vector(8).Should().Equal([2u, 4u, 4u, 6u], "V2 repeats x and y, the row is added, TOPS = BASE = 8");
        vu.Vector(9).Should().Equal([0u, 0u, 0u, 0u], "CL 2, WL 1 skips every other quadword");
        vu.Vector(10).Should().Equal([4u, 6u, 6u, 8u]);
    }
}
