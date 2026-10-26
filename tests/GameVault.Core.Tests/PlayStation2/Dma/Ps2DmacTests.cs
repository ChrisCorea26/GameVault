using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Dma;

/// <summary>The EE's DMAC (ps2tek: "DMAC I/O", "DMAC Chain Mode", "DMAC MFIFO", "DMAC Interrupts").</summary>
public sealed class Ps2DmacTests
{
    private const uint DCtrl = 0x1000E000;
    private const uint DStat = 0x1000E010;
    private const uint DPcr = 0x1000E020;
    private const uint DRbsr = 0x1000E040;
    private const uint DRbor = 0x1000E050;
    private const uint DStadr = 0x1000E060;
    private const uint DEnableR = 0x1000F520;
    private const uint DEnableW = 0x1000F590;

    private const uint Str = 0x100;
    private const uint Chain = 0x4;
    private const uint Tte = 0x40;
    private const uint Tie = 0x80;

    private const uint Refe = 0;
    private const uint Cnt = 1;
    private const uint Next = 2;
    private const uint Ref = 3;
    private const uint Refs = 4;
    private const uint Call = 5;
    private const uint Ret = 6;
    private const uint End = 7;

    private static void PutTag(Ps2DmaRig rig, uint at, uint qwc, uint id, uint address = 0, bool irq = false, uint w2 = 0, uint w3 = 0)
    {
        rig.Write32(at, qwc | (id << 28) | (irq ? 0x8000_0000u : 0));
        rig.Write32(at + 4, address);
        rig.Write32(at + 8, w2);
        rig.Write32(at + 12, w3);
    }

    private static void PutData(Ps2DmaRig rig, uint at, uint marker)
    {
        rig.Write32(at, marker);
        rig.Write32(at + 4, ~marker);
    }

    private static UInt128 Quad(uint w0, uint w1 = 0, uint w2 = 0, uint w3 = 0) =>
        ((UInt128)w3 << 96) | ((UInt128)w2 << 64) | ((UInt128)w1 << 32) | w0;

    private static uint[] Markers(IEnumerable<UInt128> quadwords) => quadwords.Select(q => (uint)q).ToArray();

    private static void StartChain(Ps2DmaRig rig, int ch, uint tadr, uint extra = 0)
    {
        var b = Ps2DmaRig.ChannelBase(ch);
        rig.SetReg(b + 0x20, 0);
        rig.SetReg(b + 0x30, tadr);
        rig.SetChcr(ch, Chain | Str | extra);
    }

    private static void StartNormal(Ps2DmaRig rig, int ch, uint madr, uint qwc, uint dir = 1)
    {
        var b = Ps2DmaRig.ChannelBase(ch);
        rig.SetReg(b + 0x10, madr);
        rig.SetReg(b + 0x20, qwc);
        rig.SetChcr(ch, Str | dir);
    }

    // ---- Normal mode ----

    [Fact]
    public void NormalModeSendsQwcQuadwordsAndRaisesTheChannelsStatusBit()
    {
        var rig = new Ps2DmaRig();
        for (var i = 0u; i < 3; i++)
        {
            PutData(rig, 0x2000 + (i * 16), 0x100 + i);
        }

        StartNormal(rig, 2, 0x2000, 3);
        rig.Dmac.Busy.Should().BeTrue();
        rig.Dmac.Advance(100);
        Markers(rig.Gif.Path3).Should().Equal(0x100u, 0x101u, 0x102u);
        rig.Madr(2).Should().Be(0x2030);
        rig.Reg(Ps2DmaRig.ChannelBase(2) + 0x20).Should().Be(0);
        (rig.Chcr(2) & Str).Should().Be(0);
        rig.Reg(DStat).Should().Be(1u << 2);
        rig.Dmac.Busy.Should().BeFalse();
    }

    [Fact]
    public void TransfersTakeAboutABusCyclePerQuadword()
    {
        var rig = new Ps2DmaRig();
        StartNormal(rig, 2, 0x2000, 40);
        rig.Dmac.Advance(5);
        rig.Gif.Path3.Should().HaveCount(5);
        rig.Madr(2).Should().Be(0x2050, "MADR follows the transfer");
        rig.Dmac.Advance(30);
        rig.Gif.Path3.Should().HaveCount(35);
        rig.Dmac.Advance(30);
        rig.Gif.Path3.Should().HaveCount(40);
    }

    [Fact]
    public void ATransferWaitsWhileThePeerRefusesData()
    {
        var rig = new Ps2DmaRig();
        rig.Gif.Path3Masked = true;
        StartNormal(rig, 2, 0x2000, 2);
        rig.Dmac.Advance(1000);
        rig.Gif.Path3.Should().BeEmpty();
        rig.Dmac.Busy.Should().BeTrue();
        rig.Gif.Path3Masked = false;
        rig.Dmac.Advance(1000);
        rig.Gif.Path3.Should().HaveCount(2);
        rig.Dmac.Busy.Should().BeFalse();
    }

    [Fact]
    public void NothingMovesWithoutDmaeOrWhileDEnablewSuspends()
    {
        var rig = new Ps2DmaRig();
        rig.Reg(DEnableR).Should().Be(0x1201u, "the value the BIOS expects at boot (ps2tek)");
        rig.SetReg(DCtrl, 0);
        StartNormal(rig, 2, 0x2000, 2);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().BeEmpty();
        rig.Dmac.Busy.Should().BeFalse();

        rig.SetReg(DCtrl, 1);
        rig.SetReg(DEnableW, 0x1201 | 0x10000);
        rig.Reg(DEnableR).Should().Be(0x11201u);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().BeEmpty();

        rig.SetReg(DEnableW, 0x1201);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().HaveCount(2);
    }

    [Fact]
    public void WritingStrZeroSuspendsAndWritesWhileRunningOnlyTouchStr()
    {
        var rig = new Ps2DmaRig();
        StartNormal(rig, 2, 0x2000, 32);
        rig.Dmac.Advance(4);
        rig.SetChcr(2, 0x1C); // STR = 0 (and other bits, ignored while running)
        rig.Chcr(2).Should().Be(0x1u);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().HaveCount(4);
        rig.Reg(DStat).Should().Be(0, "a suspended channel hasn't finished");
        rig.SetChcr(2, Str | 1);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().HaveCount(32);
    }

    [Fact]
    public void PriorityControlDisablesChannelsWithoutTheirEnableBit()
    {
        var rig = new Ps2DmaRig();
        rig.SetReg(DPcr, 0x8000_0000 | (1u << (16 + 9)));
        StartNormal(rig, 2, 0x2000, 1);
        StartNormal(rig, 9, 0x2000, 1);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().BeEmpty();
        (rig.Chcr(9) & Str).Should().Be(0);
        rig.SetReg(DPcr, 0);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().HaveCount(1);
    }

    // ---- Source chain ----

    [Fact]
    public void EverySourceTagIncludingCallAndRetTwoDeep()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 1, Cnt);
        PutData(rig, 0x1010, 1);
        PutTag(rig, 0x1020, 1, Next, 0x2000);
        PutData(rig, 0x1030, 2);
        PutTag(rig, 0x2000, 1, Ref, 0x3000);
        PutData(rig, 0x3000, 3);
        PutTag(rig, 0x2010, 1, Refs, 0x3010);
        PutData(rig, 0x3010, 4);
        PutTag(rig, 0x2020, 1, Call, 0x4000);
        PutData(rig, 0x2030, 5);
        PutTag(rig, 0x4000, 0, Call, 0x5000);
        PutTag(rig, 0x5000, 1, Ret);
        PutData(rig, 0x5010, 6);
        PutTag(rig, 0x4010, 0, Ret);
        PutTag(rig, 0x2040, 1, End);
        PutData(rig, 0x2050, 7);

        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(1000);
        Markers(rig.Gif.Path3).Should().Equal(1u, 2u, 3u, 4u, 5u, 6u, 7u);
        var b = Ps2DmaRig.ChannelBase(2);
        rig.Reg(b + 0x40).Should().Be(0x2040, "CALL saved the address after its data in ASR0");
        rig.Reg(b + 0x50).Should().Be(0x4010, "the nested CALL used ASR1");
        (rig.Chcr(2) & 0x30).Should().Be(0, "both RETs popped the stack");
        rig.Tadr(2).Should().Be(0x2040, "END leaves TADR on itself");
        rig.Madr(2).Should().Be(0x2060);
        (rig.Chcr(2) >> 16).Should().Be(0x7000u, "CHCR.TAG holds the tag's bits 16-31");
        rig.Reg(DStat).Should().Be(1u << 2);
    }

    [Fact]
    public void RefeEndsTheChainAfterItsData()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 2, Refe, 0x3000);
        PutData(rig, 0x3000, 1);
        PutData(rig, 0x3010, 2);
        PutTag(rig, 0x1010, 1, Cnt);
        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(1000);
        Markers(rig.Gif.Path3).Should().Equal(1u, 2u);
        rig.Tadr(2).Should().Be(0x1010);
        (rig.Chcr(2) & Str).Should().Be(0);
    }

    [Fact]
    public void RetWithAnEmptyStackEndsTheChain()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 1, Ret);
        PutData(rig, 0x1010, 9);
        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(1000);
        Markers(rig.Gif.Path3).Should().Equal(9u);
        (rig.Chcr(2) & Str).Should().Be(0);
    }

    [Fact]
    public void TheTagsIrqBitStopsTheChainOnlyWithTie()
    {
        foreach (var tie in new[] { false, true })
        {
            var rig = new Ps2DmaRig();
            PutTag(rig, 0x1000, 1, Cnt, irq: true);
            PutData(rig, 0x1010, 1);
            PutTag(rig, 0x1020, 1, End);
            PutData(rig, 0x1030, 2);
            StartChain(rig, 2, 0x1000, tie ? Tie : 0);
            rig.Dmac.Advance(1000);
            Markers(rig.Gif.Path3).Should().Equal(tie ? [1u] : [1u, 2u]);
            rig.Tadr(2).Should().Be(0x1020u);
            (rig.Chcr(2) >> 16).Should().Be(tie ? 0x9000u : 0x7000u);
            rig.Reg(DStat).Should().Be(1u << 2);
        }
    }

    [Fact]
    public void AChainStartedWithQwcResumesUsingTheTagInChcr()
    {
        var rig = new Ps2DmaRig();
        PutData(rig, 0x3000, 0xAA);
        var b = Ps2DmaRig.ChannelBase(2);
        rig.SetReg(b + 0x10, 0x3000);
        rig.SetReg(b + 0x20, 1);
        rig.SetReg(b + 0x30, 0x1000);
        rig.SetChcr(2, (0x7000u << 16) | Chain | Str); // the last tag was an END
        rig.Dmac.Advance(1000);
        Markers(rig.Gif.Path3).Should().Equal(0xAAu);
        (rig.Chcr(2) & Str).Should().Be(0);
    }

    [Fact]
    public void TteSendsTheTagsUpperHalfToTheVifAsVifcodes()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 1, End, w2: 0x07000000 | 0x4242, w3: 0x01000000 | 0x0404);
        rig.Write32(0x1010, 0x60010000); // UNPACK S-32, 1 vector to 0
        rig.Write32(0x1014, 0x5555);
        StartChain(rig, 1, 0x1000, Tte);
        rig.Dmac.Advance(1000);
        rig.Vif1.ReadRegister(0x30).Should().Be(0x4242);
        rig.Vif1.ReadRegister(0x40).Should().Be(0x0404);
        rig.Vu1.Vector(0).Should().Equal(0x5555u, 0x5555u, 0x5555u, 0x5555u);
    }

    [Fact]
    public void TteSendsSif1TheIopsTagAndNotTheGif()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 1, End, w2: 0x00123450, w3: 4);
        PutData(rig, 0x1010, 0x77);
        StartChain(rig, 6, 0x1000, Tte);
        rig.Dmac.Advance(1000);
        rig.Sif.Sif1.Count.Should().Be(6);
        rig.Sif.Sif1.TryPop(out uint w0);
        rig.Sif.Sif1.TryPop(out uint w1);
        rig.Sif.Sif1.TryPop(out UInt128 data);
        (w0, w1).Should().Be((0x00123450u, 4u));
        ((uint)data).Should().Be(0x77);

        var gifRig = new Ps2DmaRig();
        PutTag(gifRig, 0x1000, 1, End, w2: 0xDEAD);
        PutData(gifRig, 0x1010, 0x78);
        StartChain(gifRig, 2, 0x1000, Tte);
        gifRig.Dmac.Advance(1000);
        Markers(gifRig.Gif.Path3).Should().Equal(0x78u);
    }

    [Fact]
    public void ScratchpadAddressesInTagsReadTheScratchpad()
    {
        var rig = new Ps2DmaRig();
        BitConverter.GetBytes(0xC0FFEEu).CopyTo(rig.Scratchpad, 0x120);
        PutTag(rig, 0x1000, 1, Refe, 0x8000_0120);
        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(100);
        Markers(rig.Gif.Path3).Should().Equal(0xC0FFEEu);
    }

    // ---- Stall control ----

    [Fact]
    public void StallControlHoldsTheDrainBehindTheSource()
    {
        var rig = new Ps2DmaRig();
        rig.SetReg(DCtrl, 1 | (1 << 4) | (2 << 6)); // STS = SIF0, STD = GIF
        rig.SetReg(DStadr, 0x6000);
        PutTag(rig, 0x1000, 4, Refs, 0x6000);
        PutTag(rig, 0x1010, 0, End);
        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(100);
        rig.Gif.Path3.Should().BeEmpty();
        (rig.Reg(DStat) & (1 << 13)).Should().NotBe(0u, "SIS: the drain stalled");

        // SIF0 writes two quadwords at $6000: D_STADR follows it and the GIF may read them.
        rig.Sif.Sif0.TryPush(Quad(1));
        rig.Sif.Sif0.TryPush(Quad(2));
        StartNormal(rig, 5, 0x6000, 4, dir: 0);
        rig.Dmac.Advance(100);
        rig.Reg(DStadr).Should().Be(0x6020);
        Markers(rig.Gif.Path3).Should().Equal(1u, 2u);

        rig.Sif.Sif0.TryPush(Quad(3));
        rig.Sif.Sif0.TryPush(Quad(4));
        rig.Dmac.Advance(100);
        Markers(rig.Gif.Path3).Should().Equal(1u, 2u, 3u, 4u);
        (rig.Chcr(2) & Str).Should().Be(0);
        (rig.Chcr(5) & Str).Should().Be(0);
    }

    // ---- MFIFO ----

    [Fact]
    public void TheMfifoRingWrapsAndStallsItsDrainWhenEmpty()
    {
        var rig = new Ps2DmaRig();
        const uint ring = 0x10000;
        rig.SetReg(DCtrl, 1 | (3 << 2)); // MFIFO drained by the GIF
        rig.SetReg(DRbor, ring);
        rig.SetReg(DRbsr, 0x3F0); // 64 quadwords

        // In the scratchpad: CNT(1), data, CNT(1), data — placed at the ring's last two quadwords and its first two.
        void SprQuad(int at, UInt128 q) => BitConverter.GetBytes((ulong)q).Concat(BitConverter.GetBytes((ulong)(q >> 64))).ToArray().CopyTo(rig.Scratchpad, at);
        SprQuad(0x00, Quad(1 | (Cnt << 28)));
        SprQuad(0x10, Quad(0xA1));
        SprQuad(0x20, Quad(1 | (Cnt << 28)));
        SprQuad(0x30, Quad(0xA2));
        rig.SetSadr(8, 0);
        StartNormal(rig, 8, ring + 0x3E0, 4, dir: 0);
        rig.Dmac.Advance(100);
        rig.Madr(8).Should().Be(ring + 0x20, "SPR_FROM's MADR wrapped inside the ring");
        rig.Read32(ring + 0x3F0).Should().Be(0xA1);
        rig.Read32(ring + 0x010).Should().Be(0xA2);

        StartChain(rig, 2, ring + 0x3E0);
        rig.Dmac.Advance(100);
        Markers(rig.Gif.Path3).Should().Equal(0xA1u, 0xA2u);
        rig.Tadr(2).Should().Be(ring + 0x20);
        (rig.Chcr(2) & Str).Should().NotBe(0u, "the drain waits for more");
        (rig.Reg(DStat) & (1 << 14)).Should().NotBe(0u, "MEIS: the MFIFO is empty");

        SprQuad(0x40, Quad(1 | (End << 28)));
        SprQuad(0x50, Quad(0xA3));
        StartNormal(rig, 8, rig.Madr(8), 2, dir: 0);
        rig.Dmac.Advance(100);
        Markers(rig.Gif.Path3).Should().Equal(0xA1u, 0xA2u, 0xA3u);
        (rig.Chcr(2) & Str).Should().Be(0);
    }

    // ---- Destination chain ----

    [Fact]
    public void Sif0ReadsItsTagsFromTheFifo()
    {
        var rig = new Ps2DmaRig();
        StartChain(rig, 5, 0);
        rig.Sif.Sif0.TryPush(Quad(2 | (Cnt << 28), 0x6000, 0x1111, 0x2222));
        rig.Dmac.Advance(100);
        rig.Dmac.Busy.Should().BeTrue("the data hasn't come yet");
        rig.Sif.Sif0.TryPush(Quad(0x51));
        rig.Sif.Sif0.TryPush(Quad(0x52));
        rig.Sif.Sif0.TryPush(Quad(1 | (End << 28), 0x7000));
        rig.Sif.Sif0.TryPush(Quad(0x53));
        rig.Dmac.Advance(100);
        rig.Read32(0x6000).Should().Be(0x51);
        rig.Read32(0x6010).Should().Be(0x52);
        rig.Read32(0x7000).Should().Be(0x53);
        rig.Madr(5).Should().Be(0x7010);
        (rig.Chcr(5) & Str).Should().Be(0);
        (rig.Chcr(5) >> 28).Should().Be(End);
        rig.Reg(DStat).Should().Be(1u << 5);
    }

    [Fact]
    public void SprFromReadsItsTagsFromTheScratchpad()
    {
        var rig = new Ps2DmaRig();
        BitConverter.GetBytes(1u | (0u << 28)).CopyTo(rig.Scratchpad, 0x100); // CNTS
        BitConverter.GetBytes(0x8000u).CopyTo(rig.Scratchpad, 0x104);
        BitConverter.GetBytes(0xB1u).CopyTo(rig.Scratchpad, 0x110);
        BitConverter.GetBytes(1u | (7u << 28)).CopyTo(rig.Scratchpad, 0x120); // END
        BitConverter.GetBytes(0x9000u).CopyTo(rig.Scratchpad, 0x124);
        BitConverter.GetBytes(0xB2u).CopyTo(rig.Scratchpad, 0x130);
        rig.SetSadr(8, 0x100);
        StartChain(rig, 8, 0);
        rig.Dmac.Advance(100);
        rig.Read32(0x8000).Should().Be(0xB1);
        rig.Read32(0x9000).Should().Be(0xB2);
        rig.Sadr(8).Should().Be(0x140);
        (rig.Chcr(8) & Str).Should().Be(0);
    }

    // ---- The other peers ----

    [Fact]
    public void Sif1AndSif0MoveQuadwordsThroughTheFifos()
    {
        var rig = new Ps2DmaRig();
        PutData(rig, 0x2000, 0x61);
        PutData(rig, 0x2010, 0x62);
        StartNormal(rig, 6, 0x2000, 2);
        rig.Dmac.Advance(100);
        rig.Sif.Sif1.TryPop(out UInt128 a);
        ((uint)a).Should().Be(0x61);

        rig.Sif.Sif0.TryPush(Quad(0x71));
        StartNormal(rig, 5, 0x3000, 1, dir: 0);
        rig.Dmac.Advance(100);
        rig.Read32(0x3000).Should().Be(0x71);
    }

    [Fact]
    public void Sif1WaitsWhenTheFifoIsFull()
    {
        var rig = new Ps2DmaRig();
        StartNormal(rig, 6, 0x2000, (uint)(Ps2Sif.FifoWords / 4) + 3);
        rig.Dmac.Advance(1000);
        rig.Sif.Sif1.Free.Should().Be(0);
        rig.Dmac.Busy.Should().BeTrue();
        for (var i = 0; i < 3; i++)
        {
            rig.Sif.Sif1.TryPop(out UInt128 _);
        }

        rig.Dmac.Advance(1000);
        rig.Dmac.Busy.Should().BeFalse();
    }

    [Fact]
    public void TheIpuChannelsUseTheIpuOnceItIsSet()
    {
        var rig = new Ps2DmaRig();
        StartNormal(rig, 4, 0x2000, 2);
        rig.Dmac.Advance(100);
        rig.Dmac.Busy.Should().BeTrue();
        var ipu = new FakeIpu();
        ipu.Out.Enqueue(Quad(0x91));
        rig.Dmac.Ipu = ipu;
        StartNormal(rig, 3, 0x5000, 1, dir: 0);
        rig.Dmac.Advance(100);
        ipu.In.Should().HaveCount(2);
        rig.Read32(0x5000).Should().Be(0x91);
        rig.Dmac.Busy.Should().BeFalse();
    }

    [Fact]
    public void Vif1DownloadsWithDirZero()
    {
        var rig = new Ps2DmaRig();
        rig.Vif1.WriteRegister(0x00, 1u << 23);
        rig.Gif.Download.Enqueue(Quad(0xD1));
        rig.Gif.Download.Enqueue(Quad(0xD2));
        StartNormal(rig, 1, 0x4000, 2, dir: 0);
        rig.Dmac.Advance(100);
        rig.Read32(0x4000).Should().Be(0xD1);
        rig.Read32(0x4010).Should().Be(0xD2);
        (rig.Chcr(1) & Str).Should().Be(0);
    }

    [Fact]
    public void ScratchpadTransfersBothWays()
    {
        var rig = new Ps2DmaRig();
        PutData(rig, 0x2000, 0xE1);
        rig.SetSadr(9, 0x3FF0);
        PutData(rig, 0x2010, 0xE2);
        StartNormal(rig, 9, 0x2000, 2);
        rig.Dmac.Advance(100);
        BitConverter.ToUInt32(rig.Scratchpad, 0x3FF0).Should().Be(0xE1);
        BitConverter.ToUInt32(rig.Scratchpad, 0).Should().Be(0xE2, "SADR wraps at 16 KB");
        rig.Sadr(9).Should().Be(0x10);

        rig.SetSadr(8, 0x3FF0);
        StartNormal(rig, 8, 0x5000, 2, dir: 0);
        rig.Dmac.Advance(100);
        rig.Read32(0x5000).Should().Be(0xE1);
        rig.Read32(0x5010).Should().Be(0xE2);
    }

    // ---- D_STAT, INT1, CPCOND0 ----

    [Fact]
    public void DStatClearsStatusOnWriteAndTogglesMasksAndDrivesInt1()
    {
        var rig = new Ps2DmaRig();
        StartNormal(rig, 9, 0x2000, 1);
        rig.Dmac.Advance(10);
        rig.Reg(DStat).Should().Be(1u << 9);
        rig.Dmac.Int1.Should().BeFalse("CIM9 is clear");
        rig.SetReg(DStat, 1u << 25);
        rig.Dmac.Int1.Should().BeTrue();
        rig.SetReg(DStat, 1u << 25);
        rig.Reg(DStat).Should().Be(1u << 9, "writing a mask bit again toggles it back");
        rig.SetReg(DStat, (1u << 25) | (1u << 9));
        rig.Reg(DStat).Should().Be(1u << 25);
        rig.Dmac.Int1.Should().BeFalse();
    }

    [Fact]
    public void StallAndMfifoInterruptsHaveTheirOwnMasks()
    {
        var rig = new Ps2DmaRig();
        rig.SetReg(DCtrl, 1 | (1 << 4) | (2 << 6));
        PutTag(rig, 0x1000, 1, Refs, 0x6000);
        StartChain(rig, 2, 0x1000);
        rig.Dmac.Advance(10);
        rig.Dmac.Int1.Should().BeFalse();
        rig.SetReg(DStat, 1u << 29);
        rig.Dmac.Int1.Should().BeTrue();
        rig.SetReg(DStat, 1u << 13);
        rig.Dmac.Int1.Should().BeFalse();
    }

    [Fact]
    public void Cpcond0FollowsTheSelectedChannelsStatus()
    {
        // The sequence of ps2autotests' dma/dmac/cpcond (hardware: 1, 0, 1, 1, 1, 0).
        var rig = new Ps2DmaRig();
        var seen = new List<bool> { rig.Dmac.Cpcond0 };
        rig.SetReg(DPcr, 1 << 9);
        seen.Add(rig.Dmac.Cpcond0);
        StartNormal(rig, 9, 0x2000, 4);
        rig.Dmac.Advance(100);
        seen.Add(rig.Dmac.Cpcond0);
        rig.SetReg(DPcr, 0);
        seen.Add(rig.Dmac.Cpcond0);
        rig.SetReg(DPcr, 1 << 9);
        seen.Add(rig.Dmac.Cpcond0);
        rig.SetReg(DStat, 1 << 9);
        seen.Add(rig.Dmac.Cpcond0);
        seen.Should().Equal(true, false, true, true, true, false);
    }

    // ---- Save states ----

    [Fact]
    public void TheDmacRoundTripsThroughASaveStateMidChain()
    {
        var rig = new Ps2DmaRig();
        PutTag(rig, 0x1000, 20, Cnt);
        for (var i = 0u; i < 20; i++)
        {
            PutData(rig, 0x1010 + (i * 16), i);
        }

        PutTag(rig, 0x1150, 0, Call, 0x4000);
        PutTag(rig, 0x4000, 2, Ret);
        PutData(rig, 0x4010, 100);
        PutData(rig, 0x4020, 101);
        PutTag(rig, 0x1160, 1, End);
        PutData(rig, 0x1170, 102);
        rig.SetReg(DStat, 1u << 18);
        StartChain(rig, 2, 0x1000, Tie);
        rig.Dmac.Advance(7);
        var writer = new StateWriter();
        rig.Dmac.SaveState(writer);

        var copy = new Ps2DmaRig();
        rig.Ram.CopyTo(copy.Ram, 0);
        copy.Dmac.LoadState(new StateReader(writer.ToArray()));
        for (var r = 0x1000A000u; r < 0x1000A0A0; r += 0x10)
        {
            copy.Reg(r).Should().Be(rig.Reg(r));
        }

        rig.Dmac.Advance(1000);
        copy.Dmac.Advance(1000);
        Markers(copy.Gif.Path3).Should().Equal(Markers(rig.Gif.Path3).Skip(6));
        Markers(rig.Gif.Path3).Should().Equal(Enumerable.Range(0, 20).Select(i => (uint)i).Concat([100u, 101u, 102u]));
        copy.Gif.Path3.Should().HaveCount(rig.Gif.Path3.Count - 6, "six quadwords had gone before the save");
        copy.Reg(DStat).Should().Be(rig.Reg(DStat));
        copy.Dmac.Int1.Should().BeTrue();
    }
}
