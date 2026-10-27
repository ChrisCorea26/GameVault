using System.Runtime.CompilerServices;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The pixel pipeline (ps2tek: "GS Textures", "GS Fog", "GS Alpha Blending", "GS Tests and Pixel Control"): texture
/// lookup through the CLUT and TEXA, the colour function, fog, the alpha, destination-alpha and depth tests, the
/// blend, dithering, COLCLAMP, FBA and the FRAME/ZBUF masks. The draw state is decoded from the registers once per
/// change, not per pixel, and queued primitives keep the one they were drawn with (the pipeline itself is in
/// Ps2Gs.Renderer.cs).
/// </summary>
public sealed partial class Ps2Gs
{
    private readonly ushort[] _clut;
    private int _cbp0;
    private int _cbp1;

    private bool _drawStateDirty = true;

    /// <summary>The draw state the next primitive is queued with.</summary>
    private DrawState _state;

    /// <summary>The draw state decoded from the registers: what a queued primitive is drawn with.</summary>
    private struct DrawState
    {
        // PRIM/PRMODE attributes.
        public bool Iip;
        public bool Tme;
        public bool Fge;
        public bool Abe;
        public bool Fst;
        // The context's registers, decoded.
        public int Ofx;
        public int Ofy;
        public int Scax0;
        public int Scax1;
        public int Scay0;
        public int Scay1;
        public int Fbp;
        public int Fbw;
        public int Fpsm;
        public uint Fbmsk;
        public int[] FbTable;
        public bool Fb16;
        public bool Fb24;
        public int Zbp;
        public int[] ZTable;
        public bool Z16;
        public bool Z24;
        public uint ZMax;
        public bool Zmsk;
        public bool Ate;
        public int Atst;
        public int Aref;
        public int Afail;
        public bool Date;
        public int Datm;
        public bool Zte;
        public int Ztst;
        public int BlendA;
        public int BlendB;
        public int BlendC;
        public int BlendD;
        public int BlendFix;
        public bool Pabe;
        public bool Fba;
        public bool Colclamp;
        public bool Dthe;
        public int ScanmskSkip;
        // Texture state.
        public int Tbp;
        public int Tbw;
        public int Tpsm;
        public int TexW;
        public int TexH;
        public bool Tcc;
        public int Tfx;
        public int Wms;
        public int Wmt;
        public int Minu;
        public int Maxu;
        public int Minv;
        public int Maxv;
        public bool MagLinear;
        public bool MinLinear;
        public bool Lcm;
        public int LodL;
        public double LodK;
        public int Ta0;
        public int Ta1;
        public bool Aem;
        public int FogR;
        public int FogG;
        public int FogB;

        /// <summary>DIMX's 4×4 dither matrix, −4 … 3.</summary>
        public DitherMatrix Dimx;

        /// <summary>The texture's palette (indexed formats only): a snapshot the batch owns.</summary>
        public uint[]? Palette;
    }

    [InlineArray(16)]
    private struct DitherMatrix
    {
        private int _element;
    }

    private static bool IsIndexed(int psm) => psm is PsmT8 or PsmT4 or PsmT8H or PsmT4HL or PsmT4HH;

    private static bool Is4Bit(int psm) => psm is PsmT4 or PsmT4HL or PsmT4HH;

    private static bool Is16Bit(int psm) => psm is PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S;

    private static int[] PageTable(int psm) => psm switch
    {
        PsmCt16 => PageCt16,
        PsmCt16S => PageCt16S,
        PsmZ16 => PageZ16,
        PsmZ16S => PageZ16S,
        PsmZ32 or PsmZ24 => PageZ32,
        _ => PageCt32,
    };

    private void UpdateDrawState()
    {
        _drawStateDirty = false;
        _state = default;
        var attr = (_regs[RegPrModeCont] & 1) != 0 ? _regs[RegPrim] : _regs[RegPrMode];
        _state.Iip = (attr & (1 << 3)) != 0;
        _state.Tme = (attr & (1 << 4)) != 0;
        _state.Fge = (attr & (1 << 5)) != 0;
        _state.Abe = (attr & (1 << 6)) != 0;
        _state.Fst = (attr & (1 << 8)) != 0;
        var ctxt = (int)((attr >> 9) & 1);

        var xyoffset = _regs[RegXyOffset1 + ctxt];
        _state.Ofx = (int)(xyoffset & 0xFFFF);
        _state.Ofy = (int)((xyoffset >> 32) & 0xFFFF);

        var scissor = _regs[RegScissor1 + ctxt];
        _state.Scax0 = (int)(scissor & 0x7FF);
        _state.Scax1 = (int)((scissor >> 16) & 0x7FF);
        _state.Scay0 = (int)((scissor >> 32) & 0x7FF);
        _state.Scay1 = (int)((scissor >> 48) & 0x7FF);

        var frame = _regs[RegFrame1 + ctxt];
        _state.Fbp = (int)(frame & 0x1FF) * 32;
        _state.Fbw = (int)((frame >> 16) & 0x3F);
        _state.Fpsm = (int)((frame >> 24) & 0x3F);
        _state.Fbmsk = (uint)(frame >> 32);
        _state.FbTable = PageTable(_state.Fpsm);
        _state.Fb16 = Is16Bit(_state.Fpsm);
        _state.Fb24 = _state.Fpsm is PsmCt24 or PsmZ24;

        var zbuf = _regs[RegZbuf1 + ctxt];
        _state.Zbp = (int)(zbuf & 0x1FF) * 32;
        var zpsm = (int)((zbuf >> 24) & 0xF) | 0x30;
        _state.ZTable = PageTable(zpsm);
        _state.Z16 = Is16Bit(zpsm);
        _state.Z24 = zpsm == PsmZ24;
        _state.ZMax = _state.Z16 ? 0xFFFFu : _state.Z24 ? 0xFF_FFFFu : 0xFFFF_FFFFu;
        _state.Zmsk = (zbuf & (1UL << 32)) != 0;

        var test = _regs[RegTest1 + ctxt];
        _state.Ate = (test & 1) != 0;
        _state.Atst = (int)((test >> 1) & 7);
        _state.Aref = (int)((test >> 4) & 0xFF);
        _state.Afail = (int)((test >> 12) & 3);
        _state.Date = (test & (1 << 14)) != 0;
        _state.Datm = (int)((test >> 15) & 1);
        _state.Zte = (test & (1 << 16)) != 0;
        _state.Ztst = (int)((test >> 17) & 3);

        var alpha = _regs[RegAlpha1 + ctxt];
        _state.BlendA = (int)(alpha & 3);
        _state.BlendB = (int)((alpha >> 2) & 3);
        _state.BlendC = (int)((alpha >> 4) & 3);
        _state.BlendD = (int)((alpha >> 6) & 3);
        _state.BlendFix = (int)((alpha >> 32) & 0xFF);
        _state.Pabe = (_regs[RegPabe] & 1) != 0;
        _state.Fba = (_regs[RegFba1 + ctxt] & 1) != 0;
        _state.Colclamp = (_regs[RegColClamp] & 1) != 0;
        _state.Dthe = (_regs[RegDthe] & 1) != 0;

        // SCANMSK 2 skips odd rows, 3 even rows; 0 and 1 draw every row.
        _state.ScanmskSkip = (_regs[RegScanMsk] & 3) switch { 2 => 1, 3 => 0, _ => -1 };
        var dimx = _regs[RegDimx];
        for (var i = 0; i < 16; i++)
        {
            var d = (int)((dimx >> (i * 4)) & 7);
            _state.Dimx[i] = d >= 4 ? d - 8 : d;
        }

        var texa = _regs[RegTexA];
        _state.Ta0 = (int)(texa & 0xFF);
        _state.Aem = (texa & (1 << 15)) != 0;
        _state.Ta1 = (int)((texa >> 32) & 0xFF);
        var fogcol = _regs[RegFogCol];
        _state.FogR = (int)(fogcol & 0xFF);
        _state.FogG = (int)((fogcol >> 8) & 0xFF);
        _state.FogB = (int)((fogcol >> 16) & 0xFF);

        var tex0 = _regs[RegTex0_1 + ctxt];
        _state.Tbp = (int)(tex0 & 0x3FFF);
        _state.Tbw = (int)((tex0 >> 14) & 0x3F);
        _state.Tpsm = (int)((tex0 >> 20) & 0x3F);
        _state.TexW = 1 << Math.Min((int)((tex0 >> 26) & 0xF), 10);
        _state.TexH = 1 << Math.Min((int)((tex0 >> 30) & 0xF), 10);
        _state.Tcc = (tex0 & (1UL << 34)) != 0;
        _state.Tfx = (int)((tex0 >> 35) & 3);
        var clamp = _regs[RegClamp1 + ctxt];
        _state.Wms = (int)(clamp & 3);
        _state.Wmt = (int)((clamp >> 2) & 3);
        _state.Minu = (int)((clamp >> 4) & 0x3FF);
        _state.Maxu = (int)((clamp >> 14) & 0x3FF);
        _state.Minv = (int)((clamp >> 24) & 0x3FF);
        _state.Maxv = (int)((clamp >> 34) & 0x3FF);
        var tex1 = _regs[RegTex1_1 + ctxt];
        _state.Lcm = (tex1 & 1) != 0;
        _state.MagLinear = (tex1 & (1 << 5)) != 0;
        var mmin = (int)((tex1 >> 6) & 7);
        _state.MinLinear = mmin is 1 or 4 or 5;
        _state.LodL = (int)((tex1 >> 19) & 3);
        var k = (int)((tex1 >> 32) & 0xFFF);
        _state.LodK = (k >= 0x800 ? k - 0x1000 : k) / 16.0;

        _state.Palette = null;
        if (_state.Tme && IsIndexed(_state.Tpsm))
        {
            _state.Palette = RentPalette();
            BuildPalette(tex0, _state.Palette);
        }

        _stateIndex = -1; // queued with the next primitive
    }

    // ---- CLUT -------------------------------------------------------------------------------------------------

    /// <summary>
    /// TEX0/TEX2's CLD: loads the CLUT buffer from local memory when the condition holds (ps2tek: "GS Textures").
    /// Ported from Play! (BSD-2-Clause): Source/gs/GSHandler.cpp (ProcessCLD, ReadCLUT4, ReadCLUT8). The buffer holds
    /// 512 halfwords: 16-bit entries use all of it, 32-bit entries keep their low halves in 0–255 and high halves in
    /// 256–511.
    /// </summary>
    private void LoadClut(ulong tex0)
    {
        var cbp = (int)((tex0 >> 37) & 0x3FFF);
        switch ((int)(tex0 >> 61))
        {
            case 1:
                break;
            case 2:
                _cbp0 = cbp;
                break;
            case 3:
                _cbp1 = cbp;
                break;
            case 4:
                if (_cbp0 == cbp)
                {
                    return;
                }

                _cbp0 = cbp;
                break;
            case 5:
                if (_cbp1 == cbp)
                {
                    return;
                }

                _cbp1 = cbp;
                break;
            default:
                return;
        }

        var psm = (int)((tex0 >> 20) & 0x3F);
        if (!IsIndexed(psm))
        {
            return;
        }

        var cpsm = (int)((tex0 >> 51) & 0xF);
        var csm2 = (tex0 & (1UL << 55)) != 0;
        var csa = (int)((tex0 >> 56) & 0x1F);
        var entries = Is4Bit(psm) ? 16 : 256;
        var cpsm16 = cpsm is PsmCt16 or PsmCt16S;

        // The CLUT comes from local memory: queued primitives drawing there are drawn first.
        if (csm2)
        {
            var texclut = _regs[RegTexClut];
            FlushForClut(cbp, (int)(texclut & 0x3F), PsmCt16, ((int)((texclut >> 6) & 0x3F) * 16) + entries - 1,
                (int)((texclut >> 12) & 0x3FF), (int)((texclut >> 12) & 0x3FF));
        }
        else
        {
            FlushForClut(cbp, 1, cpsm16 ? cpsm : PsmCt32, 15, 0, 15);
        }

        if (csm2)
        {
            // CSM2: a row of PSMCT16 entries at TEXCLUT's offset in a buffer CBW wide.
            var texclut = _regs[RegTexClut];
            var cbw = (int)(texclut & 0x3F);
            var cou = (int)((texclut >> 6) & 0x3F) * 16;
            var cov = (int)((texclut >> 12) & 0x3FF);
            for (var i = 0; i < entries; i++)
            {
                _clut[i] = (ushort)ReadPixel(_memory, PsmCt16, cbp, cbw, cou + i, cov);
            }

            return;
        }

        if (entries == 16)
        {
            // CSM1, 4-bit: an 8×2 block of entries.
            for (var i = 0; i < 16; i++)
            {
                var x = i & 7;
                var y = i >> 3;
                if (cpsm16)
                {
                    _clut[(csa * 16) + i] = (ushort)ReadPixel(_memory, cpsm, cbp, 1, x, y);
                }
                else
                {
                    var c = ReadPixel(_memory, PsmCt32, cbp, 1, x, y);
                    var o = ((csa & 15) * 16) + i;
                    _clut[o] = (ushort)c;
                    _clut[o + 256] = (ushort)(c >> 16);
                }
            }

            return;
        }

        // CSM1, 8-bit: a 16×16 block whose entries 8–15 and 16–23 of each 32 are swapped (index bits 3 and 4).
        for (var j = 0; j < 16; j++)
        {
            for (var i = 0; i < 16; i++)
            {
                var index = i + (j * 16);
                index = (index & ~0x18) | ((index & 0x08) << 1) | ((index & 0x10) >> 1);
                if (cpsm16)
                {
                    _clut[index] = (ushort)ReadPixel(_memory, cpsm, cbp, 1, i, j);
                }
                else
                {
                    var c = ReadPixel(_memory, PsmCt32, cbp, 1, i, j);
                    var o = (index + (csa * 16)) & 0xFF;
                    _clut[o] = (ushort)c;
                    _clut[o + 256] = (ushort)(c >> 16);
                }
            }
        }
    }

    private void BuildPalette(ulong tex0, uint[] palette)
    {
        var cpsm = (int)((tex0 >> 51) & 0xF);
        var csa = (int)((tex0 >> 56) & 0x1F);
        var cpsm16 = cpsm is PsmCt16 or PsmCt16S || (tex0 & (1UL << 55)) != 0;
        if (Is4Bit(_state.Tpsm))
        {
            for (var i = 0; i < 16; i++)
            {
                if (cpsm16)
                {
                    palette[i] = Expand16(_clut[((csa * 16) + i) & 0x1FF], in _state);
                }
                else
                {
                    var o = ((csa & 15) * 16) + i;
                    palette[i] = ClutColor32(o, cpsm);
                }
            }

            return;
        }

        for (var i = 0; i < 256; i++)
        {
            palette[i] = cpsm16 ? Expand16(_clut[i], in _state) : ClutColor32(((csa * 16) + i) & 0xFF, cpsm);
        }
    }

    private uint ClutColor32(int offset, int cpsm)
    {
        var c = _clut[offset] | ((uint)_clut[offset + 256] << 16);
        return cpsm == PsmCt24 ? Expand24(c, in _state) : c;
    }

    // ---- Colours ----------------------------------------------------------------------------------------------

    /// <summary>A 16-bit colour to RGBA: 5 bits to 8 by shifting, A from TEXA (TA1 when the bit is set).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Expand16(uint c, in DrawState s)
    {
        var rgb = ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9);
        uint a = (c & 0x8000) != 0 ? (uint)s.Ta1 : (!s.Aem || (c & 0x7FFF) != 0) ? (uint)s.Ta0 : 0;
        return rgb | (a << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Expand24(uint c, in DrawState s)
    {
        c &= 0xFFFFFF;
        uint a = (!s.Aem || c != 0) ? (uint)s.Ta0 : 0;
        return c | (a << 24);
    }
}
