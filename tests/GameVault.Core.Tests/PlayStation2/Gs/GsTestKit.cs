using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>Register encoders (field layouts from ps2tek: "GS Reference") and a small drawing fixture.</summary>
internal static class GsRegs
{
    public static ulong Frame(int fbp, int fbw, int psm, uint fbmsk = 0) =>
        (ulong)fbp | ((ulong)fbw << 16) | ((ulong)psm << 24) | ((ulong)fbmsk << 32);

    public static ulong Zbuf(int zbp, int psm, bool zmsk = false) =>
        (ulong)zbp | ((ulong)(psm & 0xF) << 24) | (zmsk ? 1UL << 32 : 0);

    public static ulong Scissor(int x0, int x1, int y0, int y1) =>
        (ulong)x0 | ((ulong)x1 << 16) | ((ulong)y0 << 32) | ((ulong)y1 << 48);

    public static ulong Test(bool ate = false, int atst = 1, int aref = 0, int afail = 0, bool date = false, int datm = 0, bool zte = true, int ztst = 1) =>
        (ate ? 1UL : 0) | ((ulong)atst << 1) | ((ulong)aref << 4) | ((ulong)afail << 12) | (date ? 1UL << 14 : 0) |
        ((ulong)datm << 15) | (zte ? 1UL << 16 : 0) | ((ulong)ztst << 17);

    public static ulong Alpha(int a, int b, int c, int d, int fix = 0) =>
        (ulong)a | ((ulong)b << 2) | ((ulong)c << 4) | ((ulong)d << 6) | ((ulong)fix << 32);

    public static ulong Prim(int type, bool iip = false, bool tme = false, bool fge = false, bool abe = false, bool fst = false, int ctxt = 0) =>
        (ulong)type | (iip ? 1UL << 3 : 0) | (tme ? 1UL << 4 : 0) | (fge ? 1UL << 5 : 0) | (abe ? 1UL << 6 : 0) |
        (fst ? 1UL << 8 : 0) | ((ulong)ctxt << 9);

    public static ulong Rgbaq(int r, int g, int b, int a = 0x80, float q = 1) =>
        (ulong)r | ((ulong)g << 8) | ((ulong)b << 16) | ((ulong)a << 24) | ((ulong)BitConverter.SingleToUInt32Bits(q) << 32);

    /// <summary>XYZ2 from 12.4 coordinates.</summary>
    public static ulong Xyz(int x16, int y16, uint z = 0) => (ulong)(x16 & 0xFFFF) | ((ulong)(y16 & 0xFFFF) << 16) | ((ulong)z << 32);

    public static ulong Xyzf(int x16, int y16, uint z, int f) =>
        (ulong)(x16 & 0xFFFF) | ((ulong)(y16 & 0xFFFF) << 16) | ((ulong)(z & 0xFFFFFF) << 32) | ((ulong)f << 56);

    public static ulong Uv(int u16, int v16) => (ulong)u16 | ((ulong)v16 << 16);

    public static ulong St(float s, float t) =>
        BitConverter.SingleToUInt32Bits(s) | ((ulong)BitConverter.SingleToUInt32Bits(t) << 32);

    public static ulong Tex0(int tbp, int tbw, int psm, int tw, int th, bool tcc = true, int tfx = 1, int cbp = 0, int cpsm = 0, bool csm2 = false, int csa = 0, int cld = 0) =>
        (ulong)tbp | ((ulong)tbw << 14) | ((ulong)psm << 20) | ((ulong)tw << 26) | ((ulong)th << 30) | (tcc ? 1UL << 34 : 0) |
        ((ulong)tfx << 35) | ((ulong)cbp << 37) | ((ulong)cpsm << 51) | (csm2 ? 1UL << 55 : 0) | ((ulong)csa << 56) | ((ulong)cld << 61);

    public static ulong Clamp(int wms, int wmt, int minu = 0, int maxu = 0, int minv = 0, int maxv = 0) =>
        (ulong)wms | ((ulong)wmt << 2) | ((ulong)minu << 4) | ((ulong)maxu << 14) | ((ulong)minv << 24) | ((ulong)maxv << 34);

    public static ulong Tex1(bool magLinear, bool minLinear) => (magLinear ? 1UL << 5 : 0) | (minLinear ? 1UL << 6 : 0);

    public static ulong BitBltBuf(int sbp, int sbw, int spsm, int dbp, int dbw, int dpsm) =>
        (ulong)sbp | ((ulong)sbw << 16) | ((ulong)spsm << 24) | ((ulong)dbp << 32) | ((ulong)dbw << 48) | ((ulong)dpsm << 56);

    public static ulong TrxPos(int ssax, int ssay, int dsax, int dsay, int dir = 0) =>
        (ulong)ssax | ((ulong)ssay << 16) | ((ulong)dsax << 32) | ((ulong)dsay << 48) | ((ulong)dir << 59);

    public static ulong TrxReg(int w, int h) => (ulong)w | ((ulong)h << 32);
}

/// <summary>A GS with a 256-pixel-wide frame at page 0 and a Z buffer at page 100, scissor over the whole frame.</summary>
internal sealed class GsFixture
{
    public const int Fbw = 4;
    public const int ZPage = 100;
    public const int TexBlock = 0x2000;
    public const int ClutBlock = 0x3000;

    public GsFixture(int framePsm = Ps2Gs.PsmCt32, int zPsm = Ps2Gs.PsmZ32)
    {
        FramePsm = framePsm;
        Gs = new Ps2Gs();
        W(Ps2Gs.RegFrame1, GsRegs.Frame(0, Fbw, framePsm));
        W(Ps2Gs.RegZbuf1, GsRegs.Zbuf(ZPage, zPsm));
        W(Ps2Gs.RegScissor1, GsRegs.Scissor(0, 255, 0, 255));
        W(Ps2Gs.RegTest1, GsRegs.Test());
        W(Ps2Gs.RegXyOffset1, 0);
    }

    public Ps2Gs Gs { get; }

    public int FramePsm { get; }

    public void W(int register, ulong value) => Gs.WriteRegister(register, value);

    public uint Pixel(int x, int y) => Gs.ReadPixel(FramePsm, 0, Fbw, x, y);

    public uint Depth(int x, int y, int psm = Ps2Gs.PsmZ32) => Gs.ReadPixel(psm, ZPage * 32, Fbw, x, y);

    /// <summary>An untextured flat sprite from pixel corners (x0, y0) to (x1, y1), exclusive.</summary>
    public void Sprite(int x0, int y0, int x1, int y1, int r, int g, int b, int a = 0x80, uint z = 0, bool abe = false)
    {
        W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite, abe: abe));
        W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(r, g, b, a));
        W(Ps2Gs.RegXyz2, GsRegs.Xyz(x0 * 16, y0 * 16, z));
        W(Ps2Gs.RegXyz2, GsRegs.Xyz(x1 * 16, y1 * 16, z));
    }

    /// <summary>Uploads <paramref name="pixels"/> (one value per pixel, packed at the format's size) host to local.</summary>
    public void Upload(int dbp, int dbw, int psm, int x, int y, int w, int h, IReadOnlyList<uint> pixels)
    {
        W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 0, 0, dbp, dbw, psm));
        W(Ps2Gs.RegTrxPos, GsRegs.TrxPos(0, 0, x, y));
        W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(w, h));
        W(Ps2Gs.RegTrxDir, 0);
        foreach (var word in Pack(Ps2Gs.BitsPerPixel(psm), pixels))
        {
            Gs.WriteHwReg(word);
        }
    }

    /// <summary>Packs pixels into 64-bit HWREG words, low bits first, padded to a whole quadword.</summary>
    public static List<ulong> Pack(int bits, IReadOnlyList<uint> pixels)
    {
        var bytes = new List<byte>();
        ulong acc = 0;
        var accBits = 0;
        foreach (var p in pixels)
        {
            acc |= (ulong)(p & (uint)((1UL << bits) - 1)) << accBits;
            accBits += bits;
            while (accBits >= 8)
            {
                bytes.Add((byte)acc);
                acc >>= 8;
                accBits -= 8;
            }
        }

        if (accBits > 0)
        {
            bytes.Add((byte)acc);
        }

        while (bytes.Count % 16 != 0)
        {
            bytes.Add(0);
        }

        var words = new List<ulong>();
        for (var i = 0; i < bytes.Count; i += 8)
        {
            words.Add(BitConverter.ToUInt64([.. bytes.GetRange(i, 8)]));
        }

        return words;
    }
}
