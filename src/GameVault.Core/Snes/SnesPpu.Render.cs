namespace GameVault.Core.Snes;

/// <summary>The S-PPU's line renderer.</summary>
public sealed partial class SnesPpu
{
    // Layer ids for priority and colour math: BG1–BG4 are 0–3, sprites 4, the backdrop 5.
    private const int Obj = 4, Backdrop = 5;

    private const ushort Transparent = 0xFFFF;

    private readonly ushort[] _staging = new ushort[MaxWidth * MaxHeight];

    // Per-layer line buffers: colour (BGR555, or Transparent) and priority.
    private readonly ushort[][] _layerColor = [new ushort[MaxWidth], new ushort[MaxWidth], new ushort[MaxWidth], new ushort[MaxWidth], new ushort[MaxWidth]];
    private readonly byte[][] _layerPriority = [new byte[MaxWidth], new byte[MaxWidth], new byte[MaxWidth], new byte[MaxWidth], new byte[MaxWidth]];
    private readonly byte[] _objPalette = new byte[MaxWidth];
    private readonly bool[] _windowOne = new bool[256];
    private readonly bool[] _windowTwo = new bool[256];

    /// <summary>Bits per pixel of BG1–BG4 in each mode (0: the BG isn't there).</summary>
    private static readonly int[,] Depths =
    {
        { 2, 2, 2, 2 }, { 4, 4, 2, 0 }, { 4, 4, 0, 0 }, { 8, 4, 0, 0 },
        { 8, 2, 0, 0 }, { 4, 2, 0, 0 }, { 4, 0, 0, 0 }, { 8, 0, 0, 0 },
    };

    private int Mode => _bgMode & 7;

    /// <summary>Lays the frame out at the end: 512 wide if any line was hi-res (the others doubled), otherwise 256.</summary>
    public void FinishFrame()
    {
        _frameWidth = _anyHires ? 512 : 256;
        for (var y = 0; y < _frameHeight; y++)
        {
            var source = _staging.AsSpan(y * MaxWidth, MaxWidth);
            var target = _frame.AsSpan(y * _frameWidth, _frameWidth);
            if (_frameWidth == 256)
            {
                source[..256].CopyTo(target);
            }
            else if (_hiresLine[y])
            {
                source.CopyTo(target);
            }
            else
            {
                for (var x = 0; x < 256; x++)
                {
                    target[x * 2] = target[x * 2 + 1] = source[x];
                }
            }
        }
    }

    private bool RenderToLine(int y)
    {
        var output = _staging.AsSpan(y * MaxWidth, MaxWidth);
        if (ForcedBlank)
        {
            output[..256].Clear();
            return false;
        }

        var mode = Mode;
        var hires = mode is 5 or 6 || (_setini & 0x08) != 0;
        var width = hires ? 512 : 256;

        // Backgrounds and sprites for the layers either screen shows.
        var shown = _tm | _ts;
        for (var bg = 0; bg < 4; bg++)
        {
            if ((shown & (1 << bg)) != 0 && Depths[mode, bg] != 0 || (mode == 7 && bg == 1 && (_setini & 0x40) != 0 && (shown & 2) != 0))
            {
                if (mode == 7)
                {
                    RenderMode7(bg, y);
                }
                else
                {
                    RenderBackground(bg, y, width);
                }
            }
            else
            {
                Array.Fill(_layerColor[bg], Transparent);
            }
        }

        if ((shown & 0x10) != 0)
        {
            RenderSprites(y);
        }
        else
        {
            Array.Fill(_layerColor[Obj], Transparent);
        }

        BuildWindows();
        var backdrop = _cgram[0];
        var brightness = _inidisp & 0x0F;
        for (var x = 0; x < width; x++)
        {
            // In hi-res, even pixels come from the sub screen and odd pixels from the main screen.
            var px = hires && mode is 5 or 6 ? x : hires ? x >> 1 : x;
            var screenX = hires ? x >> 1 : x;
            var subPixel = hires && (x & 1) == 0;
            var (mainColor, mainLayer, mainPalette) = TopLayer(px, screenX, _tm, _tmw, backdrop);
            var (subColor, subLayer, _) = TopLayer(px, screenX, _ts, _tsw, backdrop);
            ushort color;
            if (subPixel)
            {
                color = subLayer == Backdrop && (_cgwsel & 2) != 0 ? _fixedColor : subColor;
            }
            else
            {
                color = ColorMath(screenX, mainColor, mainLayer, mainPalette, subColor, subLayer);
            }

            output[x] = Brighten(color, brightness);
        }

        return hires;
    }

    private static ushort Brighten(ushort color, int brightness)
    {
        if (brightness == 15)
        {
            return color;
        }

        var r = (color & 0x1F) * (brightness + 1) >> 4;
        var g = ((color >> 5) & 0x1F) * (brightness + 1) >> 4;
        var b = ((color >> 10) & 0x1F) * (brightness + 1) >> 4;
        return (ushort)(r | g << 5 | b << 10);
    }

    /// <summary>The front pixel of a screen: the highest-ranked opaque layer the screen shows outside its windows.</summary>
    private (ushort Color, int Layer, int Palette) TopLayer(int px, int screenX, byte enabled, byte windowed, ushort backdrop)
    {
        var bestRank = -1;
        var bestLayer = Backdrop;
        ushort bestColor = backdrop;
        for (var layer = 0; layer < 5; layer++)
        {
            if ((enabled & (1 << layer)) == 0)
            {
                continue;
            }

            var color = _layerColor[layer][layer == Obj ? screenX : px];
            if (color == Transparent)
            {
                continue;
            }

            if ((windowed & (1 << layer)) != 0 && InWindow(layer, screenX))
            {
                continue;
            }

            var rank = Rank(layer, _layerPriority[layer][layer == Obj ? screenX : px]);
            if (rank > bestRank)
            {
                bestRank = rank;
                bestLayer = layer;
                bestColor = color;
            }
        }

        return (bestColor, bestLayer, bestLayer == Obj ? _objPalette[screenX] : 0);
    }

    /// <summary>The layer order of each mode (higher is in front), from fullsnes' priority tables.</summary>
    private int Rank(int layer, int priority)
    {
        var mode = Mode;
        if (layer == Obj)
        {
            return mode switch
            {
                0 => priority switch { 3 => 11, 2 => 8, 1 => 5, _ => 2 },
                1 => priority switch { 3 => 10, 2 => 7, 1 => 4, _ => 2 },
                7 => priority switch { 3 => 10, 2 => 8, 1 => 6, _ => 3 },
                _ => priority switch { 3 => 10, 2 => 8, 1 => 5, _ => 3 },
            };
        }

        var high = priority != 0;
        return mode switch
        {
            0 => layer switch
            {
                0 => high ? 10 : 7,
                1 => high ? 9 : 6,
                2 => high ? 4 : 1,
                _ => high ? 3 : 0,
            },
            1 => layer switch
            {
                0 => high ? 9 : 6,
                1 => high ? 8 : 5,
                _ => high ? ((_bgMode & 0x08) != 0 ? 12 : 3) : 1,
            },
            7 => layer == 0 ? 4 : (high ? 7 : 1),
            _ => layer == 0 ? (high ? 9 : 4) : (high ? 7 : 1),
        };
    }

    private ushort ColorMath(int x, ushort main, int mainLayer, int mainPalette, ushort sub, int subLayer)
    {
        // Clip to black (CGWSEL bits 6–7) and prevent math (bits 4–5) by the colour window.
        var inWindow = InColorWindow(x);
        if (Region(_cgwsel >> 6, inWindow))
        {
            main = 0;
        }

        var mathEnabled = (_cgadsub & (1 << mainLayer)) != 0 && !(mainLayer == Obj && mainPalette < 4);
        if (!mathEnabled || Region((_cgwsel >> 4) & 3, inWindow))
        {
            return main;
        }

        var useSub = (_cgwsel & 2) != 0;
        var operand = useSub && subLayer != Backdrop ? sub : _fixedColor;
        var half = (_cgadsub & 0x40) != 0 && !(useSub && subLayer == Backdrop);
        var subtract = (_cgadsub & 0x80) != 0;
        int Combine(int a, int b)
        {
            var value = subtract ? a - b : a + b;
            if (half)
            {
                value >>= 1;
            }

            return Math.Clamp(value, 0, 31);
        }

        var r = Combine(main & 0x1F, operand & 0x1F);
        var g = Combine((main >> 5) & 0x1F, (operand >> 5) & 0x1F);
        var b = Combine((main >> 10) & 0x1F, (operand >> 10) & 0x1F);
        return (ushort)(r | g << 5 | b << 10);

        static bool Region(int setting, bool inside) => setting switch
        {
            0 => false,
            1 => !inside,
            2 => inside,
            _ => true,
        };
    }

    // ---- Windows ----

    private void BuildWindows()
    {
        for (var x = 0; x < 256; x++)
        {
            _windowOne[x] = x >= _wh0 && x <= _wh1;
            _windowTwo[x] = x >= _wh2 && x <= _wh3;
        }
    }

    /// <summary>Whether a layer's window settings (W12SEL/W34SEL/WOBJSEL and the logic) mask pixel x.</summary>
    private bool InWindow(int layer, int x)
    {
        var settings = layer switch
        {
            0 => _w12sel & 0x0F,
            1 => _w12sel >> 4,
            2 => _w34sel & 0x0F,
            3 => _w34sel >> 4,
            _ => _wobjsel & 0x0F,
        };
        var logic = layer < 4 ? (_wbglog >> (layer * 2)) & 3 : _wobjlog & 3;
        return Combine(settings, logic, x);
    }

    private bool InColorWindow(int x) => Combine(_wobjsel >> 4, (_wobjlog >> 2) & 3, x);

    private bool Combine(int settings, int logic, int x)
    {
        var oneEnabled = (settings & 2) != 0;
        var twoEnabled = (settings & 8) != 0;
        var one = _windowOne[x] ^ ((settings & 1) != 0);
        var two = _windowTwo[x] ^ ((settings & 4) != 0);
        if (oneEnabled && twoEnabled)
        {
            return logic switch
            {
                0 => one || two,
                1 => one && two,
                2 => one ^ two,
                _ => !(one ^ two),
            };
        }

        return oneEnabled ? one : twoEnabled && two;
    }

    // ---- Backgrounds ----

    private void RenderBackground(int bg, int y, int width)
    {
        var colors = _layerColor[bg];
        var priorities = _layerPriority[bg];
        var mode = Mode;
        var depth = Depths[mode, bg];
        var hiresBg = mode is 5 or 6;
        var large = (_bgMode & (0x10 << bg)) != 0;
        var tileWidth = large || hiresBg ? 16 : 8;
        var tileHeight = large ? 16 : 8;
        var screen = _bgScreen[bg];
        var mapBase = (screen & 0xFC) << 8;
        var mapWide = (screen & 1) != 0;
        var mapTall = (screen & 2) != 0;
        var charBase = _bgChar[bg] << 12;
        var mosaicSize = (_mosaic & (1 << bg)) != 0 ? (_mosaic >> 4) + 1 : 1;
        var lineY = y - y % mosaicSize;
        var paletteBase = mode == 0 ? bg * 32 : 0;
        var direct = depth == 8 && (_cgwsel & 1) != 0;
        var offsetPerTile = mode is 2 or 4 or 6;
        var count = hiresBg ? 512 : 256;

        for (var x = 0; x < count; x++)
        {
            var mx = (x - x % (hiresBg ? mosaicSize * 2 : mosaicSize));
            var hscroll = _hofs[bg];
            var vscroll = _vofs[bg];
            if (offsetPerTile && bg < 2)
            {
                ApplyOffsetPerTile(bg, mx >> (hiresBg ? 1 : 0), ref hscroll, ref vscroll);
            }

            var px = ((hiresBg ? hscroll * 2 : hscroll) + mx) & 0x3FFF;
            var py = (vscroll + lineY + 1) & 0x3FF;
            var tileX = px / tileWidth;
            var tileY = py / tileHeight;
            var screenX = mapWide ? (tileX >> 5) & 1 : 0;
            var screenY = mapTall ? (tileY >> 5) & 1 : 0;
            var screenIndex = screenX + screenY * (mapWide ? 2 : 1);
            var entry = _vram[(mapBase + screenIndex * 0x400 + (tileY & 31) * 32 + (tileX & 31)) & 0x7FFF];

            var fineX = px % tileWidth;
            var fineY = py % tileHeight;
            if ((entry & 0x4000) != 0)
            {
                fineX = tileWidth - 1 - fineX;
            }

            if ((entry & 0x8000) != 0)
            {
                fineY = tileHeight - 1 - fineY;
            }

            var tile = (entry & 0x3FF) + (fineX >> 3) + ((fineY >> 3) << 4);
            var pixel = TilePixel(charBase, tile & 0x3FF, depth, fineX & 7, fineY & 7);
            if (pixel == 0)
            {
                colors[x] = Transparent;
                continue;
            }

            var palette = (entry >> 10) & 7;
            colors[x] = direct ? DirectColor(pixel, palette) : _cgram[(paletteBase + palette * (1 << depth) + pixel) & 0xFF];
            priorities[x] = (byte)((entry >> 13) & 1);
        }

        if (!hiresBg && width == 512)
        {
            // Pseudo hi-res: the 256-wide layer serves both halves.
        }
    }

    /// <summary>Modes 2, 4 and 6 take each column's scroll from BG3's tilemap (offset-per-tile).</summary>
    private void ApplyOffsetPerTile(int bg, int x, ref ushort hscroll, ref ushort vscroll)
    {
        var column = ((x + (_hofs[bg] & 7)) >> 3) - 1;
        if (column < 0)
        {
            return;
        }

        var mapBase = (_bgScreen[2] & 0xFC) << 8;
        var scrolledColumn = ((column * 8 + (_hofs[2] & ~7)) >> 3) & 31;
        var row = (_vofs[2] >> 3) & 31;
        var mask = (ushort)(bg == 0 ? 0x2000 : 0x4000);
        var horizontal = _vram[(mapBase + row * 32 + scrolledColumn) & 0x7FFF];
        if (Mode == 4)
        {
            if ((horizontal & mask) != 0)
            {
                if ((horizontal & 0x8000) == 0)
                {
                    hscroll = (ushort)((horizontal & 0x3F8) | (hscroll & 7));
                }
                else
                {
                    vscroll = (ushort)(horizontal & 0x3FF);
                }
            }

            return;
        }

        var vertical = _vram[(mapBase + ((row + 1) & 31) * 32 + scrolledColumn) & 0x7FFF];
        if ((horizontal & mask) != 0)
        {
            hscroll = (ushort)((horizontal & 0x3F8) | (hscroll & 7));
        }

        if ((vertical & mask) != 0)
        {
            vscroll = (ushort)(vertical & 0x3FF);
        }
    }

    /// <summary>A tile's pixel: bitplane pairs, 8 words per pair (2bpp: 8 words; 4bpp: 16; 8bpp: 32).</summary>
    private int TilePixel(int charBase, int tile, int depth, int x, int y)
    {
        var tileWords = depth * 4;
        var address = charBase + tile * tileWords + y;
        var bit = 7 - x;
        var pixel = 0;
        for (var pair = 0; pair < depth / 2; pair++)
        {
            var word = _vram[(address + pair * 8) & 0x7FFF];
            pixel |= ((word >> bit) & 1) << (pair * 2);
            pixel |= ((word >> (8 + bit)) & 1) << (pair * 2 + 1);
        }

        return pixel;
    }

    /// <summary>Direct colour: an 8-bit pixel BBGGGRRR plus the palette bits (bgr) gives BGR555.</summary>
    private static ushort DirectColor(int pixel, int palette)
    {
        var r = ((pixel & 7) << 2) | ((palette & 1) << 1);
        var g = (((pixel >> 3) & 7) << 2) | (palette & 2);
        var b = (((pixel >> 6) & 3) << 3) | ((palette & 4) << 0);
        return (ushort)(r | g << 5 | b << 10);
    }

    // ---- Mode 7 ----

    private static int Clip(int value) => (value & 0x2000) != 0 ? value | ~0x3FF : value & 0x3FF;

    private void RenderMode7(int bg, int y)
    {
        var colors = _layerColor[bg];
        var priorities = _layerPriority[bg];
        var a = (int)_m7a;
        var b = (int)_m7b;
        var c = (int)_m7c;
        var d = (int)_m7d;
        var cx = (_m7x << 19) >> 19; // 13-bit signed
        var cy = (_m7y << 19) >> 19;
        var hofs = (_m7hofs << 19) >> 19;
        var vofs = (_m7vofs << 19) >> 19;
        var mosaicSize = (_mosaic & 1) != 0 ? (_mosaic >> 4) + 1 : 1;
        var line = (y + 1) - (y + 1) % mosaicSize;
        var screenY = (_m7sel & 2) != 0 ? 255 - line : line;
        var direct = bg == 0 && (_cgwsel & 1) != 0;
        var psx = ((a * Clip(hofs - cx)) & ~63) + ((b * Clip(vofs - cy)) & ~63) + ((b * screenY) & ~63) + (cx << 8);
        var psy = ((c * Clip(hofs - cx)) & ~63) + ((d * Clip(vofs - cy)) & ~63) + ((d * screenY) & ~63) + (cy << 8);
        for (var x = 0; x < 256; x++)
        {
            var mx = x - x % mosaicSize;
            var screenX = (_m7sel & 1) != 0 ? 255 - mx : mx;
            var px = (psx + a * screenX) >> 8;
            var py = (psy + c * screenX) >> 8;
            int tile;
            var outside = (px & ~0x3FF) != 0 || (py & ~0x3FF) != 0;
            switch (_m7sel >> 6)
            {
                case 2 when outside:
                    colors[x] = Transparent;
                    continue;
                case 3 when outside:
                    tile = 0;
                    break;
                default:
                    tile = _vram[((py & 0x3FF) >> 3) * 128 + ((px & 0x3FF) >> 3)] & 0xFF;
                    break;
            }

            var pixel = _vram[(tile * 64 + (py & 7) * 8 + (px & 7)) & 0x7FFF] >> 8;
            if (bg == 1)
            {
                // EXTBG: bit 7 is the priority and the colour has 7 bits.
                priorities[x] = (byte)(pixel >> 7);
                pixel &= 0x7F;
            }
            else
            {
                priorities[x] = 0;
            }

            colors[x] = pixel == 0 ? Transparent : direct ? DirectColor(pixel, 0) : _cgram[pixel];
        }
    }

    // ---- Sprites ----

    private static readonly (int Small, int Large)[] SpriteSizes =
    [
        (8, 16), (8, 32), (8, 64), (16, 32), (16, 64), (32, 64), (16, 32), (16, 32),
    ];

    private (int Width, int Height) SpriteSize(bool large)
    {
        var sizes = _obsel >> 5;
        var size = large ? SpriteSizes[sizes].Large : SpriteSizes[sizes].Small;
        return sizes switch
        {
            6 when !large => (16, 32),
            6 => (32, 64),
            7 when !large => (16, 32),
            7 => (32, 32),
            _ => (size, size),
        };
    }

    private void RenderSprites(int y)
    {
        var colors = _layerColor[Obj];
        var priorities = _layerPriority[Obj];
        Array.Fill(colors, Transparent);
        var line = y; // sprites show one line below their Y relative to the backgrounds
        var first = _oamPriorityRotation ? (_oamAddressReload & 0xFE) >> 1 & 0x7F : 0;
        Span<int> inRange = stackalloc int[32];
        var count = 0;
        for (var n = 0; n < 128; n++)
        {
            var index = (first + n) & 0x7F;
            var high = _oam[0x200 + (index >> 2)] >> ((index & 3) * 2);
            var (_, height) = SpriteSize((high & 2) != 0);
            var spriteY = _oam[index * 4 + 1];
            var row = (line - spriteY) & 0xFF;
            if (row >= height)
            {
                continue;
            }

            if (count == 32)
            {
                _rangeOver = true;
                break;
            }

            inRange[count++] = index;
        }

        // Draw back to front so the lowest index ends on top; at most 34 eight-pixel slivers per line.
        var slivers = 0;
        var nameBase = (_obsel & 7) << 13;
        var nameGap = ((_obsel >> 3) & 3) + 1 << 12;
        for (var i = count - 1; i >= 0; i--)
        {
            var index = inRange[i];
            var high = _oam[0x200 + (index >> 2)] >> ((index & 3) * 2);
            var (width, height) = SpriteSize((high & 2) != 0);
            var x = _oam[index * 4] | (high & 1) << 8;
            if (x >= 256)
            {
                x -= 512;
            }

            var spriteY = _oam[index * 4 + 1];
            var tile = _oam[index * 4 + 2];
            var attributes = _oam[index * 4 + 3];
            var row = (line - spriteY) & 0xFF;
            if ((attributes & 0x80) != 0)
            {
                row = height - 1 - row;
            }

            slivers += width / 8;
            if (slivers > 34)
            {
                _timeOver = true;
            }

            var palette = (attributes >> 1) & 7;
            var priority = (attributes >> 4) & 3;
            var table = (attributes & 1) != 0 ? nameBase + nameGap : nameBase;
            for (var column = 0; column < width; column++)
            {
                var screenX = x + column;
                if (screenX is < 0 or >= 256)
                {
                    continue;
                }

                var sx = (attributes & 0x40) != 0 ? width - 1 - column : column;
                var cell = ((tile & 0x0F) + (sx >> 3)) & 0x0F | ((tile & 0xF0) + ((row >> 3) << 4)) & 0xF0;
                var pixel = TilePixel(table & 0x7FFF, cell, 4, sx & 7, row & 7);
                if (pixel == 0)
                {
                    continue;
                }

                colors[screenX] = _cgram[128 + palette * 16 + pixel];
                priorities[screenX] = (byte)priority;
                _objPalette[screenX] = (byte)palette;
            }
        }
    }
}
