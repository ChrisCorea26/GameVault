namespace GameVault.Core.Nintendo64;

/// <summary>The RDP's mode-setting commands and the TMEM loads (n64brew: "Reality Display Processor/Commands").</summary>
public sealed partial class N64Rdp
{
    // Image formats and pixel sizes (Set Color Image, Set Texture Image, Set Tile).
    public const int FormatRgba = 0;
    public const int FormatYuv = 1;
    public const int FormatCi = 2;
    public const int FormatIa = 3;
    public const int FormatI = 4;

    public const int Size4 = 0;
    public const int Size8 = 1;
    public const int Size16 = 2;
    public const int Size32 = 3;

    // Cycle types (Set Other Modes bits 53–52).
    public const int CycleOne = 0;
    public const int CycleTwo = 1;
    public const int CycleCopy = 2;
    public const int CycleFill = 3;

    private readonly Tile[] _tiles;

    private ulong _otherModes;
    private ulong _combineMode;
    private uint _fillColor;
    private uint _fogColor;
    private uint _blendColor;
    private uint _primColor;
    private uint _envColor;
    private int _primLodFraction;
    private int _primMinLevel;
    private int _primZ;
    private int _primDeltaZ;
    private ulong _keyGb;
    private ulong _keyR;
    private ulong _convert;
    private ulong _scissor;
    private Image _textureImage;
    private Image _colorImage;
    private uint _zImageAddress;

    // Decoded Set Other Modes fields.
    private int _cycleType;
    private bool _perspective;
    private bool _detailTexture;
    private bool _sharpenTexture;
    private bool _textureLod;
    private bool _tlut;
    private bool _tlutIa;
    private bool _bilinear;
    private bool _midTexel;
    private bool _biLerp0;
    private bool _biLerp1;
    private bool _convertOne;
    private bool _keyEnable;
    private int _rgbDither;
    private int _alphaDither;
    private readonly int[] _blendP = new int[2];
    private readonly int[] _blendA = new int[2];
    private readonly int[] _blendM = new int[2];
    private readonly int[] _blendB = new int[2];
    private bool _forceBlend;
    private bool _alphaCvgSelect;
    private bool _cvgTimesAlpha;
    private int _zMode;
    private int _cvgDest;
    private bool _colorOnCvg;
    private bool _imageRead;
    private bool _zUpdate;
    private bool _zCompare;
    private bool _antialias;
    private bool _zSourcePrim;
    private bool _ditherAlpha;
    private bool _alphaCompare;

    // Set Convert's K0–K5 (9-bit signed), Set Key's widths, centres and scales.
    private int _k0Filter, _k1Filter, _k2Filter, _k3Filter, _k4, _k5;
    private int _keyCenterR, _keyCenterG, _keyCenterB;
    private int _keyScaleR, _keyScaleG, _keyScaleB;
    private int _keyWidthR, _keyWidthG, _keyWidthB;

    // Scissor in 10.2 fixed point.
    private int _scissorXh, _scissorYh, _scissorXl, _scissorYl;
    private bool _scissorField, _scissorOdd;

    private readonly record struct Image(int Format, int Size, int Width, uint Address);

    private struct Tile
    {
        public ulong SetTileWord;
        public int Format;
        public int Size;
        public int Line;
        public int Tmem;
        public int Palette;
        public bool ClampT;
        public bool MirrorT;
        public int MaskT;
        public int ShiftT;
        public bool ClampS;
        public bool MirrorS;
        public int MaskS;
        public int ShiftS;

        // Set Tile Size, in 10.2 fixed point.
        public int Sl;
        public int Tl;
        public int Sh;
        public int Th;

        // Derived: whether S and T clamp (always without a mask), and the clamp limits in texels.
        public bool ClampEnabledS;
        public bool ClampEnabledT;
        public int ClampDiffS;
        public int ClampDiffT;

        public void UpdateDerived()
        {
            ClampEnabledS = ClampS || MaskS == 0;
            ClampEnabledT = ClampT || MaskT == 0;
            ClampDiffS = ((Sh >> 2) - (Sl >> 2)) & 0x3FF;
            ClampDiffT = ((Th >> 2) - (Tl >> 2)) & 0x3FF;
        }
    }

    private void ResetModes()
    {
        for (var i = 0; i < 8; i++)
        {
            SetTile((ulong)i << 24);
        }

        SetOtherModes(0);
        SetCombine(0);
        SetConvert(0);
        SetKeyGb(0);
        SetKeyR(0);
        SetScissor(0);
        UpdateConstantInputs();
    }

    private static Image DecodeImage(ulong w) =>
        new((int)(w >> 53) & 7, (int)(w >> 51) & 3, ((int)(w >> 32) & 0x3FF) + 1, (uint)w & 0x3FFFFFF);

    private static int SignExtend(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    private void SetOtherModes(ulong w)
    {
        _otherModes = w;
        _cycleType = (int)(w >> 52) & 3;
        _perspective = (w & (1UL << 51)) != 0;
        _detailTexture = (w & (1UL << 50)) != 0;
        _sharpenTexture = (w & (1UL << 49)) != 0;
        _textureLod = (w & (1UL << 48)) != 0;
        _tlut = (w & (1UL << 47)) != 0;
        _tlutIa = (w & (1UL << 46)) != 0;
        _bilinear = (w & (1UL << 45)) != 0;
        _midTexel = (w & (1UL << 44)) != 0;
        _biLerp0 = (w & (1UL << 43)) != 0;
        _biLerp1 = (w & (1UL << 42)) != 0;
        _convertOne = (w & (1UL << 41)) != 0;
        _keyEnable = (w & (1UL << 40)) != 0;
        _rgbDither = (int)(w >> 38) & 3;
        _alphaDither = (int)(w >> 36) & 3;
        _blendP[0] = (int)(w >> 30) & 3;
        _blendP[1] = (int)(w >> 28) & 3;
        _blendA[0] = (int)(w >> 26) & 3;
        _blendA[1] = (int)(w >> 24) & 3;
        _blendM[0] = (int)(w >> 22) & 3;
        _blendM[1] = (int)(w >> 20) & 3;
        _blendB[0] = (int)(w >> 18) & 3;
        _blendB[1] = (int)(w >> 16) & 3;
        _forceBlend = (w & (1UL << 14)) != 0;
        _alphaCvgSelect = (w & (1UL << 13)) != 0;
        _cvgTimesAlpha = (w & (1UL << 12)) != 0;
        _zMode = (int)(w >> 10) & 3;
        _cvgDest = (int)(w >> 8) & 3;
        _colorOnCvg = (w & (1UL << 7)) != 0;
        _imageRead = (w & (1UL << 6)) != 0;
        _zUpdate = (w & (1UL << 5)) != 0;
        _zCompare = (w & (1UL << 4)) != 0;
        _antialias = (w & (1UL << 3)) != 0;
        _zSourcePrim = (w & (1UL << 2)) != 0;
        _ditherAlpha = (w & (1UL << 1)) != 0;
        _alphaCompare = (w & 1) != 0;
        UpdateCombinerUsage();
    }

    private void SetConvert(ulong w)
    {
        _convert = w;
        // K0–K3 feed the texture unit's YUV conversion as 2K + 1 over 256; K4 and K5 are combiner inputs, raw 9-bit.
        _k0Filter = (SignExtend((int)(w >> 45) & 0x1FF, 9) << 1) + 1;
        _k1Filter = (SignExtend((int)(w >> 36) & 0x1FF, 9) << 1) + 1;
        _k2Filter = (SignExtend((int)(w >> 27) & 0x1FF, 9) << 1) + 1;
        _k3Filter = (SignExtend((int)(w >> 18) & 0x1FF, 9) << 1) + 1;
        _k4 = (int)(w >> 9) & 0x1FF;
        _k5 = (int)w & 0x1FF;
        UpdateConstantInputs();
    }

    private void SetKeyGb(ulong w)
    {
        _keyGb = w;
        _keyWidthG = (int)(w >> 44) & 0xFFF;
        _keyWidthB = (int)(w >> 32) & 0xFFF;
        _keyCenterG = (int)(w >> 24) & 0xFF;
        _keyScaleG = (int)(w >> 16) & 0xFF;
        _keyCenterB = (int)(w >> 8) & 0xFF;
        _keyScaleB = (int)w & 0xFF;
        UpdateConstantInputs();
    }

    private void SetKeyR(ulong w)
    {
        _keyR = w;
        _keyWidthR = (int)(w >> 16) & 0xFFF;
        _keyCenterR = (int)(w >> 8) & 0xFF;
        _keyScaleR = (int)w & 0xFF;
        UpdateConstantInputs();
    }

    private void SetScissor(ulong w)
    {
        _scissor = w;
        _scissorXh = (int)(w >> 44) & 0xFFF;
        _scissorYh = (int)(w >> 32) & 0xFFF;
        _scissorField = (w & (1UL << 25)) != 0;
        _scissorOdd = (w & (1UL << 24)) != 0;
        _scissorXl = (int)(w >> 12) & 0xFFF;
        _scissorYl = (int)w & 0xFFF;
    }

    private void SetTile(ulong w)
    {
        ref var tile = ref _tiles[(int)(w >> 24) & 7];
        tile.SetTileWord = w;
        tile.Format = (int)(w >> 53) & 7;
        tile.Size = (int)(w >> 51) & 3;
        tile.Line = (int)(w >> 41) & 0x1FF;
        tile.Tmem = (int)(w >> 32) & 0x1FF;
        tile.Palette = (int)(w >> 20) & 0xF;
        tile.ClampT = (w & (1UL << 19)) != 0;
        tile.MirrorT = (w & (1UL << 18)) != 0;
        tile.MaskT = Math.Min((int)(w >> 14) & 0xF, 10);
        tile.ShiftT = (int)(w >> 10) & 0xF;
        tile.ClampS = (w & (1UL << 9)) != 0;
        tile.MirrorS = (w & (1UL << 8)) != 0;
        tile.MaskS = Math.Min((int)(w >> 4) & 0xF, 10);
        tile.ShiftS = (int)w & 0xF;
        tile.UpdateDerived();
    }

    private void SetTileSize(ulong w)
    {
        ref var tile = ref _tiles[(int)(w >> 24) & 7];
        tile.Sl = (int)(w >> 44) & 0xFFF;
        tile.Tl = (int)(w >> 32) & 0xFFF;
        tile.Sh = (int)(w >> 12) & 0xFFF;
        tile.Th = (int)w & 0xFFF;
        tile.UpdateDerived();
    }

    /// <summary>Bytes per texel for sizes 8, 16 and 32 bits (4-bit images are handled as half a byte).</summary>
    private static int BytesPerTexel(int size) => size switch { Size8 => 1, Size16 => 2, Size32 => 4, _ => 0 };

    private static uint TexelBytes(int texels, int size) => (uint)(size == Size4 ? texels >> 1 : texels * BytesPerTexel(size));

    /// <summary>
    /// Load Tile: copies a rectangle of the texture image into TMEM, one 64-bit word at a time, rows a tile line
    /// apart. The 32-bit words of odd rows are swapped (the interleave that lets the texture unit read four texels at
    /// once). 32-bit RGBA tiles are split, red and green in the low half of TMEM and blue and alpha in the high half;
    /// YUV tiles too, U and V low and Y high.
    /// </summary>
    private void LoadTile(ulong w)
    {
        var tileIndex = (int)(w >> 24) & 7;
        ref var tile = ref _tiles[tileIndex];
        tile.Sl = (int)(w >> 44) & 0xFFF;
        tile.Tl = (int)(w >> 32) & 0xFFF;
        tile.Sh = (int)(w >> 12) & 0xFFF;
        tile.Th = (int)w & 0xFFF;
        tile.UpdateDerived();
        int sl = tile.Sl >> 2, tl = tile.Tl >> 2, sh = tile.Sh >> 2, th = tile.Th >> 2;
        var image = _textureImage;
        if (image.Size == Size4)
        {
            return; // The RDP can't load 4-bit images (n64brew: games load them as 8-bit).
        }

        var words = (int)((TexelBytes(sh - sl + 1, image.Size) + 7) >> 3);
        for (var t = tl; t <= th; t++)
        {
            var row = t - tl;
            var source = image.Address + TexelBytes(t * image.Width + sl, image.Size);
            var destination = (tile.Tmem + row * tile.Line) << 3;
            for (var i = 0; i < words; i++)
            {
                StoreTmemWord(ref tile, destination, i, (row & 1) != 0, ReadRdramUnaligned64(source + (uint)(i * 8)));
            }
        }
    }

    /// <summary>
    /// Load Block: copies <c>SH − SL + 1</c> texels in a straight line from the tile's TMEM address. DxT counts rows in
    /// 1.11 fixed point, one step per 64-bit word, and the words of odd rows are swapped as for Load Tile.
    /// </summary>
    private void LoadBlock(ulong w)
    {
        ref var tile = ref _tiles[(int)(w >> 24) & 7];
        var sl = (int)(w >> 44) & 0xFFF;
        var tl = (int)(w >> 32) & 0xFFF;
        var sh = (int)(w >> 12) & 0xFFF;
        var dxt = (int)w & 0xFFF;
        tile.Sl = sl;
        tile.Tl = tl;
        tile.Sh = sh;
        tile.Th = dxt;
        tile.UpdateDerived();
        var image = _textureImage;
        if (image.Size == Size4 || sh < sl)
        {
            return;
        }

        var words = (int)((TexelBytes(sh - sl + 1, image.Size) + 7) >> 3);
        var source = image.Address + TexelBytes(tl * image.Width + sl, image.Size);
        var destination = tile.Tmem << 3;
        var t = 0;
        for (var i = 0; i < words && i < 2048; i++)
        {
            StoreTmemWord(ref tile, destination, i, ((t >> 11) & 1) != 0, ReadRdramUnaligned64(source + (uint)(i * 8)));
            t += dxt;
        }
    }

    /// <summary>Stores the <paramref name="index"/>th 64-bit word of a load, formatted for the tile (see <see cref="LoadTile"/>).</summary>
    private void StoreTmemWord(ref Tile tile, int rowStart, int index, bool oddRow, ulong word)
    {
        var swap = oddRow ? 4 : 0;
        if (tile.Format == FormatYuv || (tile.Format == FormatRgba && tile.Size == Size32))
        {
            // Four bytes into each half of TMEM.
            uint low, high;
            if (tile.Format == FormatYuv)
            {
                low = (uint)(((word >> 56) & 0xFF) << 24 | ((word >> 40) & 0xFF) << 16 | ((word >> 24) & 0xFF) << 8 | ((word >> 8) & 0xFF));
                high = (uint)(((word >> 48) & 0xFF) << 24 | ((word >> 32) & 0xFF) << 16 | ((word >> 16) & 0xFF) << 8 | (word & 0xFF));
            }
            else
            {
                low = (uint)(((word >> 48) << 16) | ((word >> 16) & 0xFFFF));
                high = (uint)((((word >> 32) & 0xFFFF) << 16) | (word & 0xFFFF));
            }

            var at = rowStart + index * 4;
            for (var b = 0; b < 4; b++)
            {
                var a = ((at + b) ^ swap) & 0x7FF;
                _tmem[a] = (byte)(low >> (24 - b * 8));
                _tmem[a | 0x800] = (byte)(high >> (24 - b * 8));
            }

            return;
        }

        var start = rowStart + index * 8;
        for (var b = 0; b < 8; b++)
        {
            _tmem[((start + b) ^ swap) & 0xFFF] = (byte)(word >> (56 - b * 8));
        }
    }

    private ulong ReadRdramUnaligned64(uint address)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | ReadRdram8(address + (uint)i);
        }

        return value;
    }

    /// <summary>
    /// Load TLUT: copies 16-bit palette entries into TMEM (normally its high half). Each entry is written to all four
    /// banks, so with the usual 4-bit load tile and 16-bit image entry <c>n</c> fills the 64-bit word at
    /// <c>tmem + n × 8</c>. The hardware runs it as a load whose S steps by 0x200 >> image size, so other tile and image
    /// sizes space the entries differently (PeterLemon's LoadTLUT tests).
    /// </summary>
    private void LoadTlut(ulong w)
    {
        ref var tile = ref _tiles[(int)(w >> 24) & 7];
        tile.Sl = (int)(w >> 44) & 0xFFF;
        tile.Tl = (int)(w >> 32) & 0xFFF;
        tile.Sh = (int)(w >> 12) & 0xFFF;
        tile.Th = (int)w & 0xFFF;
        tile.UpdateDerived();
        int sl = tile.Sl >> 2, tl = tile.Tl >> 2, sh = tile.Sh >> 2;
        var image = _textureImage;
        if (image.Size == Size4 || sh < sl)
        {
            return;
        }

        // Per step: the bytes of RDRAM it moves on, the texels it covers, and its S distance in 1/8 texel.
        var (advance, texelsPerStep) = image.Size switch
        {
            Size8 => (8, 8),
            Size16 => (2, 1),
            _ => (8, 2),
        };
        var sStep = 0x40 >> image.Size;
        var tileShift = tile.Format == FormatYuv || tile.Size == Size8 ? 1 : tile.Size == Size4 ? 2 : 0;
        var source = image.Address + TexelBytes(tl * image.Width + sl, image.Size);
        var steps = (sh - sl + texelsPerStep) / texelsPerStep;
        for (var k = 0; k < steps; k++)
        {
            var entry = ReadRdram16((source + (uint)(k * advance)) & ~1u);
            var index = ((tile.Tmem << 2) + ((k * sStep) >> tileShift)) & 0x7FD;
            for (var bank = 0; bank < 4; bank++)
            {
                var at = ((index + bank) & 0x7FF) << 1;
                _tmem[at] = (byte)(entry >> 8);
                _tmem[at + 1] = (byte)entry;
            }
        }
    }
}
