using GameVault.Core.State;

namespace GameVault.Core.Genesis;

/// <summary>
/// The Genesis VDP (Sega 315-5313) in Mode 5 (SPEC §7.11, Phase 13): two scroll planes and the window, 80 sprites
/// with the per-line limits, shadow/highlight, horizontal scroll per screen, cell or line, vertical scroll per
/// screen or 2-cell column, DMA (68000 to VRAM/CRAM/VSRAM, fill, copy), the HV counter, and H- and V-interrupts.
/// </summary>
/// <remarks>
/// <para>
/// Each line is drawn as it starts, from the registers and memory at that moment, so changes made during the
/// previous line's H-blank (raster effects, palette changes) show. VRAM is kept in the 68000's byte order. The frame
/// is indices into <see cref="Palette"/>: the 9-bit colour plus 512 for normal or 1024 for highlight (0 is shadow).
/// </para>
/// <para>
/// References: Charles MacDonald, "Sega Genesis VDP documentation"; the Sega Genesis Software Manual; Plutiedev and
/// SpritesMind notes on sprite masking, the window plane and the HV counter.
/// </para>
/// </remarks>
public sealed class GenesisVdp : IStateful
{
    public const int MaxWidth = 320;
    public const int MaxHeight = 240;

    /// <summary>Master clocks per line (3420), so 488.57 68000 cycles and 228 Z80 cycles.</summary>
    public const int MasterClocksPerLine = 3420;

    private static readonly StateTag Tag = StateTag.From("GVDP");

    private readonly byte[] _vram = new byte[0x10000];
    private readonly ushort[] _cram = new ushort[64];
    private readonly ushort[] _vsram = new ushort[64];
    private readonly byte[] _registers = new byte[32];
    private readonly ushort[] _frame = new ushort[MaxWidth * MaxHeight];
    private readonly byte[] _planeA = new byte[MaxWidth];
    private readonly byte[] _planeB = new byte[MaxWidth];
    private readonly byte[] _spriteLine = new byte[MaxWidth];

    private bool _pending;
    private int _code;
    private int _address;
    private bool _fillPending;
    private ushort _readBuffer;

    private int _line;
    private int _hintCounter;
    private bool _vintPending;
    private bool _hintPending;
    private bool _vblank;
    private bool _hblank;
    private bool _spriteOverflow;
    private bool _spriteCollision;
    private bool _oddFrame;
    private int _frameWidth = 320;
    private int _frameHeight = 224;
    private int _hvLatch = -1;

    public GenesisVdp(bool pal) => Pal = pal;

    /// <summary>A 50 Hz console: 313 lines, and the V30 (240-line) mode works.</summary>
    public bool Pal { get; set; }

    public int LinesPerFrame => Pal ? 313 : 262;

    /// <summary>The frame's size, from the mode at its first line: 256 or 320 wide, 224 or 240 high.</summary>
    public int FrameWidth => _frameWidth;

    public int FrameHeight => _frameHeight;

    public ReadOnlySpan<ushort> Frame => _frame.AsSpan(0, _frameWidth * _frameHeight);

    /// <summary>The 1,536 colours: shadow (0–511), normal (512–1023) and highlight (1024–1535) of the 9-bit colours.</summary>
    public static ReadOnlySpan<uint> Palette => PaletteTable;

    private static readonly uint[] PaletteTable = BuildPalette();

    /// <summary>Reads 68000 memory for DMA: a word at an even address.</summary>
    public Func<uint, ushort>? DmaRead { get; set; }

    /// <summary>68000 cycles the last DMA transfer stalled the CPU for; the console takes them and clears this.</summary>
    public int DmaStallCycles { get; set; }

    /// <summary>Raised at the start of the vertical blank: the Z80's interrupt line goes low for about one line.</summary>
    public bool Z80Interrupt { get; private set; }

    public int Line => _line;

    public bool InVBlank => _vblank;

    public bool DisplayEnabled => (_registers[1] & 0x40) != 0;

    public bool H40 => (_registers[12] & 0x81) != 0;

    public bool V30 => Pal && (_registers[1] & 0x08) != 0;

    public int ActiveHeight => V30 ? 240 : 224;

    public byte Register(int index) => _registers[index];

    public ReadOnlySpan<byte> Vram => _vram;

    public ReadOnlySpan<ushort> Cram => _cram;

    public ReadOnlySpan<ushort> Vsram => _vsram;

    /// <summary>The interrupt level the VDP holds on the 68000's IPL pins: 6 (vertical), 4 (horizontal) or 0.</summary>
    public int InterruptLevel =>
        _vintPending && (_registers[1] & 0x20) != 0 ? 6
        : _hintPending && (_registers[0] & 0x10) != 0 ? 4
        : 0;

    public void Power()
    {
        Array.Clear(_vram);
        Array.Clear(_cram);
        Array.Clear(_vsram);
        Array.Clear(_registers);
        Array.Clear(_frame);
        _pending = false;
        _code = 0;
        _address = 0;
        _fillPending = false;
        _readBuffer = 0;
        _line = 0;
        _hintCounter = 0;
        _vintPending = _hintPending = false;
        _vblank = _hblank = false;
        _spriteOverflow = _spriteCollision = false;
        _oddFrame = false;
        _hvLatch = -1;
        Z80Interrupt = false;
    }

    /// <summary>The 68000 took the interrupt at <paramref name="level"/>: its pending flag clears.</summary>
    public void Acknowledge(int level)
    {
        if (level == 6)
        {
            _vintPending = false;
        }
        else if (level == 4)
        {
            _hintPending = false;
        }
    }

    // ---- Frame timing (driven by the console) ----

    /// <summary>The start of line <paramref name="line"/>: draws it if it's visible, and updates the counters.</summary>
    public void BeginLine(int line)
    {
        _line = line;
        _hblank = false;
        Z80Interrupt = false;
        var active = ActiveHeight;
        if (line == 0)
        {
            // The vertical blank reloads the H-interrupt counter every line, so each frame starts from R10.
            _hintCounter = _registers[10];
            _vblank = false;
            _frameWidth = H40 ? 320 : 256;
            _frameHeight = active;
            _oddFrame = !_oddFrame && (_registers[12] & 2) != 0;
        }

        if (line < active)
        {
            RenderLine(line);
        }
        else if (line == active)
        {
            // Vertical blank: the V-interrupt is flagged a little into the line, with the Z80's interrupt.
            _vblank = true;
            _vintPending = true;
            Z80Interrupt = true;
        }
    }

    /// <summary>The end of the line's active part (H-blank begins): the H-interrupt counter steps.</summary>
    public void BeginHBlank()
    {
        _hblank = true;
        if (_line <= ActiveHeight)
        {
            if (_hintCounter == 0)
            {
                _hintCounter = _registers[10];
                _hintPending = true;
            }
            else
            {
                _hintCounter--;
            }
        }
        else
        {
            _hintCounter = _registers[10];
        }
    }

    /// <summary>The HV counter at <paramref name="lineFraction"/> (0–1) of the current line.</summary>
    public ushort HvCounter(double lineFraction)
    {
        if (_hvLatch >= 0)
        {
            return (ushort)_hvLatch;
        }

        return (ushort)((VCounter() << 8) | HCounter(lineFraction));
    }

    private int VCounter()
    {
        var line = _line;
        int value;
        if (!Pal)
        {
            value = line <= 0xEA ? line : 0xE5 + (line - 0xEB); // after $EA the count jumps back to $E5
        }
        else if (V30)
        {
            value = line <= 0x10A ? line : 0x1D2 + (line - 0x10B);
        }
        else
        {
            value = line <= 0x102 ? line : 0x1CA + (line - 0x103);
        }

        return value & 0xFF;
    }

    private int HCounter(double lineFraction)
    {
        // H40 counts $00–$B6 then $E4–$FF; H32 counts $00–$93 then $E9–$FF.
        var (end, resume) = H40 ? (0xB6, 0xE4) : (0x93, 0xE9);
        var total = end + 1 + (0x100 - resume);
        var position = (int)(Math.Clamp(lineFraction, 0, 0.999) * total);
        return position <= end ? position : resume + (position - end - 1);
    }

    // ---- Ports ----

    public ushort ReadStatus(double lineFraction)
    {
        _pending = false;
        var status = 0x3400 | 0x0200; // FIFO empty
        if (_vintPending)
        {
            status |= 0x80;
        }

        if (_spriteOverflow)
        {
            status |= 0x40;
        }

        if (_spriteCollision)
        {
            status |= 0x20;
        }

        if (_oddFrame)
        {
            status |= 0x10;
        }

        if (_vblank || !DisplayEnabled)
        {
            status |= 0x08;
        }

        if (_hblank || lineFraction > 0.8)
        {
            status |= 0x04;
        }

        if (Pal)
        {
            status |= 0x01;
        }

        _spriteOverflow = false;
        _spriteCollision = false;
        return (ushort)status;
    }

    public void WriteControl(ushort value)
    {
        if (!_pending)
        {
            if ((value & 0xC000) == 0x8000)
            {
                WriteRegister((value >> 8) & 0x1F, (byte)value);
                return;
            }

            _code = (_code & 0x3C) | ((value >> 14) & 3);
            _address = (_address & 0xC000) | (value & 0x3FFF);
            _pending = true;
            return;
        }

        _pending = false;
        _code = (_code & 3) | ((value >> 2) & 0x3C);
        _address = (_address & 0x3FFF) | ((value & 3) << 14);
        if ((_code & 0x20) != 0 && (_registers[1] & 0x10) != 0)
        {
            switch (_registers[23] >> 6)
            {
                case 2:
                    _fillPending = true; // starts with the next data write
                    break;
                case 3:
                    DmaCopy();
                    break;
                default:
                    DmaTransfer();
                    break;
            }
        }

        if ((_code & 0x0F) is 0 or 4 or 8 or 0x0C)
        {
            PrefetchRead();
        }
    }

    public void WriteData(ushort value)
    {
        _pending = false;
        WriteTarget(value);
        if (_fillPending)
        {
            _fillPending = false;
            DmaFill(value);
        }
    }

    public ushort ReadData()
    {
        _pending = false;
        var value = _readBuffer;
        _address = (_address + _registers[15]) & 0xFFFF;
        PrefetchRead();
        return value;
    }

    private void PrefetchRead()
    {
        _readBuffer = (_code & 0x0F) switch
        {
            0 => (ushort)((_vram[_address & 0xFFFE] << 8) | _vram[(_address & 0xFFFE) + 1]),
            4 => (_address >> 1) < 40 ? _vsram[(_address >> 1) & 0x3F] : _vsram[0],
            8 => _cram[(_address >> 1) & 0x3F],
            0x0C => _vram[_address ^ 1],
            _ => _readBuffer,
        };
    }

    private void WriteRegister(int index, byte value)
    {
        if (index > 23)
        {
            return;
        }

        _registers[index] = value;
        if (index == 0)
        {
            _hvLatch = (value & 2) != 0 ? (VCounter() << 8) | HCounter(0) : -1;
        }
    }

    private void WriteTarget(ushort value)
    {
        switch (_code & 0x0F)
        {
            case 1:
            {
                // An odd address swaps the bytes.
                var address = _address & 0xFFFE;
                if ((_address & 1) != 0)
                {
                    _vram[address] = (byte)value;
                    _vram[address + 1] = (byte)(value >> 8);
                }
                else
                {
                    _vram[address] = (byte)(value >> 8);
                    _vram[address + 1] = (byte)value;
                }

                break;
            }

            case 3:
                _cram[(_address >> 1) & 0x3F] = (ushort)(value & 0x0EEE);
                break;
            case 5:
                if ((_address >> 1) < 40)
                {
                    _vsram[_address >> 1] = (ushort)(value & 0x07FF);
                }

                break;
        }

        _address = (_address + _registers[15]) & 0xFFFF;
    }

    // ---- DMA ----

    private int DmaLength()
    {
        var length = _registers[19] | (_registers[20] << 8);
        return length == 0 ? 0x10000 : length;
    }

    private uint DmaSource() => (uint)(_registers[21] | (_registers[22] << 8) | ((_registers[23] & 0x7F) << 16)) << 1;

    /// <summary>68000 memory to VRAM, CRAM or VSRAM. The 68000 waits while it runs.</summary>
    private void DmaTransfer()
    {
        var length = DmaLength();
        var source = DmaSource();
        for (var i = 0; i < length; i++)
        {
            var word = DmaRead?.Invoke(source & 0xFFFFFE) ?? 0;
            WriteTarget(word);

            // The source address wraps within a 128 KB window.
            source = (source & 0xFE0000) | ((source + 2) & 0x1FFFF);
        }

        UpdateDmaRegisters(source >> 1);

        // Words per line: about 205 bytes in the blank, 18 during the active display (H40); a 68000 line is 488 cycles.
        var activeDisplay = DisplayEnabled && !_vblank;
        var bytesPerLine = H40 ? (activeDisplay ? 18 : 205) : (activeDisplay ? 16 : 167);
        DmaStallCycles += (int)((long)length * 2 * 488 / bytesPerLine);
    }

    /// <summary>VRAM fill: the data write goes in normally, then the length's bytes get its upper byte.</summary>
    private void DmaFill(ushort value)
    {
        var length = DmaLength();
        var target = (_code & 0x0F) == 1 ? 0 : 1;
        var fill = (byte)(value >> 8);
        var address = (_address - _registers[15]) & 0xFFFF;
        for (var i = 0; i < length; i++)
        {
            if (target == 0)
            {
                _vram[address ^ 1] = fill;
            }
            else if ((_code & 0x0F) == 3)
            {
                _cram[(address >> 1) & 0x3F] = (ushort)(value & 0x0EEE);
            }
            else if ((_address >> 1) < 40)
            {
                _vsram[(address >> 1) & 0x3F] = (ushort)(value & 0x07FF);
            }

            address = (address + _registers[15]) & 0xFFFF;
        }

        _address = address;
        UpdateDmaRegisters(DmaSource() >> 1);
    }

    /// <summary>VRAM to VRAM, a byte at a time.</summary>
    private void DmaCopy()
    {
        var length = DmaLength();
        var source = _registers[21] | (_registers[22] << 8);
        for (var i = 0; i < length; i++)
        {
            _vram[_address ^ 1] = _vram[source ^ 1];
            source = (source + 1) & 0xFFFF;
            _address = (_address + _registers[15]) & 0xFFFF;
        }

        _registers[21] = (byte)source;
        _registers[22] = (byte)(source >> 8);
        _registers[19] = 0;
        _registers[20] = 0;
    }

    /// <summary>After a DMA the length counts down to 0 and the source address has moved past the data.</summary>
    private void UpdateDmaRegisters(uint sourceWords)
    {
        _registers[19] = 0;
        _registers[20] = 0;
        _registers[21] = (byte)sourceWords;
        _registers[22] = (byte)(sourceWords >> 8);
        _registers[23] = (byte)((_registers[23] & 0x80u) | ((sourceWords >> 16) & 0x7Fu));
    }

    // ---- Rendering ----

    private static int ScrollSize(int bits) => bits switch
    {
        0 => 32,
        1 => 64,
        3 => 128,
        _ => 32,
    };

    private void RenderLine(int line)
    {
        var width = H40 ? 320 : 256;
        var output = _frame.AsSpan(line * _frameWidth, _frameWidth);
        var background = (ushort)(512 + Color9(_cram[_registers[7] & 0x3F]));
        if (!DisplayEnabled || (_registers[1] & 0x04) == 0)
        {
            output.Fill(background);
            return;
        }

        var cells = width / 8;
        var planeWidth = ScrollSize(_registers[16] & 3);
        var planeHeight = ScrollSize((_registers[16] >> 4) & 3);

        // Horizontal scroll for this line: per screen, per 8 lines or per line.
        var hscrollBase = (_registers[13] & 0x3F) << 10;
        var hscrollOffset = (_registers[11] & 3) switch
        {
            2 => (line & ~7) * 4,
            3 => line * 4,
            _ => 0,
        };
        var hA = Word(hscrollBase + hscrollOffset) & 0x3FF;
        var hB = Word(hscrollBase + hscrollOffset + 2) & 0x3FF;

        var planeABase = (_registers[2] & 0x38) << 10;
        var planeBBase = (_registers[4] & 0x07) << 13;
        RenderPlane(_planeB, line, width, planeBBase, planeWidth, planeHeight, hB, columnScroll: 1);
        RenderPlane(_planeA, line, width, planeABase, planeWidth, planeHeight, hA, columnScroll: 0);
        RenderWindow(line, width, cells);
        RenderSprites(line, width);

        var shadowHighlight = (_registers[12] & 0x08) != 0;
        var hideLeft = (_registers[0] & 0x20) != 0;
        var count = Math.Min(width, _frameWidth);
        for (var x = 0; x < count; x++)
        {
            output[x] = hideLeft && x < 8 ? background : Compose(_planeA[x], _planeB[x], _spriteLine[x], shadowHighlight);
        }

        if (count < _frameWidth)
        {
            output[count..].Fill(background);
        }
    }

    /// <summary>
    /// Picks the visible pixel: back to front, the background colour, B low, A low, sprites low, B high, A high,
    /// sprites high. With shadow/highlight, pixels where neither plane has priority are shadowed, and sprite
    /// colours 14 and 15 of palette 3 highlight or shadow what's beneath instead of drawing.
    /// </summary>
    private ushort Compose(byte a, byte b, byte sprite, bool shadowHighlight)
    {
        var pixel = _registers[7] & 0x3F;
        var priority = -1;
        void Consider(byte layer, int rank)
        {
            if ((layer & 0x0F) != 0 && rank > priority)
            {
                priority = rank;
                pixel = layer & 0x3F;
            }
        }

        var spriteIsOperator = shadowHighlight && (sprite & 0x3F) is 0x3E or 0x3F;
        Consider(b, (b & 0x80) != 0 ? 3 : 0);
        Consider(a, (a & 0x80) != 0 ? 4 : 1);
        if (!spriteIsOperator)
        {
            Consider(sprite, (sprite & 0x80) != 0 ? 5 : 2);
        }

        var color = Color9(_cram[pixel]);
        if (!shadowHighlight)
        {
            return (ushort)(512 + color);
        }

        // 0 shadow, 1 normal, 2 highlight.
        var level = (a & 0x80) != 0 || (b & 0x80) != 0 ? 1 : 0;
        if (priority == 5)
        {
            level = 1;
        }
        else if (spriteIsOperator && (sprite & 0x0F) != 0)
        {
            var spriteAbove = (sprite & 0x80) != 0 || priority < 3;
            if (spriteAbove)
            {
                level = (sprite & 0x0F) == 0x0E ? level + 1 : 0;
            }
        }

        return (ushort)(Math.Clamp(level, 0, 2) * 512 + color);
    }

    private ushort Word(int address) => (ushort)((_vram[address & 0xFFFE] << 8) | _vram[(address & 0xFFFE) + 1]);

    private void RenderPlane(byte[] target, int line, int width, int nameBase, int planeWidth, int planeHeight, int hscroll, int columnScroll)
    {
        var columnMode = (_registers[11] & 4) != 0;
        var widthMask = planeWidth * 8 - 1;
        var heightMask = planeHeight * 8 - 1;
        for (var x = 0; x < width; x++)
        {
            var vscroll = columnMode ? _vsram[Math.Min(39, ((x >> 4) * 2) + columnScroll)] : _vsram[columnScroll];
            var px = (x - hscroll) & widthMask;
            var py = (line + vscroll) & heightMask;
            var entry = Word(nameBase + (((py >> 3) * planeWidth) + (px >> 3)) * 2);
            target[x] = TilePixel(entry, px & 7, py & 7);
        }
    }

    /// <summary>One pixel of a name-table entry's tile, as priority (bit 7), palette (bits 4–5) and colour.</summary>
    private byte TilePixel(ushort entry, int column, int row)
    {
        if ((entry & 0x1000) != 0)
        {
            row = 7 - row;
        }

        if ((entry & 0x0800) != 0)
        {
            column = 7 - column;
        }

        var tile = entry & 0x7FF;
        var data = _vram[(tile * 32 + row * 4 + (column >> 1)) & 0xFFFF];
        var color = (column & 1) == 0 ? data >> 4 : data & 0x0F;
        return (byte)((entry & 0x8000) >> 8 | ((entry >> 9) & 0x30) | color);
    }

    /// <summary>The window replaces plane A where R17/R18 put it; it doesn't scroll.</summary>
    private void RenderWindow(int line, int width, int cells)
    {
        var horizontal = _registers[17];
        var vertical = _registers[18];
        var windowLine = (vertical & 0x80) != 0 ? line >= (vertical & 0x1F) * 8 : line < (vertical & 0x1F) * 8;
        var splitX = (horizontal & 0x1F) * 16;
        var right = (horizontal & 0x80) != 0;
        if (!windowLine && splitX == 0 && !right)
        {
            return;
        }

        var nameBase = H40 ? (_registers[3] & 0x3C) << 10 : (_registers[3] & 0x3E) << 10;
        var windowCells = H40 ? 64 : 32;
        for (var x = 0; x < width; x++)
        {
            var inWindow = windowLine || (right ? x >= splitX : x < splitX);
            if (!inWindow)
            {
                continue;
            }

            var entry = Word(nameBase + (((line >> 3) * windowCells) + (x >> 3)) * 2);
            _planeA[x] = TilePixel(entry, x & 7, line & 7);
        }

        _ = cells;
    }

    private void RenderSprites(int line, int width)
    {
        Array.Clear(_spriteLine);
        var tableBase = H40 ? (_registers[5] & 0x7E) << 9 : (_registers[5] & 0x7F) << 9;
        var maxSprites = H40 ? 80 : 64;
        var perLine = H40 ? 20 : 16;
        var pixelBudget = width;
        var found = 0;
        var masked = false;
        var anyBefore = false;
        var index = 0;
        for (var n = 0; n < maxSprites; n++)
        {
            var entry = tableBase + index * 8;
            var y = (Word(entry) & 0x3FF) - 128;
            var size = _vram[(entry + 2) & 0xFFFF];
            var link = _vram[(entry + 3) & 0xFFFF] & 0x7F;
            var heightCells = (size & 3) + 1;
            var widthCells = ((size >> 2) & 3) + 1;
            if (line >= y && line < y + heightCells * 8)
            {
                if (++found > perLine)
                {
                    _spriteOverflow = true;
                    break;
                }

                var attributes = Word(entry + 4);
                var xPosition = (Word(entry + 6) & 0x1FF) - 128;
                if (xPosition == -128 && anyBefore)
                {
                    masked = true;
                }

                anyBefore = true;
                var spriteWidth = widthCells * 8;
                if (!masked)
                {
                    DrawSprite(attributes, xPosition, line - y, widthCells, heightCells, width, Math.Min(spriteWidth, pixelBudget));
                }

                pixelBudget -= spriteWidth;
                if (pixelBudget <= 0)
                {
                    _spriteOverflow = true;
                    break;
                }
            }

            if (link == 0 || link >= maxSprites)
            {
                break;
            }

            index = link;
        }
    }

    private void DrawSprite(int attributes, int x, int row, int widthCells, int heightCells, int width, int drawWidth)
    {
        var vflip = (attributes & 0x1000) != 0;
        var hflip = (attributes & 0x0800) != 0;
        var tileRow = vflip ? heightCells * 8 - 1 - row : row;
        var cellY = tileRow >> 3;
        var flags = (byte)((attributes & 0x8000) >> 8 | ((attributes >> 9) & 0x30));
        for (var sx = 0; sx < drawWidth; sx++)
        {
            var screenX = x + sx;
            if (screenX < 0 || screenX >= width)
            {
                continue;
            }

            var column = hflip ? widthCells * 8 - 1 - sx : sx;
            var tile = (attributes & 0x7FF) + (column >> 3) * heightCells + cellY;
            var data = _vram[(tile * 32 + (tileRow & 7) * 4 + ((column & 7) >> 1)) & 0xFFFF];
            var color = (column & 1) == 0 ? data >> 4 : data & 0x0F;
            if (color == 0)
            {
                continue;
            }

            if ((_spriteLine[screenX] & 0x0F) != 0)
            {
                _spriteCollision = true;
                continue; // the earlier sprite stays on top
            }

            _spriteLine[screenX] = (byte)(flags | color);
        }
    }

    /// <summary>A CRAM word (0000 BBB0 GGG0 RRR0) as a 9-bit colour (BBBGGGRRR).</summary>
    private static int Color9(ushort cram) => ((cram >> 1) & 7) | (((cram >> 5) & 7) << 3) | (((cram >> 9) & 7) << 6);

    private static uint[] BuildPalette()
    {
        // Each 3-bit level on the Genesis' nonlinear DAC; shadow is half, highlight half plus half.
        ReadOnlySpan<int> levels = [0, 52, 87, 116, 144, 172, 206, 255];
        var table = new uint[1536];
        for (var color = 0; color < 512; color++)
        {
            var r = levels[color & 7];
            var g = levels[(color >> 3) & 7];
            var b = levels[(color >> 6) & 7];
            table[color] = Argb(r / 2, g / 2, b / 2);
            table[512 + color] = Argb(r, g, b);
            table[1024 + color] = Argb(r / 2 + 128, g / 2 + 128, b / 2 + 128);
        }

        return table;
    }

    private static uint Argb(int r, int g, int b) =>
        0xFF000000u | (uint)(Math.Min(255, r) << 16) | (uint)(Math.Min(255, g) << 8) | (uint)Math.Min(255, b);

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_vram);
        foreach (var value in _cram)
        {
            writer.WriteUInt16(value);
        }

        foreach (var value in _vsram)
        {
            writer.WriteUInt16(value);
        }

        writer.WriteBytes(_registers);
        writer.WriteBool(_pending);
        writer.WriteInt32(_code);
        writer.WriteInt32(_address);
        writer.WriteBool(_fillPending);
        writer.WriteUInt16(_readBuffer);
        writer.WriteInt32(_line);
        writer.WriteInt32(_hintCounter);
        writer.WriteBool(_vintPending);
        writer.WriteBool(_hintPending);
        writer.WriteBool(_vblank);
        writer.WriteBool(_hblank);
        writer.WriteBool(_spriteOverflow);
        writer.WriteBool(_spriteCollision);
        writer.WriteBool(_oddFrame);
        writer.WriteInt32(_frameWidth);
        writer.WriteInt32(_frameHeight);
        writer.WriteInt32(_hvLatch);
        writer.WriteInt32(DmaStallCycles);
        writer.WriteBool(Z80Interrupt);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_vram);
        for (var i = 0; i < _cram.Length; i++)
        {
            _cram[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _vsram.Length; i++)
        {
            _vsram[i] = reader.ReadUInt16();
        }

        reader.ReadBytes(_registers);
        _pending = reader.ReadBool();
        _code = reader.ReadInt32();
        _address = reader.ReadInt32();
        _fillPending = reader.ReadBool();
        _readBuffer = reader.ReadUInt16();
        _line = reader.ReadInt32();
        _hintCounter = reader.ReadInt32();
        _vintPending = reader.ReadBool();
        _hintPending = reader.ReadBool();
        _vblank = reader.ReadBool();
        _hblank = reader.ReadBool();
        _spriteOverflow = reader.ReadBool();
        _spriteCollision = reader.ReadBool();
        _oddFrame = reader.ReadBool();
        _frameWidth = reader.ReadInt32();
        _frameHeight = reader.ReadInt32();
        _hvLatch = reader.ReadInt32();
        DmaStallCycles = reader.ReadInt32();
        Z80Interrupt = reader.ReadBool();
        reader.EndChunk();
    }
}
