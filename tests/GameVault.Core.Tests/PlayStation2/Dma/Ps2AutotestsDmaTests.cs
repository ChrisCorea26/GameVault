using System.Text;
using FluentAssertions;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation2.Dma;

/// <summary>
/// ps2autotests' DMA programs (jpd002/ps2autotests, ISC: tests/dma/dmac, tests/dma/spr, tests/dma/vif) replayed against
/// the DMAC and VIFs: each test does what the program's C++ does — the same register writes, packets and printf calls —
/// and compares the text with the program's <c>.expected</c> file, recorded on a PS2.
/// </summary>
public sealed class Ps2AutotestsDmaTests
{
    private const uint DStat = 0x1000E010;
    private const uint DPcr = 0x1000E020;
    private const uint DSqwc = 0x1000E030;
    private const int FromSpr = 8;
    private const int ToSpr = 9;

    private static string Expected(string name) =>
        Encoding.ASCII.GetString(TestRomLocator.ReadCached($"ps2autotests/dma/{name}.expected")).Replace("\r\n", "\n");

    // ---- dma/dmac ----

    [TestRomFact("ps2autotests/dma/dmac/cpcond.expected")]
    public void DmacCpcond()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        var buf = rig.Alloc(16 * 1024);
        rig.Write32(buf, 0x01234567);
        rig.Write32(buf + 4, 0x89ABCDEF);
        rig.Write32(buf + 8, 0xDEADBEEF);
        rig.Write32(buf + 12, 0x1337C0DE);
        int Cond() => rig.Dmac.Cpcond0 ? 1 : 0;

        o.Append($"initial value: {Cond()}\n");
        rig.SetReg(DPcr, 1 << 9);
        o.Append($"value after setting PCR.CPC: {Cond()}\n");
        rig.SendSimple(ToSpr, buf, 16 * 1024, s => o.Append(s));
        o.Append($"value after sending DMA: {Cond()}\n");
        rig.SetReg(DPcr, rig.Reg(DPcr) & ~(1u << 9));
        o.Append($"value after clearing PCR.CPC: {Cond()}\n");
        rig.SetReg(DPcr, 1 << 9);
        o.Append($"value after restoring PCR.CPC: {Cond()}\n");
        rig.SetReg(DStat, 1 << 9);
        o.Append($"value after resetting STAT.CIS: {Cond()}\n");
        o.Append("-- TEST END\n");

        o.ToString().Should().Be(Expected("dmac/cpcond"));
    }

    [TestRomFact("ps2autotests/dma/dmac/tagintr.expected")]
    public void DmacTagInterrupt()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        var buf = rig.Alloc(16 * 1024);
        rig.Write32(buf, 0x01234567);

        void TestTagIrq(bool tagIntEnable, bool tagIrq)
        {
            rig.SetReg(DStat, 1 << 9);
            var packet = new ChainPacket(rig, 512);
            packet.Reset();
            packet.Tag(1, 3, buf, tagIrq); // REF
            packet.Tag(1, 3, buf); // REF
            packet.Tag(0, 7, 0); // END
            rig.SetChcr(ToSpr, tagIntEnable ? rig.Chcr(ToSpr) | 0x80 : rig.Chcr(ToSpr) & ~0x80u);
            var cisBefore = rig.Reg(DStat) & 0x200;
            rig.SendChain(ToSpr, packet.Start, s => o.Append(s));
            var cisAfter = rig.Reg(DStat) & 0x200;
            var tadrOffset = rig.Tadr(ToSpr) - packet.Start;
            o.Append($"Tag interrupt {(tagIntEnable ? "enabled" : "disabled")}, Tag IRQ {(tagIrq ? "enabled" : "disabled")}:\n");
            o.Append($"  CHCR(tag): {rig.Chcr(ToSpr) & 0xFFFF0000:x8}, TADR offset: {tadrOffset:x8}, CIS(before): {cisBefore:x8}, CIS(after): {cisAfter:x8}\n");
        }

        TestTagIrq(true, true);
        TestTagIrq(true, false);
        TestTagIrq(false, true);
        TestTagIrq(false, false);
        o.Append("-- TEST END\n");

        o.ToString().Should().Be(Expected("dmac/tagintr"));
    }

    // ---- dma/spr ----

    private static void SetWords(Ps2DmaRig rig, uint at, params uint[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            rig.Write32(at + (uint)(i * 4), words[i]);
        }
    }

    private static string Words(Ps2DmaRig rig, uint at) =>
        $"{rig.Read32(at):x8} {rig.Read32(at + 4):x8} {rig.Read32(at + 8):x8} {rig.Read32(at + 12):x8}";

    private static void ClearSpr(Ps2DmaRig rig, uint buf, Action<string> print)
    {
        rig.Fill(buf, 16 * 1024, 0);
        rig.SetSadr(ToSpr, 0);
        rig.SendSimple(ToSpr, buf, 16 * 1024, print);
    }

    private static void FillPattern(Ps2DmaRig rig, uint buf)
    {
        for (var i = 0u; i < 16 * 1024 / 4; i++)
        {
            var c = i & 0xFF;
            var v = c | (c << 8) | (c << 16) | (c << 24);
            if (i > 256)
            {
                v |= 0xFF000000;
            }

            rig.Write32(buf + (i * 4), v);
        }
    }

    [TestRomFact("ps2autotests/dma/spr/normal.expected")]
    public void SprNormal()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);
        var buf = rig.Alloc(16 * 1024);

        // testSADROffsets
        ClearSpr(rig, buf, P);
        P("SADR offsets:\n");
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        rig.SetSadr(ToSpr, 0x20);
        P($"  toSPR SADR (0x10): {rig.Sadr(ToSpr):x8} -> ");
        rig.SendSimple(ToSpr, buf, 16, P);
        P($"{rig.Sadr(ToSpr):x8}\n");
        rig.SetSadr(FromSpr, 0x10);
        P($"  fromSPR SADR (0x20): {rig.Sadr(FromSpr):x8} -> ");
        rig.SendSimple(FromSpr, buf, 32, P);
        P($"{rig.Sadr(FromSpr):x8}\n");
        P($"  SADR +0x10: {Words(rig, buf)}\n");
        P($"  SADR +0x20: {Words(rig, buf + 16)}\n");
        P("\n");
        rig.Fill(buf, 16 * 1024, 0xCC);
        rig.SetSadr(FromSpr, 0x14);
        P($"  fromSPR SADR (0x20): {rig.Sadr(FromSpr):x8} -> ");
        rig.SendSimple(FromSpr, buf, 32, P);
        P($"{rig.Sadr(FromSpr):x8}\n");
        P($"  SADR +0x14: {Words(rig, buf)}\n");
        P($"  SADR +0x24: {Words(rig, buf + 16)}\n");
        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("\n");

        // testSADRHighBits
        ClearSpr(rig, buf, P);
        P("SADR high bits:\n");
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        rig.SetSadr(ToSpr, 0x81234028);
        P($"  toSPR SADR (0x10): {rig.Sadr(ToSpr):x8} -> ");
        rig.SendSimple(ToSpr, buf, 16, P);
        P($"{rig.Sadr(ToSpr):x8}\n");
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0x20);
        P($"  fromSPR SADR (0x10): {rig.Sadr(FromSpr):x8} -> ");
        rig.SendSimple(FromSpr, buf, 16, P);
        P($"{rig.Sadr(FromSpr):x8}\n");
        P($"  SADR send with ignored bits: {Words(rig, buf)}\n");
        P("\n");
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0x6703802C);
        P($"  fromSPR SADR (0x10): {rig.Sadr(FromSpr):x8} -> ");
        rig.SendSimple(FromSpr, buf, 16, P);
        P($"{rig.Sadr(FromSpr):x8}\n");
        P($"  SADR receive with ignored bits: {Words(rig, buf)}\n");
        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("\n");

        // testMADRBit
        ClearSpr(rig, buf, P);
        P("MADR with high bit:\n");
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        rig.SetSadr(ToSpr, 0);
        rig.SendSimple(ToSpr, buf | 0x80000000, 16, P);
        P($"  toSPR MADR: high bit set? {((rig.Madr(ToSpr) & 0x80000000) != 0 ? "yes" : "no")}\n");
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0);
        rig.SendSimple(FromSpr, buf, 16, P);
        P($"  MADR send with SPR bit: {Words(rig, buf)}\n");
        P("\n");
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        rig.SetSadr(ToSpr, 0x100);
        rig.SendSimple(ToSpr, buf, 16, P);
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0x100);
        rig.SendSimple(FromSpr, buf | 0x80000000, 16, P);
        P($"  fromSPR MADR: high bit set? {((rig.Madr(FromSpr) & 0x80000000) != 0 ? "yes" : "no")}\n");
        P($"  MADR receive with SPR bit: {Words(rig, buf)}\n");
        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("\n");

        // testNearEnd
        ClearSpr(rig, buf, P);
        P("Near end (wrapping):\n");
        FillPattern(rig, buf);
        rig.SetSadr(ToSpr, 0);
        rig.SendSimple(ToSpr, buf, 16 * 1024, P);
        rig.Fill(buf, 16 * 1024, 0xCC);
        rig.SetSadr(FromSpr, 0x3FF0);
        rig.SendSimple(FromSpr, buf, 16 * 1024, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Send near end 0x0000: {Words(rig, buf)}\n");
        P($"  Send near end 0x0010: {Words(rig, buf + 16)}\n");
        ClearSpr(rig, buf, P);
        FillPattern(rig, buf);
        rig.SetSadr(ToSpr, 0x3FF0);
        rig.SendSimple(ToSpr, buf, 16 * 1024, P);
        rig.Fill(buf, 16 * 1024, 0xCC);
        rig.SetSadr(FromSpr, 0);
        rig.SendSimple(FromSpr, buf, 16 * 1024, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Read near end 0x0000: {Words(rig, buf)}\n");
        P($"  Read near end 0x0010: {Words(rig, buf + 16)}\n");
        P("-- TEST END\n");

        o.ToString().Should().Be(Expected("spr/normal"));
    }

    [TestRomFact("ps2autotests/dma/spr/chain.expected")]
    public void SprChain()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);
        var buf = rig.Alloc(16 * 1024);

        // testSADROffsets
        ClearSpr(rig, buf, P);
        P("SADR offsets:\n");
        var tags = new ChainPacket(rig, 512);
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        tags.Reset();
        tags.Tag(1, 0, buf); // REFE
        rig.SetSadr(ToSpr, 0x20);
        rig.SendChain(ToSpr, tags.Start, P);
        rig.SetSadr(FromSpr, 0x10);
        rig.SendSimple(FromSpr, buf, 32, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Tag data updated: MADR=+{rig.Madr(ToSpr) - buf:x8}, TADR=+{rig.Tadr(ToSpr) - tags.Start:x8}\n");
        P($"  SADR +0x10: {Words(rig, buf)}\n");
        P($"  SADR +0x20: {Words(rig, buf + 16)}\n");
        P("\n");
        rig.Fill(buf, 16 * 1024, 0xCC);
        SetWords(rig, buf, 0xDEADBEEF, 0x89ABCDEF, 0x01234567, 0x1337C0DE);
        tags.Reset();
        tags.Tag(1, 0, buf);
        rig.SetSadr(ToSpr, 0x14);
        rig.SendChain(ToSpr, tags.Start, P);
        rig.Fill(buf, 16 * 1024, 0xCC);
        rig.SetSadr(FromSpr, 0x10);
        rig.SendSimple(FromSpr, buf, 32, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Tag data updated: MADR=+{rig.Madr(ToSpr) - buf:x8}, TADR=+{rig.Tadr(ToSpr) - tags.Start:x8}\n");
        P($"  SADR +0x14: {Words(rig, buf)}\n");
        P($"  SADR +0x24: {Words(rig, buf + 16)}\n");
        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("\n");

        // testSADRHighBits
        ClearSpr(rig, buf, P);
        P("SADR high bits:\n");
        tags = new ChainPacket(rig, 512);
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        tags.Reset();
        tags.Tag(16, 0, buf);
        rig.SetSadr(ToSpr, 0x81234028);
        rig.SendChain(ToSpr, tags.Start, P);
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0x20);
        rig.SendSimple(FromSpr, buf, 16, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Tag data updated: MADR=+{rig.Madr(ToSpr) - buf:x8}, TADR=+{rig.Tadr(ToSpr) - tags.Start:x8}\n");
        P($"  SADR send with ignored bits: {Words(rig, buf)}\n");
        P("\n");
        rig.Fill(buf, 16, 0);
        rig.SetSadr(FromSpr, 0x6703802C);
        rig.SendSimple(FromSpr, buf, 16, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  SADR receive with ignored bits: {Words(rig, buf)}\n");
        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("\n");

        // testSizeZero
        ClearSpr(rig, buf, P);
        P("Size zero:\n");
        tags = new ChainPacket(rig, 512);
        rig.Fill(buf, 16 * 1024, 0xDD);
        SetWords(rig, buf, 0x01234567, 0x89ABCDEF, 0xDEADBEEF, 0x1337C0DE);
        tags.Reset();
        tags.Tag(0, 0, buf);
        rig.SetSadr(ToSpr, 0);
        rig.SendChain(ToSpr, tags.Start, P);
        rig.Fill(buf, 16 * 1024, 0xCC);
        rig.SetSadr(FromSpr, 0);
        rig.SendSimple(FromSpr, buf, 16 * 1024, P);
        P($"  SADR updated: to={rig.Sadr(ToSpr):x8}, from={rig.Sadr(FromSpr):x8}\n");
        P($"  Send zero 0x0000: {Words(rig, buf)}\n");
        P($"  Send zero 0x0010: {Words(rig, buf + 16)}\n");
        for (var i = 4u; i < 16 * 1024 / 4; i++)
        {
            if (rig.Read32(buf + (i * 4)) != 0)
            {
                P($"  Read zeros until: 0x{i * 4:x4} {rig.Read32(buf + (i * 4)):x8}\n");
                break;
            }
        }

        rig.SetSadr(FromSpr, 0);
        rig.SetSadr(ToSpr, 0);
        P("-- TEST END\n");

        o.ToString().Should().Be(Expected("spr/chain"));
    }

    [TestRomFact("ps2autotests/dma/spr/interleave.expected")]
    public void SprInterleave()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);
        var buf = rig.Alloc(16 * 1024);

        void SetSqwc(uint skip, uint transfer)
        {
            var v = (rig.Reg(DSqwc) & 0xFFFF0000) | (skip & 0xFFFF);
            rig.SetReg(DSqwc, v);
            v = (rig.Reg(DSqwc) & 0x0000FFFF) | ((transfer & 0xFFFF) << 16);
            rig.SetReg(DSqwc, v);
        }

        void Dump(string prefix)
        {
            for (var i = 0u; i < 0x100 / 4; i += 4)
            {
                P($"  {prefix} {i:x2}: {Words(rig, buf + (i * 4))}\n");
            }
        }

        void To(string title, bool enable, uint skip, uint transfer, uint qwc, bool disp = true)
        {
            P($"Interleave upload {title}:\n");
            if (enable)
            {
                rig.SetChcr(ToSpr, rig.Chcr(ToSpr) | 0x8);
            }

            rig.Fill(buf, 16 * 1024, 0xCC);
            rig.SetSadr(ToSpr, 0);
            rig.SendSimple(ToSpr, buf, 16 * 1024, P);
            FillPattern(rig, buf);
            SetSqwc(skip, transfer);
            rig.SetSadr(ToSpr, 0);
            rig.SendSimple(ToSpr, buf, (int)qwc * 16, P);
            rig.Fill(buf, 16 * 1024, 0xDD);
            SetSqwc(0, 0);
            rig.SetSadr(FromSpr, 0);
            rig.SendSimple(FromSpr, buf, 16 * 1024, P);
            if (enable)
            {
                rig.SetChcr(ToSpr, rig.Chcr(ToSpr) & ~0x8u);
            }

            P($"  Upload {title} SADR: from={rig.Sadr(FromSpr):x8}, to={rig.Sadr(ToSpr):x8}\n");
            if (disp)
            {
                Dump($"Upload {title}");
            }

            P("\n");
        }

        void From(string title, bool enable, uint skip, uint transfer, uint qwc, bool disp = true)
        {
            P($"Interleave download {title}:\n");
            if (enable)
            {
                rig.SetChcr(FromSpr, rig.Chcr(FromSpr) | 0x8);
            }

            FillPattern(rig, buf);
            rig.SetSadr(ToSpr, 0);
            rig.SendSimple(ToSpr, buf, 16 * 1024, P);
            rig.Fill(buf, 16 * 1024, 0xCC);
            SetSqwc(skip, transfer);
            rig.SetSadr(FromSpr, 0);
            rig.SendSimple(FromSpr, buf, (int)qwc * 16, P);
            SetSqwc(0, 0);
            if (enable)
            {
                rig.SetChcr(FromSpr, rig.Chcr(FromSpr) & ~0x8u);
            }

            P($"  Download {title} SADR: from={rig.Sadr(FromSpr):x8}, to={rig.Sadr(ToSpr):x8}\n");
            if (disp)
            {
                Dump($"Download {title}");
            }

            P("\n");
        }

        ClearSpr(rig, buf, P);
        To("(disabled, count = 3)", false, 1, 1, 3);
        To("(skip 0, count = 3)", true, 0, 1, 3);
        To("(skip 1, count = 3)", true, 1, 1, 3);
        To("(skip 2, count = 3)", true, 2, 1, 3);
        To("(skip 3, count = 3)", true, 3, 1, 3);
        To("(transfer 1, count 3)", true, 1, 1, 3);
        To("(transfer 2, count 4)", true, 1, 2, 4);
        To("(transfer 3, count 6)", true, 1, 3, 6);
        To("(transfer 2, skip 2, count 6)", true, 2, 2, 6);
        To("(transfer 3, skip 3, count 9)", true, 3, 3, 9);
        To("(transfer 1, skip 14, count 2)", true, 14, 1, 2);
        To("(transfer 1, skip 128, count 2)", true, 128, 1, 2);
        To("(transfer 1, skip 256, count 2)", true, 256, 1, 2);
        To("(transfer 1, skip 257, count 2)", true, 257, 1, 2);
        To("(transfer 128, skip 1, count 128)", true, 1, 128, 128);
        To("(transfer 257, skip 1, count 3)", true, 1, 257, 3);
        To("(transfer 1, skip 255, count 4)", true, 255, 1, 4);
        P("\n");

        ClearSpr(rig, buf, P);
        From("(disabled, count 3)", false, 1, 1, 3);
        From("(skip 0, count 3)", true, 0, 1, 3);
        From("(skip 1, count 3)", true, 1, 1, 3);
        From("(skip 2, count 3)", true, 2, 1, 3);
        From("(skip 3, count 3)", true, 3, 1, 3);
        From("(transfer 1, count 3)", true, 1, 1, 3);
        From("(transfer 2, count 4)", true, 1, 2, 4);
        From("(transfer 3, count 6)", true, 1, 3, 6);
        From("(transfer 2, skip 2, count 6)", true, 2, 2, 6);
        From("(transfer 3, skip 3, count 9)", true, 3, 3, 9);
        From("(transfer 1, skip 14, count 2)", true, 14, 1, 2);
        From("(transfer 1, skip 128, count 2)", true, 128, 1, 2, disp: false);
        From("(transfer 1, skip 256, count 2)", true, 256, 1, 2);
        From("(transfer 1, skip 257, count 2)", true, 257, 1, 2);
        From("(transfer 128, skip 1, count 128)", true, 1, 128, 128, disp: false);
        From("(transfer 257, skip 1, count 3)", true, 1, 257, 3);
        From("(transfer 1, skip 255, count 4)", true, 255, 1, 4, disp: false);
        P("-- TEST END\n");

        o.ToString().Should().Be(Expected("spr/interleave"));
    }

    // ---- dma/vif ----

    private static (FakeVu Vu, int Channel, Core.PlayStation2.Ps2Vif Vif) Unit(Ps2DmaRig rig, int index) =>
        index == 0 ? (rig.Vu0, 0, rig.Vif0) : (rig.Vu1, 1, rig.Vif1);

    [TestRomFact("ps2autotests/dma/vif/basic.expected")]
    public void VifBasic()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);

        for (var unit = 0; unit < 2; unit++)
        {
            var (vu, ch, vif) = Unit(rig, unit);
            P($"== VIF{unit} ==\n");
            var code = new VifPacket(rig, 64);
            code.Stcycl(4, 4);
            code.Unpack(0x60, 4, 0);
            code.Emit(0x1337C0DE);
            code.Emit(0x1337C1DE);
            code.Emit(0x1337C2DE);
            code.Emit(0x1337C3DE);
            var src = new ChainPacket(rig, 512);
            src.Tag(2, 0, code.Start); // REFE

            string Show() => $"{vu.Word(0):x8} ({vu.Word(4):x8}) - {vu.Word(16):x8} - {vu.Word(32):x8} - {vu.Word(48):x8}";

            vu.DataMemory.AsSpan(0, 64).Clear();
            for (var i = 0; i < 2; i++)
            {
                var q = BitConverter.ToUInt64(rig.Ram, (int)code.Start + (i * 16)) |
                        ((UInt128)BitConverter.ToUInt64(rig.Ram, (int)code.Start + (i * 16) + 8) << 64);
                vif.Write([q]);
            }

            P($"FIFO: {Show()}\n");
            vu.DataMemory.AsSpan(0, 64).Clear();
            rig.SendSimple(ch, code.Start, 32, P);
            P($"DMA simple: {Show()}\n");
            vu.DataMemory.AsSpan(0, 64).Clear();
            rig.SendChain(ch, src.Start, P);
            P($"DMA chain (basic): {Show()}\n");
            P("\n");
        }

        P("-- TEST END\n");
        o.ToString().Should().Be(Expected("vif/basic"));
    }

    [TestRomFact("ps2autotests/dma/vif/unpack.expected")]
    public void VifUnpack()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);

        for (var unit = 0; unit < 2; unit++)
        {
            var (vu, ch, vif) = Unit(rig, unit);
            P($"== VIF{unit} ==\n");

            void Simple(string description, uint type, bool zeroExtend, uint v0, uint v1 = 0, uint v2 = 0, uint v3 = 0)
            {
                vif.WriteRegister(0x10, 1);
                vu.DataMemory.AsSpan(0, 16).Fill(0xFF);
                var code = new VifPacket(rig, 32);
                code.Stcycl(4, 4);
                code.Unpack(type, 1, 0, zeroExtend ? 1u << 14 : 0);
                code.Emit(v0);
                code.Emit(v1);
                code.Emit(v2);
                code.Emit(v3);
                rig.SendSimple(ch, code.Start, 32, P);
                P($"Simple UNPACK {description} - {vu.Word(0):x8} - {vu.Word(4):x8} - {vu.Word(8):x8} - {vu.Word(12):x8}\n");
            }

            Simple("S8(signExtend)", 0x62, false, 0x88);
            Simple("S8(zeroExtend)", 0x62, true, 0x88);
            Simple("S16(signExtend)", 0x61, false, 0x8888);
            Simple("S16(zeroExtend)", 0x61, true, 0x8888);
            Simple("S32", 0x60, false, 0x88888888);
            Simple("V2-8(signExtend)", 0x66, false, 0x9988);
            Simple("V2-8(zeroExtend)", 0x66, true, 0x9988);
            Simple("V2-16(signExtend)", 0x65, false, 0x99998888);
            Simple("V2-16(zeroExtend)", 0x65, true, 0x99998888);
            Simple("V2-32", 0x64, false, 0x88888888, 0x99999999);
            Simple("V3-8(signExtend)", 0x6A, false, 0xAA9988);
            Simple("V3-8(zeroExtend)", 0x6A, true, 0xAA9988);
            Simple("V3-16(signExtend)", 0x69, false, 0x99998888, 0xAAAA);
            Simple("V3-16(zeroExtend)", 0x69, true, 0x99998888, 0xAAAA);
            Simple("V3-32", 0x68, false, 0x88888888, 0x99999999, 0xAAAAAAAA);
            Simple("V4-8(signExtend)", 0x6E, false, 0xBBAA9988);
            Simple("V4-8(zeroExtend)", 0x6E, true, 0xBBAA9988);
            Simple("V4-16(signExtend)", 0x6D, false, 0x99998888, 0xBBBBAAAA);
            Simple("V4-16(zeroExtend)", 0x6D, true, 0x99998888, 0xBBBBAAAA);
            Simple("V4-32", 0x6C, false, 0x88888888, 0x99999999, 0xAAAAAAAA, 0xBBBBBBBB);
            Simple("V4-5", 0x6F, false, 0xFFFF);

            // testSetUnpack
            vif.WriteRegister(0x10, 1);
            vu.DataMemory.AsSpan(0, 4096).Fill(0xFF);
            var set = new VifPacket(rig, 64);
            set.Stcol(0xFFEEDDCC, 0xBBAA9988, 0x77665544, 0x33221100);
            set.Stmask(0xAAAAAAAA);
            set.Stcycl(1, 0);
            set.Unpack(0x62, 256, 0, 0x10 << 24);
            rig.SendSimple(ch, set.Start, 64, P);
            P($"Set 4096 bytes UNPACK - {vu.Word(0):x8} - {vu.Word(4):x8} - {vu.Word(1022 * 4):x8} - {vu.Word(1023 * 4):x8}\n");

            void NoData(uint num)
            {
                vif.WriteRegister(0x10, 1);
                var before = vif.ReadRegister(0) & 3;
                var code = new VifPacket(rig, 16);
                code.Nop();
                code.Nop();
                code.Stcycl(4, 4);
                code.Unpack(0x62, num, 0);
                rig.SendSimple(ch, code.Start, 16, P);
                var after = vif.ReadRegister(0) & 3;
                P($"No data UNPACK(NUM: {num}) -> VPS before: {before}, VPS after: {after}, CODE: {vif.ReadRegister(0x80):x8}, NUM: {vif.ReadRegister(0x60)}\n");
            }

            NoData(1);
            NoData(255);
            NoData(256);
            P("\n");
        }

        P("-- TEST END\n");
        o.ToString().Should().Be(Expected("vif/unpack"));
    }

    [TestRomFact("ps2autotests/dma/vif/stcycl.expected")]
    public void VifStcycl()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);

        for (var unit = 0; unit < 2; unit++)
        {
            var (vu, ch, vif) = Unit(rig, unit);
            P($"== VIF{unit} ==\n");

            void Header(VifPacket code, uint cl, uint wl, uint size)
            {
                code.Nop();
                code.Stcycl(cl, wl);
                code.Stmask(0xAAAAAAAA);
                code.Nop();
                code.Nop();
                code.Nop();
                code.Stcol(1, 2, 3, 4);
                code.Nop();
                code.Nop();
                code.Nop();
                code.Unpack(0x6C, size, 0, 0x10 << 24);
            }

            void Show()
            {
                P($"CYCLE: {vif.ReadRegister(0x40):x8}, ");
                for (var i = 0; i < 16; i++)
                {
                    P($"{vu.Word(i * 64):x}{vu.Word((i * 64) + 16):x}{vu.Word((i * 64) + 32):x}{vu.Word((i * 64) + 48):x}");
                }

                P("\n");
            }

            void Normal(uint cl, uint wl)
            {
                vu.DataMemory.AsSpan(0, 16 * 64).Clear();
                var code = new VifPacket(rig, 16 * 12);
                Header(code, cl, wl, 8);
                for (var i = 0u; i < 32; i++)
                {
                    code.Emit(i);
                }

                rig.SendSimple(ch, code.Start, 16 * 12, P);
                Show();
            }

            void Truncated(uint cl, uint wl)
            {
                vu.DataMemory.AsSpan(0, 16 * 64).Clear();
                var first = new VifPacket(rig, 16 * 8);
                Header(first, cl, wl, 8);
                for (var i = 0u; i < 16; i++)
                {
                    first.Emit(i);
                }

                rig.SendSimple(ch, first.Start, 16 * 8, P);
                var second = new VifPacket(rig, 16 * 4);
                for (var i = 16u; i < 32; i++)
                {
                    second.Emit(i);
                }

                rig.SendSimple(ch, second.Start, 16 * 4, P);
                Show();
            }

            for (var cl = 0u; cl <= 8; cl++)
            {
                for (var wl = 0u; wl <= 8; wl++)
                {
                    P($"normal (cl {cl}, wl {wl}): ");
                    Normal(cl, wl);
                }
            }

            for (var cl = 251u; cl <= 255; cl++)
            {
                for (var wl = 0u; wl <= 8; wl++)
                {
                    P($"normal (cl {cl}, wl {wl}): ");
                    Normal(cl, wl);
                }
            }

            for (var cl = 0u; cl <= 8; cl++)
            {
                for (var wl = 251u; wl <= 255; wl++)
                {
                    P($"normal (cl {cl}, wl {wl}): ");
                    Normal(cl, wl);
                }
            }

            for (var cl = 0u; cl <= 8; cl++)
            {
                for (var wl = 0u; wl <= 8; wl++)
                {
                    P($"truncated (cl {cl}, wl {wl}): ");
                    Truncated(cl, wl);
                }
            }

            P("\n");
        }

        P("-- TEST END\n");
        o.ToString().Should().Be(Expected("vif/stcycl"));
    }

    [TestRomFact("ps2autotests/dma/vif/stmod.expected")]
    public void VifStmod()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);

        for (var unit = 0; unit < 2; unit++)
        {
            var (vu, ch, vif) = Unit(rig, unit);
            P($"== VIF{unit} ==\n");

            void Mode(string title, uint mode, bool maskEnabled, uint mask = 0)
            {
                P($"{title}:\n");
                vu.DataMemory.AsSpan(0, 64).Clear();
                var code = new VifPacket(rig, 16 * 7);
                code.Nop();
                code.Nop();
                code.Stcycl(4, 4);
                code.Strow(0x1000, 0x1000, 0x1000, 0x1000);
                code.Stmask(mask);
                code.Stmod(mode);
                code.Unpack(0x6C, 4, 0, maskEnabled ? 0x10u << 24 : 0);
                for (var i = 0u; i < 16; i++)
                {
                    code.Emit(i);
                }

                rig.SendSimple(ch, code.Start, 16 * 7, P);
                for (var i = 0; i < 4; i++)
                {
                    P($"  vumem({i * 0x10:x2}): {vu.Word(i * 16):x8} - {vu.Word((i * 16) + 4):x8} - {vu.Word((i * 16) + 8):x8} - {vu.Word((i * 16) + 12):x8}\n");
                }

                P($"  row: {vif.ReadRegister(0x100):x8} - {vif.ReadRegister(0x110):x8} - {vif.ReadRegister(0x120):x8} - {vif.ReadRegister(0x130):x8}\n");
                P($"  mode: {vif.ReadRegister(0x50):x8}\n");
                P("\n");
            }

            Mode("normal (no mask)", 0, false);
            Mode("normal (mask diagonal)", 0, true, 0x40100401);
            Mode("offset (no mask)", 1, false);
            Mode("offset (mask diagonal)", 1, true, 0x40100401);
            Mode("difference (no mask)", 2, false);
            Mode("difference (mask diagonal)", 2, true, 0x40100401);
            Mode("mode 3 (no mask)", 3, false);
            Mode("mode 3 (mask diagonal)", 3, true, 0x40100401);
            P("\n");
        }

        P("-- TEST END\n");
        o.ToString().Should().Be(Expected("vif/stmod"));
    }

    /// <remarks>
    /// One known difference: on hardware STAT's FQC reads 0 after these transfers, though the stalled VIF hasn't run
    /// the rest of the packet; here FQC counts the quadwords still waiting in the FIFO, so it is masked out.
    /// </remarks>
    [TestRomFact("ps2autotests/dma/vif/intstall.expected")]
    public void VifInterruptStall()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);

        for (var unit = 0; unit < 2; unit++)
        {
            var (_, ch, vif) = Unit(rig, unit);
            P($"== VIF{unit} ==\n");

            void Test(string name, Action<VifPacket> intCode)
            {
                vif.WriteRegister(0x10, 1);
                var code = new VifPacket(rig, 48);
                code.Stmask(0xAAAAAAAA);
                intCode(code);
                code.Stmask(0xBBBBBBBB);
                code.Nop();
                code.Nop();
                code.Stmask(0xCCCCCCCC);
                rig.SendSimple(ch, code.Start, 48, P);
                P($"{name}:\n");
                P($"  Status after transfer - MASK: {vif.ReadRegister(0x70):x8}, STAT: {vif.ReadRegister(0) & 0x00FFFFFF:x8}\n");
            }

            Test("NOP (I)", c => c.Nop(0x8000_0000));
            Test("MARK (I)", c => c.Mark(0xBEEF, 0x8000_0000));
            Test("STMASK (I)", c => c.Stmask(0xFFFFFFFF, 0x8000_0000));
            P("\n");
        }

        P("-- TEST END\n");
        o.ToString().Should().Be(Expected("vif/intstall"));
    }

    [TestRomFact("ps2autotests/dma/vif/mskpath3.expected")]
    public void VifMskpath3()
    {
        var rig = new Ps2DmaRig();
        var o = new StringBuilder("-- TEST BEGIN\n");
        void P(string s) => o.Append(s);
        uint M3p() => rig.Gif.Path3Masked ? 2u : 0u;

        P($"mask value (initial): {M3p():X8}\n");
        var on = new VifPacket(rig, 16);
        on.Mskpath3(true);
        rig.SendSimple(1, on.Start, 16, P);
        P($"mask value (after mskpath3(true))): {M3p():X8}\n");
        var off = new VifPacket(rig, 16);
        off.Mskpath3(false);
        rig.SendSimple(1, off.Start, 16, P);
        P($"mask value (after mskpath3(false))): {M3p():X8}\n");
        P("-- TEST END\n");

        o.ToString().Should().Be(Expected("vif/mskpath3"));
    }
}
