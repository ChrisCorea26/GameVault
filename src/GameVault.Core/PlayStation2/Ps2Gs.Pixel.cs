using System.Runtime.CompilerServices;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The pixel pipeline (ps2tek: "GS Textures", "GS Fog", "GS Alpha Blending", "GS Tests and Pixel Control"): texture
/// lookup through the CLUT and TEXA, the colour function, fog, the alpha, destination-alpha and depth tests, the
/// blend, dithering, COLCLAMP, FBA and the FRAME/ZBUF masks. The draw state is decoded from the registers once per
/// change, not per pixel.
/// </summary>
public sealed partial class Ps2Gs
{
    private readonly ushort[] _clut;
    private readonly uint[] _palette = new uint[256];
    private readonly int[] _dimx = new int[16];
    private int _cbp0;
    private int _cbp1;

    private bool _drawStateDirty = true;

    // PRIM/PRMODE attributes.
    private bool _iip;
    private bool _tme;
    private bool _fge;
    private bool _abe;
    private bool _fst;

    // The context's registers, decoded.
    private int _ofx;
    private int _ofy;
    private int _scax0;
    private int _scax1;
    private int _scay0;
    private int _scay1;
    private int _fbp;
    private int _fbw;
    private int _fpsm;
    private uint _fbmsk;
    private int[] _fbTable = PageCt32;
    private bool _fb16;
    private bool _fb24;
    private int _zbp;
    private int[] _zTable = PageZ32;
    private bool _z16;
    private bool _z24;
    private uint _zMax;
    private bool _zmsk;
    private bool _ate;
    private int _atst;
    private int _aref;
    private int _afail;
    private bool _date;
    private int _datm;
    private bool _zte;
    private int _ztst;
    private int _blendA;
    private int _blendB;
    private int _blendC;
    private int _blendD;
    private int _blendFix;
    private bool _pabe;
    private bool _fba;
    private bool _colclamp;
    private bool _dthe;

    // Texture state.
    private int _tbp;
    private int _tbw;
    private int _tpsm;
    private int _texW;
    private int _texH;
    private bool _tcc;
    private int _tfx;
    private int _wms;
    private int _wmt;
    private int _minu;
    private int _maxu;
    private int _minv;
    private int _maxv;
    private bool _magLinear;
    private bool _minLinear;
    private bool _lcm;
    private int _lodL;
    private double _lodK;
    private int _ta0;
    private int _ta1;
    private bool _aem;
    private int _fogR;
    private int _fogG;
    private int _fogB;

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
        var attr = (_regs[RegPrModeCont] & 1) != 0 ? _regs[RegPrim] : _regs[RegPrMode];
        _iip = (attr & (1 << 3)) != 0;
        _tme = (attr & (1 << 4)) != 0;
        _fge = (attr & (1 << 5)) != 0;
        _abe = (attr & (1 << 6)) != 0;
        _fst = (attr & (1 << 8)) != 0;
        var ctxt = (int)((attr >> 9) & 1);

        var xyoffset = _regs[RegXyOffset1 + ctxt];
        _ofx = (int)(xyoffset & 0xFFFF);
        _ofy = (int)((xyoffset >> 32) & 0xFFFF);

        var scissor = _regs[RegScissor1 + ctxt];
        _scax0 = (int)(scissor & 0x7FF);
        _scax1 = (int)((scissor >> 16) & 0x7FF);
        _scay0 = (int)((scissor >> 32) & 0x7FF);
        _scay1 = (int)((scissor >> 48) & 0x7FF);

        var frame = _regs[RegFrame1 + ctxt];
        _fbp = (int)(frame & 0x1FF) * 32;
        _fbw = (int)((frame >> 16) & 0x3F);
        _fpsm = (int)((frame >> 24) & 0x3F);
        _fbmsk = (uint)(frame >> 32);
        _fbTable = PageTable(_fpsm);
        _fb16 = Is16Bit(_fpsm);
        _fb24 = _fpsm is PsmCt24 or PsmZ24;

        var zbuf = _regs[RegZbuf1 + ctxt];
        _zbp = (int)(zbuf & 0x1FF) * 32;
        var zpsm = (int)((zbuf >> 24) & 0xF) | 0x30;
        _zTable = PageTable(zpsm);
        _z16 = Is16Bit(zpsm);
        _z24 = zpsm == PsmZ24;
        _zMax = _z16 ? 0xFFFFu : _z24 ? 0xFF_FFFFu : 0xFFFF_FFFFu;
        _zmsk = (zbuf & (1UL << 32)) != 0;

        var test = _regs[RegTest1 + ctxt];
        _ate = (test & 1) != 0;
        _atst = (int)((test >> 1) & 7);
        _aref = (int)((test >> 4) & 0xFF);
        _afail = (int)((test >> 12) & 3);
        _date = (test & (1 << 14)) != 0;
        _datm = (int)((test >> 15) & 1);
        _zte = (test & (1 << 16)) != 0;
        _ztst = (int)((test >> 17) & 3);

        var alpha = _regs[RegAlpha1 + ctxt];
        _blendA = (int)(alpha & 3);
        _blendB = (int)((alpha >> 2) & 3);
        _blendC = (int)((alpha >> 4) & 3);
        _blendD = (int)((alpha >> 6) & 3);
        _blendFix = (int)((alpha >> 32) & 0xFF);
        _pabe = (_regs[RegPabe] & 1) != 0;
        _fba = (_regs[RegFba1 + ctxt] & 1) != 0;
        _colclamp = (_regs[RegColClamp] & 1) != 0;
        _dthe = (_regs[RegDthe] & 1) != 0;
        var dimx = _regs[RegDimx];
        for (var i = 0; i < 16; i++)
        {
            var d = (int)((dimx >> (i * 4)) & 7);
            _dimx[i] = d >= 4 ? d - 8 : d;
        }

        var texa = _regs[RegTexA];
        _ta0 = (int)(texa & 0xFF);
        _aem = (texa & (1 << 15)) != 0;
        _ta1 = (int)((texa >> 32) & 0xFF);
        var fogcol = _regs[RegFogCol];
        _fogR = (int)(fogcol & 0xFF);
        _fogG = (int)((fogcol >> 8) & 0xFF);
        _fogB = (int)((fogcol >> 16) & 0xFF);

        var tex0 = _regs[RegTex0_1 + ctxt];
        _tbp = (int)(tex0 & 0x3FFF);
        _tbw = (int)((tex0 >> 14) & 0x3F);
        _tpsm = (int)((tex0 >> 20) & 0x3F);
        _texW = 1 << Math.Min((int)((tex0 >> 26) & 0xF), 10);
        _texH = 1 << Math.Min((int)((tex0 >> 30) & 0xF), 10);
        _tcc = (tex0 & (1UL << 34)) != 0;
        _tfx = (int)((tex0 >> 35) & 3);
        var clamp = _regs[RegClamp1 + ctxt];
        _wms = (int)(clamp & 3);
        _wmt = (int)((clamp >> 2) & 3);
        _minu = (int)((clamp >> 4) & 0x3FF);
        _maxu = (int)((clamp >> 14) & 0x3FF);
        _minv = (int)((clamp >> 24) & 0x3FF);
        _maxv = (int)((clamp >> 34) & 0x3FF);
        var tex1 = _regs[RegTex1_1 + ctxt];
        _lcm = (tex1 & 1) != 0;
        _magLinear = (tex1 & (1 << 5)) != 0;
        var mmin = (int)((tex1 >> 6) & 7);
        _minLinear = mmin is 1 or 4 or 5;
        _lodL = (int)((tex1 >> 19) & 3);
        var k = (int)((tex1 >> 32) & 0xFFF);
        _lodK = (k >= 0x800 ? k - 0x1000 : k) / 16.0;

        if (_tme && IsIndexed(_tpsm))
        {
            BuildPalette(tex0);
        }
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

        if (csm2)
        {
            // CSM2: a row of PSMCT16 entries at TEXCLUT's offset in a buffer CBW wide.
            var texclut = _regs[RegTexClut];
            var cbw = (int)(texclut & 0x3F);
            var cou = (int)((texclut >> 6) & 0x3F) * 16;
            var cov = (int)((texclut >> 12) & 0x3FF);
            for (var i = 0; i < entries; i++)
            {
                _clut[i] = (ushort)ReadPixel(PsmCt16, cbp, cbw, cou + i, cov);
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
                    _clut[(csa * 16) + i] = (ushort)ReadPixel(cpsm, cbp, 1, x, y);
                }
                else
                {
                    var c = ReadPixel(PsmCt32, cbp, 1, x, y);
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
                    _clut[index] = (ushort)ReadPixel(cpsm, cbp, 1, i, j);
                }
                else
                {
                    var c = ReadPixel(PsmCt32, cbp, 1, i, j);
                    var o = (index + (csa * 16)) & 0xFF;
                    _clut[o] = (ushort)c;
                    _clut[o + 256] = (ushort)(c >> 16);
                }
            }
        }
    }

    private void BuildPalette(ulong tex0)
    {
        var cpsm = (int)((tex0 >> 51) & 0xF);
        var csa = (int)((tex0 >> 56) & 0x1F);
        var cpsm16 = cpsm is PsmCt16 or PsmCt16S || (tex0 & (1UL << 55)) != 0;
        if (Is4Bit(_tpsm))
        {
            for (var i = 0; i < 16; i++)
            {
                if (cpsm16)
                {
                    _palette[i] = Expand16(_clut[((csa * 16) + i) & 0x1FF]);
                }
                else
                {
                    var o = ((csa & 15) * 16) + i;
                    _palette[i] = ClutColor32(o, cpsm);
                }
            }

            return;
        }

        for (var i = 0; i < 256; i++)
        {
            _palette[i] = cpsm16 ? Expand16(_clut[i]) : ClutColor32(((csa * 16) + i) & 0xFF, cpsm);
        }
    }

    private uint ClutColor32(int offset, int cpsm)
    {
        var c = _clut[offset] | ((uint)_clut[offset + 256] << 16);
        return cpsm == PsmCt24 ? Expand24(c) : c;
    }

    // ---- Texels -----------------------------------------------------------------------------------------------

    /// <summary>A 16-bit colour to RGBA: 5 bits to 8 by shifting, A from TEXA (TA1 when the bit is set).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Expand16(uint c)
    {
        var rgb = ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9);
        uint a = (c & 0x8000) != 0 ? (uint)_ta1 : (!_aem || (c & 0x7FFF) != 0) ? (uint)_ta0 : 0;
        return rgb | (a << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Expand24(uint c)
    {
        c &= 0xFFFFFF;
        uint a = (!_aem || c != 0) ? (uint)_ta0 : 0;
        return c | (a << 24);
    }

    private uint FetchTexel(int u, int v)
    {
        var raw = ReadPixel(_tpsm, _tbp, _tbw, u, v);
        return _tpsm switch
        {
            PsmCt32 or PsmZ32 => raw,
            PsmCt24 or PsmZ24 => Expand24(raw),
            PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S => Expand16(raw),
            _ => _palette[raw & 0xFF],
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Wrap(int t, int mode, int size, int min, int max) => mode switch
    {
        0 => t & (size - 1),
        1 => t < 0 ? 0 : t >= size ? size - 1 : t,
        2 => t < min ? min : t > max ? max : t,
        _ => (t & min) | max,
    };

    /// <summary>A texel at 1/16-texel coordinates, nearest or bilinear (the four texels around the sample point).</summary>
    private uint SampleTexture(int u16, int v16, bool linear)
    {
        if (!linear)
        {
            return FetchTexel(Wrap(u16 >> 4, _wms, _texW, _minu, _maxu), Wrap(v16 >> 4, _wmt, _texH, _minv, _maxv));
        }

        u16 -= 8;
        v16 -= 8;
        var fu = u16 & 15;
        var fv = v16 & 15;
        var u0 = Wrap(u16 >> 4, _wms, _texW, _minu, _maxu);
        var u1 = Wrap((u16 >> 4) + 1, _wms, _texW, _minu, _maxu);
        var v0 = Wrap(v16 >> 4, _wmt, _texH, _minv, _maxv);
        var v1 = Wrap((v16 >> 4) + 1, _wmt, _texH, _minv, _maxv);
        var c00 = FetchTexel(u0, v0);
        var c10 = FetchTexel(u1, v0);
        var c01 = FetchTexel(u0, v1);
        var c11 = FetchTexel(u1, v1);
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var a = (int)((c00 >> shift) & 0xFF);
            var b = (int)((c10 >> shift) & 0xFF);
            var c = (int)((c01 >> shift) & 0xFF);
            var d = (int)((c11 >> shift) & 0xFF);
            var top = (a * (16 - fu)) + (b * fu);
            var bottom = (c * (16 - fu)) + (d * fu);
            result |= (uint)(((top * (16 - fv)) + (bottom * fv)) >> 8) << shift;
        }

        return result;
    }

    // ---- Fragments --------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToByte(double value) => value <= 0 ? 0 : value >= 255 ? 255 : (int)value;

    private static int ToTexCoord(double value) =>
        value is >= -(1 << 24) and <= 1 << 24 ? (int)Math.Floor(value) : value > 0 ? 1 << 24 : -(1 << 24);

    /// <summary>Shades one sampled pixel from its interpolated attributes and sends it through the tests.</summary>
    private void ShadeFragment(int x, int y, ref Attr attr)
    {
        var r = ToByte(attr.R);
        var g = ToByte(attr.G);
        var b = ToByte(attr.B);
        var a = ToByte(attr.A);
        var zf = attr.Z;
        var z = zf <= 0 ? 0u : zf >= uint.MaxValue ? uint.MaxValue : (uint)zf;

        if (_tme)
        {
            int u16, v16;
            var q = attr.Q;
            if (_fst)
            {
                u16 = ToTexCoord(attr.S);
                v16 = ToTexCoord(attr.T);
            }
            else
            {
                var scale = 16 / q;
                u16 = ToTexCoord(attr.S * scale * _texW);
                v16 = ToTexCoord(attr.T * scale * _texH);
            }

            bool linear;
            if (_magLinear == _minLinear)
            {
                linear = _magLinear;
            }
            else
            {
                // LOD = K when LCM is set, otherwise (log2(1/|Q|) << L) + K; above 0 minifies (level 0 only).
                var lod = _lcm ? _lodK : (Math.Log2(1 / Math.Abs(q)) * (1 << _lodL)) + _lodK;
                linear = lod > 0 ? _minLinear : _magLinear;
            }

            var tex = SampleTexture(u16, v16, linear);
            var tr = (int)(tex & 0xFF);
            var tg = (int)((tex >> 8) & 0xFF);
            var tb = (int)((tex >> 16) & 0xFF);
            var ta = (int)(tex >> 24);
            switch (_tfx)
            {
                case 0: // MODULATE
                    r = Math.Min((r * tr) >> 7, 255);
                    g = Math.Min((g * tg) >> 7, 255);
                    b = Math.Min((b * tb) >> 7, 255);
                    if (_tcc)
                    {
                        a = Math.Min((a * ta) >> 7, 255);
                    }

                    break;
                case 1: // DECAL
                    r = tr;
                    g = tg;
                    b = tb;
                    if (_tcc)
                    {
                        a = ta;
                    }

                    break;
                default: // HIGHLIGHT, HIGHLIGHT2
                    r = Math.Min(((r * tr) >> 7) + a, 255);
                    g = Math.Min(((g * tg) >> 7) + a, 255);
                    b = Math.Min(((b * tb) >> 7) + a, 255);
                    if (_tcc)
                    {
                        a = _tfx == 2 ? Math.Min(ta + a, 255) : ta;
                    }

                    break;
            }
        }

        if (_fge)
        {
            var f = ToByte(attr.F);
            r = ((f * r) + ((255 - f) * _fogR)) >> 8;
            g = ((f * g) + ((255 - f) * _fogG)) >> 8;
            b = ((f * b) + ((255 - f) * _fogB)) >> 8;
        }

        WriteFragment(x, y, z, r, g, b, a);
    }

    private bool AlphaPasses(int a) => _atst switch
    {
        0 => false,
        1 => true,
        2 => a < _aref,
        3 => a <= _aref,
        4 => a == _aref,
        5 => a >= _aref,
        6 => a > _aref,
        _ => a != _aref,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FrameAddress(int x, int y) =>
        _fb16 ? Address16(_fbTable, _fbp, _fbw, x, y) : Address32(_fbTable, _fbp, _fbw, x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ZAddress(int x, int y) =>
        _z16 ? Address16(_zTable, _zbp, _fbw, x, y) : Address32(_zTable, _zbp, _fbw, x, y);

    private uint ReadZ(int address) =>
        _z16 ? Read16(_memory, address)
        : Read32(_memory, address) & _zMax;

    private void WriteZ(int address, uint z)
    {
        if (_z16)
        {
            Write16(_memory, address, (ushort)z);
        }
        else if (_z24)
        {
            Write24(address, z);
        }
        else
        {
            Write32(_memory, address, z);
        }
    }

    /// <summary>The frame pixel as RGBA8888; a 24-bit frame reads alpha 0x80, a 16-bit one 0x80 or 0.</summary>
    private uint ReadFrameColor(int address)
    {
        if (_fb16)
        {
            uint c = Read16(_memory, address);
            return ((c & 0x1F) << 3) | ((c & 0x3E0) << 6) | ((c & 0x7C00) << 9) | ((c & 0x8000) != 0 ? 0x8000_0000u : 0);
        }

        var v = Read32(_memory, address);
        return _fb24 ? (v & 0xFFFFFF) | 0x8000_0000u : v;
    }

    /// <summary>Writes RGBA8888 under FBMSK (a set bit keeps the old bit), converting for 16- and 24-bit frames.</summary>
    private void WriteFrameColor(int address, uint color, uint mask)
    {
        if (_fb16)
        {
            var c16 = ((color >> 3) & 0x1F) | ((color >> 6) & 0x3E0) | ((color >> 9) & 0x7C00) | ((color >> 16) & 0x8000);
            var m16 = ((mask >> 3) & 0x1F) | ((mask >> 6) & 0x3E0) | ((mask >> 9) & 0x7C00) | ((mask >> 16) & 0x8000);
            if (m16 != 0)
            {
                c16 = (c16 & ~m16) | (Read16(_memory, address) & m16);
            }

            Write16(_memory, address, (ushort)c16);
            return;
        }

        if (_fb24)
        {
            mask |= 0xFF00_0000;
        }

        if (mask != 0)
        {
            color = (color & ~mask) | (Read32(_memory, address) & mask);
        }

        Write32(_memory, address, color);
    }

    /// <summary>The tests, the blend and the writes for one coloured fragment.</summary>
    private void WriteFragment(int x, int y, uint z, int r, int g, int b, int a)
    {
        var fbWrite = true;
        var zWrite = _zte && !_zmsk;
        var mask = _fbmsk;
        if (_ate && !AlphaPasses(a))
        {
            switch (_afail)
            {
                case 0: // KEEP
                    return;
                case 1: // FB_ONLY
                    zWrite = false;
                    break;
                case 2: // ZB_ONLY
                    fbWrite = false;
                    break;
                default: // RGB_ONLY
                    zWrite = false;
                    mask |= 0xFF00_0000;
                    break;
            }
        }

        // ZTE = 0 is documented as prohibited; it is taken as "no depth test and no depth write".
        var zAddress = 0;
        if (_zte)
        {
            if (z > _zMax)
            {
                z = _zMax;
            }

            switch (_ztst)
            {
                case 0:
                    return;
                case 1:
                    if (zWrite)
                    {
                        zAddress = ZAddress(x, y);
                    }

                    break;
                default:
                {
                    zAddress = ZAddress(x, y);
                    var stored = ReadZ(zAddress);
                    if (_ztst == 2 ? z < stored : z <= stored)
                    {
                        return;
                    }

                    break;
                }
            }
        }

        var fbAddress = FrameAddress(x, y);
        var needDest = _date || _abe;
        var dest = needDest ? ReadFrameColor(fbAddress) : 0;
        if (_date && !_fb24 && (int)(dest >> 31) != _datm)
        {
            return;
        }

        if (zWrite)
        {
            WriteZ(zAddress, z);
        }

        if (!fbWrite)
        {
            return;
        }

        if (_abe && !(_pabe && (a & 0x80) == 0))
        {
            var dr = (int)(dest & 0xFF);
            var dg = (int)((dest >> 8) & 0xFF);
            var db = (int)((dest >> 16) & 0xFF);
            var da = (int)(dest >> 24);
            var c = _blendC switch { 0 => a, 1 => da, _ => _blendFix };
            r = Blend(r, dr, c);
            g = Blend(g, dg, c);
            b = Blend(b, db, c);
        }

        if (_dthe && _fb16)
        {
            var d = _dimx[((y & 3) << 2) | (x & 3)];
            r += d;
            g += d;
            b += d;
        }

        if (_colclamp)
        {
            r = r < 0 ? 0 : r > 255 ? 255 : r;
            g = g < 0 ? 0 : g > 255 ? 255 : g;
            b = b < 0 ? 0 : b > 255 ? 255 : b;
        }
        else
        {
            r &= 0xFF;
            g &= 0xFF;
            b &= 0xFF;
        }

        if (_fba)
        {
            a |= 0x80;
        }

        WriteFrameColor(fbAddress, (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)(a & 0xFF) << 24), mask);
    }

    /// <summary>((A − B) × C &gt;&gt; 7) + D on one channel (ps2tek: "GS Alpha Blending").</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Blend(int source, int dest, int c)
    {
        var av = _blendA switch { 0 => source, 1 => dest, _ => 0 };
        var bv = _blendB switch { 0 => source, 1 => dest, _ => 0 };
        var dv = _blendD switch { 0 => source, 1 => dest, _ => 0 };
        return (((av - bv) * c) >> 7) + dv;
    }

    // ---- Fast paths -------------------------------------------------------------------------------------------

    /// <summary>An untextured sprite whose pixels don't depend on what is underneath can be stored directly.</summary>
    private bool CanFillFast() =>
        !_abe && (!_ate || _atst == 1) && !_date && (!_zte || _ztst == 1) && !(_dthe && _fb16);

    private void FillSpriteFast(int xs, int xe, int ys, int ye, in Vertex v)
    {
        var a = _fba ? v.A | 0x80 : v.A;
        var color = (uint)v.R | ((uint)v.G << 8) | ((uint)v.B << 16) | ((uint)(a & 0xFF) << 24);
        var writeZ = _zte && !_zmsk;
        var z = Math.Min(v.Z, _zMax);
        for (var y = ys; y <= ye; y++)
        {
            for (var x = xs; x <= xe; x++)
            {
                WriteFrameColor(FrameAddress(x, y), color, _fbmsk);
                if (writeZ)
                {
                    WriteZ(ZAddress(x, y), z);
                }
            }
        }
    }
}
