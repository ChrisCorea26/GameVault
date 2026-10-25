using System.Runtime.CompilerServices;
using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Ppu;

/// <summary>
/// The 2C02 picture processing unit, emulated one dot at a time.
/// </summary>
/// <remarks>
/// Behavior follows the NESdev Wiki pages "PPU registers", "PPU scrolling", "PPU rendering",
/// "PPU sprite evaluation", "PPU frame timing" and "PPU palettes". <see cref="Scanline"/> and
/// <see cref="Dot"/> name the next dot to be processed: lines 0–239 are visible, 241 starts VBlank,
/// and 261 is the pre-render line.
/// </remarks>
public sealed class Ppu2C02 : IStateful
{
    public const int Width = 256;
    public const int Height = 240;
    public const int DotsPerScanline = 341;
    public const int ScanlinesPerFrame = 262;

    private const int VBlankScanline = 241;
    private const int PreRenderScanline = 261;

    /// <summary>Frames (about 600 ms) before an unrefreshed open-bus bit decays to 0 (NESdev Wiki: "Open bus behavior").</summary>
    private const int OpenBusDecayFrames = 36;

    private static readonly StateTag Tag = StateTag.From("PPU ");

    private readonly IMapper _mapper;
    private readonly byte[] _ciram;
    private readonly byte[] _palette = new byte[32];
    private readonly byte[] _oam = new byte[256];
    private readonly byte[] _secondaryOam = new byte[32];
    private readonly ushort[] _frame = new ushort[Width * Height];

    // Registers
    private byte _ctrl;
    private byte _mask;
    private bool _vblank;
    private bool _sprite0Hit;
    private bool _spriteOverflow;
    private byte _oamAddr;
    private ushort _v;
    private ushort _t;
    private byte _fineX;
    private bool _w;
    private byte _readBuffer;
    private bool _suppressVblank;

    // Open bus (I/O latch) and the frame each bit was last refreshed on.
    private byte _ioLatch;
    private readonly long[] _ioLatchRefreshed = new long[8];

    // Background pipeline
    private byte _ntLatch;
    private byte _atLatch;
    private byte _patternLoLatch;
    private byte _patternHiLatch;
    private ushort _bgShiftLo;
    private ushort _bgShiftHi;
    private ushort _atShiftLo;
    private ushort _atShiftHi;

    // Sprite evaluation (for the next line) and sprite units (for the current line)
    private byte _oamCopy;
    private int _evalN;
    private int _evalM;
    private int _secondaryIndex;
    private int _spritesFound;
    private bool _evalDone;
    private bool _sprite0Next;

    private readonly byte[] _spritePatternLo = new byte[64];
    private readonly byte[] _spritePatternHi = new byte[64];
    private readonly byte[] _spriteAttr = new byte[64];
    private readonly byte[] _spriteX = new byte[64];
    private int _spriteCount;
    private bool _sprite0OnLine;

    private bool _lastA12;
    private bool _skipOddDot;

    public Ppu2C02(IMapper mapper)
    {
        _mapper = mapper;
        _ciram = new byte[mapper.Mirroring == Mirroring.FourScreen ? 0x1000 : 0x800];
    }

    public int Scanline { get; private set; }

    public int Dot { get; private set; }

    public long FrameCount { get; private set; }

    /// <summary>256×240 pixels: palette index (bits 0–5) | emphasis (bits 6–8), after grayscale.</summary>
    public ReadOnlySpan<ushort> FrameBuffer => _frame;

    /// <summary>Level of the /NMI output: VBlank flag AND NMI enable ($2000 bit 7).</summary>
    public bool NmiOutput => _vblank && (_ctrl & 0x80) != 0;

    /// <summary>Draw every sprite on a line instead of the hardware limit of 8. Flags and timing are unchanged.</summary>
    public bool RemoveSpriteLimit { get; set; }

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    // Exposed for unit tests.
    internal ushort V => _v;

    internal ushort T => _t;

    internal byte FineX => _fineX;

    internal bool WriteToggle => _w;

    public void Power()
    {
        Scanline = Dot = 0;
        FrameCount = 0;
        _ctrl = _mask = _oamAddr = _fineX = _readBuffer = _ioLatch = 0;
        _v = _t = 0;
        _w = _vblank = _sprite0Hit = _spriteOverflow = _suppressVblank = false;
        Array.Clear(_ioLatchRefreshed);
        Array.Clear(_ciram);
        Array.Clear(_palette);
        Array.Clear(_oam);
        Array.Clear(_frame);
        _spriteCount = 0;
        _lastA12 = false;
    }

    /// <summary>The console's reset line clears $2000, $2001, the write toggle and the read buffer.</summary>
    public void Reset()
    {
        _ctrl = _mask = 0;
        _w = false;
        _readBuffer = 0;
    }

    // ------------------------------------------------------------------ CPU interface

    public byte ReadRegister(ushort address)
    {
        switch (address & 7)
        {
            case 2:
            {
                var value = (byte)((_vblank ? 0x80 : 0) | (_sprite0Hit ? 0x40 : 0) | (_spriteOverflow ? 0x20 : 0)
                    | (DecayedLatch() & 0x1F));

                // Reading one dot before VBlank starts reads it clear and prevents it this frame.
                if (Scanline == VBlankScanline && Dot == 1)
                {
                    _suppressVblank = true;
                }

                _vblank = false;
                _w = false;
                RefreshLatch(value, 0xE0);
                return value;
            }

            case 4:
            {
                byte value;
                if (RenderingEnabled && (Scanline < 240 || Scanline == PreRenderScanline) && Dot is >= 1 and <= 64)
                {
                    value = 0xFF; // secondary OAM is being cleared
                }
                else if (RenderingEnabled && Scanline < 240 && Dot is >= 65 and <= 256)
                {
                    value = _oamCopy;
                }
                else
                {
                    value = _oam[_oamAddr];
                    if ((_oamAddr & 3) == 2)
                    {
                        value &= 0xE3; // attribute bits 2-4 don't exist
                    }
                }

                RefreshLatch(value, 0xFF);
                return value;
            }

            case 7:
            {
                var vramAddress = (ushort)(_v & 0x3FFF);
                byte value;
                if (vramAddress >= 0x3F00)
                {
                    // Palette reads are immediate; the buffer gets the nametable byte "underneath".
                    value = (byte)((ReadPalette(vramAddress) & 0x3F) | (DecayedLatch() & 0xC0));
                    _readBuffer = ReadVram((ushort)(vramAddress - 0x1000));
                    RefreshLatch(value, 0x3F);
                }
                else
                {
                    value = _readBuffer;
                    _readBuffer = ReadVram(vramAddress);
                    RefreshLatch(value, 0xFF);
                }

                IncrementAfterDataAccess();
                return value;
            }

            default:
                return DecayedLatch();
        }
    }

    public void WriteRegister(ushort address, byte value)
    {
        RefreshLatch(value, 0xFF);
        switch (address & 7)
        {
            case 0:
                _ctrl = value;
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10));
                break;

            case 1:
                _mask = value;
                break;

            case 3:
                _oamAddr = value;
                break;

            case 4:
                if (RenderingEnabled && (Scanline < 240 || Scanline == PreRenderScanline))
                {
                    _oamAddr += 4; // writes during rendering only bump the high 6 bits
                }
                else
                {
                    _oam[_oamAddr++] = value;
                }

                break;

            case 5:
                if (!_w)
                {
                    _t = (ushort)((_t & 0xFFE0) | (value >> 3));
                    _fineX = (byte)(value & 7);
                }
                else
                {
                    _t = (ushort)((_t & 0x8C1F) | ((value & 0x07) << 12) | ((value & 0xF8) << 2));
                }

                _w = !_w;
                break;

            case 6:
                if (!_w)
                {
                    _t = (ushort)((_t & 0x00FF) | ((value & 0x3F) << 8));
                }
                else
                {
                    _t = (ushort)((_t & 0xFF00) | value);
                    _v = _t;
                }

                _w = !_w;
                break;

            case 7:
            {
                var vramAddress = (ushort)(_v & 0x3FFF);
                if (vramAddress >= 0x3F00)
                {
                    WritePalette(vramAddress, value);
                }
                else
                {
                    WriteVram(vramAddress, value);
                }

                IncrementAfterDataAccess();
                break;
            }
        }
    }

    /// <summary>Side-effect-free palette read for debuggers.</summary>
    public byte PeekPalette(int index) => _palette[PaletteIndex(index)];

    private void IncrementAfterDataAccess()
    {
        if (RenderingEnabled && (Scanline < 240 || Scanline == PreRenderScanline))
        {
            // During rendering, $2007 access triggers the fetch-unit increments (NESdev Wiki: "PPU scrolling").
            IncrementCoarseX();
            IncrementY();
        }
        else
        {
            _v = (ushort)((_v + ((_ctrl & 0x04) != 0 ? 32 : 1)) & 0x7FFF);
        }
    }

    // ------------------------------------------------------------------ open bus

    private byte DecayedLatch()
    {
        var frame = FrameCount;
        for (var bit = 0; bit < 8; bit++)
        {
            if (frame - _ioLatchRefreshed[bit] > OpenBusDecayFrames)
            {
                _ioLatch &= (byte)~(1 << bit);
            }
        }

        return _ioLatch;
    }

    private void RefreshLatch(byte value, byte bits)
    {
        _ioLatch = (byte)((_ioLatch & ~bits) | (value & bits));
        for (var bit = 0; bit < 8; bit++)
        {
            if ((bits & (1 << bit)) != 0)
            {
                _ioLatchRefreshed[bit] = FrameCount;
            }
        }
    }

    // ------------------------------------------------------------------ PPU memory

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackA12(ushort address)
    {
        var a12 = (address & 0x1000) != 0;
        if (a12 && !_lastA12)
        {
            _mapper.OnA12Rise();
        }

        _lastA12 = a12;
    }

    private byte ReadVram(ushort address)
    {
        address &= 0x3FFF;
        TrackA12(address);
        if (address < 0x2000)
        {
            return _mapper.PpuRead(address);
        }

        return _ciram[NametableIndex(address)];
    }

    private void WriteVram(ushort address, byte value)
    {
        address &= 0x3FFF;
        TrackA12(address);
        if (address < 0x2000)
        {
            _mapper.PpuWrite(address, value);
        }
        else
        {
            _ciram[NametableIndex(address)] = value;
        }
    }

    /// <summary>Maps $2000-$3EFF onto CIRAM using the cartridge's mirroring (NESdev Wiki: "Mirroring").</summary>
    private int NametableIndex(ushort address) => _mapper.Mirroring switch
    {
        Mirroring.Vertical => address & 0x07FF,
        Mirroring.Horizontal => ((address >> 1) & 0x0400) | (address & 0x03FF),
        Mirroring.SingleScreenA => address & 0x03FF,
        Mirroring.SingleScreenB => 0x0400 | (address & 0x03FF),
        _ => address & (_ciram.Length - 1),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PaletteIndex(int address)
    {
        address &= 0x1F;
        return (address & 0x13) == 0x10 ? address & ~0x10 : address; // $3F10/14/18/1C mirror $3F00/04/08/0C
    }

    private byte ReadPalette(ushort address)
    {
        var value = _palette[PaletteIndex(address)];
        return (_mask & 0x01) != 0 ? (byte)(value & 0x30) : value;
    }

    private void WritePalette(ushort address, byte value) => _palette[PaletteIndex(address)] = (byte)(value & 0x3F);

    // ------------------------------------------------------------------ dot clock

    /// <summary>Processes one dot and advances the dot/scanline counters.</summary>
    public void Tick()
    {
        var scanline = Scanline;
        var dot = Dot;

        if (scanline < 240)
        {
            RenderLine(scanline, dot, visible: true);
        }
        else if (scanline == VBlankScanline)
        {
            if (dot == 1)
            {
                if (!_suppressVblank)
                {
                    _vblank = true;
                }

                _suppressVblank = false;
            }
        }
        else if (scanline == PreRenderScanline)
        {
            if (dot == 1)
            {
                _vblank = false;
                _sprite0Hit = false;
                _spriteOverflow = false;
            }

            RenderLine(scanline, dot, visible: false);
        }

        // With rendering on, odd frames skip the last dot of the pre-render line. The decision is
        // latched at dot 338, so enabling rendering on dot 339 is too late (ppu_vbl_nmi 10-even_odd_timing).
        if (scanline == PreRenderScanline && dot == 338)
        {
            _skipOddDot = (FrameCount & 1) == 1 && RenderingEnabled;
        }

        dot++;
        if (scanline == PreRenderScanline && dot == DotsPerScanline - 1 && _skipOddDot)
        {
            dot++;
        }

        if (dot == DotsPerScanline)
        {
            dot = 0;
            if (++scanline == ScanlinesPerFrame)
            {
                scanline = 0;
                FrameCount++;
            }
        }

        Scanline = scanline;
        Dot = dot;
    }

    private void RenderLine(int scanline, int dot, bool visible)
    {
        var rendering = RenderingEnabled;

        if (visible && dot is >= 1 and <= 256)
        {
            // Pixel x = dot - 1. On dots 2..257 the shifters move first (see below), so draw after shifting.
            if (dot >= 2 && rendering)
            {
                ShiftBackground();
            }

            if (rendering)
            {
                BackgroundFetch(dot);
            }

            DrawPixel(scanline, dot - 1);
        }
        else if (rendering)
        {
            if (dot == 257 || (dot is >= 322 and <= 337))
            {
                ShiftBackground();
            }

            if (dot == 257 || (dot is >= 321 and <= 337) || (!visible && dot is >= 1 and <= 256))
            {
                if (!visible && dot is >= 2 and <= 256)
                {
                    ShiftBackground();
                }

                BackgroundFetch(dot);
            }
            else if (dot is 338 or 340)
            {
                ReadVram((ushort)(0x2000 | (_v & 0x0FFF))); // unused nametable fetches
            }
        }

        if (!rendering)
        {
            return;
        }

        if (dot == 256)
        {
            IncrementY();
        }
        else if (dot == 257)
        {
            // Copy horizontal scroll bits from t to v.
            _v = (ushort)((_v & 0xFBE0) | (_t & 0x041F));
        }
        else if (!visible && dot is >= 280 and <= 304)
        {
            // Copy vertical scroll bits from t to v.
            _v = (ushort)((_v & 0x841F) | (_t & 0x7BE0));
        }

        // Sprites
        if (visible)
        {
            if (dot is >= 1 and <= 64)
            {
                if ((dot & 1) == 0)
                {
                    _secondaryOam[(dot >> 1) - 1] = 0xFF;
                }
            }
            else if (dot is >= 65 and <= 256)
            {
                EvaluateSprites(scanline, dot);
            }
        }

        if (dot is >= 257 and <= 320)
        {
            _oamAddr = 0;
            if (dot == 257)
            {
                LoadSpritesForNextLine(scanline, visible);
            }
        }
    }

    /// <summary>Background tile fetches, every 8 dots on dots 1–256 and 321–336 (NESdev Wiki: "PPU rendering").</summary>
    private void BackgroundFetch(int dot)
    {
        switch ((dot - 1) & 7)
        {
            case 0:
                if (dot >= 9)
                {
                    ReloadBackgroundShifters();
                }

                _ntLatch = ReadVram((ushort)(0x2000 | (_v & 0x0FFF)));
                break;
            case 2:
            {
                var at = ReadVram((ushort)(0x23C0 | (_v & 0x0C00) | ((_v >> 4) & 0x38) | ((_v >> 2) & 0x07)));
                var shift = ((_v >> 4) & 0x04) | (_v & 0x02);
                _atLatch = (byte)((at >> shift) & 0x03);
                break;
            }
            case 4:
                _patternLoLatch = ReadVram(BackgroundPatternAddress());
                break;
            case 6:
                _patternHiLatch = ReadVram((ushort)(BackgroundPatternAddress() + 8));
                break;
            case 7:
                if (dot != 257)
                {
                    IncrementCoarseX();
                }

                break;
        }
    }

    private ushort BackgroundPatternAddress() =>
        (ushort)(((_ctrl & 0x10) << 8) | (_ntLatch << 4) | ((_v >> 12) & 0x07));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ShiftBackground()
    {
        _bgShiftLo <<= 1;
        _bgShiftHi <<= 1;
        _atShiftLo <<= 1;
        _atShiftHi <<= 1;
    }

    private void ReloadBackgroundShifters()
    {
        _bgShiftLo = (ushort)((_bgShiftLo & 0xFF00) | _patternLoLatch);
        _bgShiftHi = (ushort)((_bgShiftHi & 0xFF00) | _patternHiLatch);
        _atShiftLo = (ushort)((_atShiftLo & 0xFF00) | ((_atLatch & 1) != 0 ? 0xFF : 0x00));
        _atShiftHi = (ushort)((_atShiftHi & 0xFF00) | ((_atLatch & 2) != 0 ? 0xFF : 0x00));
    }

    private void IncrementCoarseX()
    {
        if ((_v & 0x001F) == 31)
        {
            _v = (ushort)((_v & ~0x001F) ^ 0x0400); // wrap and switch horizontal nametable
        }
        else
        {
            _v++;
        }
    }

    private void IncrementY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000; // fine Y
            return;
        }

        _v &= 0x0FFF;
        var coarseY = (_v & 0x03E0) >> 5;
        if (coarseY == 29)
        {
            coarseY = 0;
            _v ^= 0x0800; // switch vertical nametable
        }
        else if (coarseY == 31)
        {
            coarseY = 0; // out-of-range Y wraps without switching
        }
        else
        {
            coarseY++;
        }

        _v = (ushort)((_v & ~0x03E0) | (coarseY << 5));
    }

    private void DrawPixel(int scanline, int x)
    {
        int color;
        if (!RenderingEnabled)
        {
            // With rendering off, the backdrop shows, unless v points into palette RAM.
            color = (_v & 0x3F00) == 0x3F00 ? _palette[PaletteIndex(_v)] : _palette[0];
        }
        else
        {
            var bgPixel = 0;
            var bgPalette = 0;
            if ((_mask & 0x08) != 0 && (x >= 8 || (_mask & 0x02) != 0))
            {
                var bit = 15 - _fineX;
                bgPixel = (((_bgShiftHi >> bit) & 1) << 1) | ((_bgShiftLo >> bit) & 1);
                bgPalette = (((_atShiftHi >> bit) & 1) << 1) | ((_atShiftLo >> bit) & 1);
            }

            var spritePixel = 0;
            var spritePalette = 0;
            var spriteBehind = false;
            var isSprite0 = false;
            if ((_mask & 0x10) != 0 && (x >= 8 || (_mask & 0x04) != 0))
            {
                for (var i = 0; i < _spriteCount; i++)
                {
                    var offset = x - _spriteX[i];
                    if ((uint)offset > 7)
                    {
                        continue;
                    }

                    var shift = 7 - offset;
                    var pixel = (((_spritePatternHi[i] >> shift) & 1) << 1) | ((_spritePatternLo[i] >> shift) & 1);
                    if (pixel == 0)
                    {
                        continue;
                    }

                    spritePixel = pixel;
                    spritePalette = (_spriteAttr[i] & 0x03) + 4;
                    spriteBehind = (_spriteAttr[i] & 0x20) != 0;
                    isSprite0 = i == 0 && _sprite0OnLine;
                    break;
                }
            }

            if (bgPixel == 0)
            {
                color = spritePixel == 0 ? _palette[0] : _palette[(spritePalette << 2) | spritePixel];
            }
            else if (spritePixel == 0)
            {
                color = _palette[(bgPalette << 2) | bgPixel];
            }
            else
            {
                if (isSprite0 && x != 255)
                {
                    _sprite0Hit = true;
                }

                color = spriteBehind ? _palette[(bgPalette << 2) | bgPixel] : _palette[(spritePalette << 2) | spritePixel];
            }
        }

        if ((_mask & 0x01) != 0)
        {
            color &= 0x30; // grayscale
        }

        _frame[(scanline << 8) | x] = (ushort)(color | ((_mask & 0xE0) << 1));
    }

    /// <summary>
    /// Secondary-OAM evaluation on dots 65–256: odd dots read OAM, even dots write secondary OAM. Once 8
    /// sprites are found, the hardware's buggy overflow search also increments m (NESdev Wiki: "PPU sprite evaluation").
    /// </summary>
    private void EvaluateSprites(int scanline, int dot)
    {
        if (dot == 65)
        {
            _evalN = _evalM = _secondaryIndex = _spritesFound = 0;
            _evalDone = false;
            _sprite0Next = false;
        }

        if ((dot & 1) == 1)
        {
            _oamCopy = _oam[((_evalN << 2) | _evalM) & 0xFF];
            return;
        }

        if (_evalDone)
        {
            return;
        }

        var height = (_ctrl & 0x20) != 0 ? 16 : 8;
        if (_spritesFound < 8)
        {
            _secondaryOam[_secondaryIndex] = _oamCopy;
            if (_evalM == 0)
            {
                var row = scanline - _oamCopy;
                if (row >= 0 && row < height)
                {
                    if (_evalN == 0)
                    {
                        _sprite0Next = true;
                    }

                    _evalM = 1;
                    _secondaryIndex++;
                }
                else if (++_evalN == 64)
                {
                    _evalDone = true;
                }
            }
            else
            {
                _secondaryIndex++;
                if (++_evalM == 4)
                {
                    _evalM = 0;
                    _spritesFound++;
                    if (++_evalN == 64)
                    {
                        _evalDone = true;
                    }
                }
            }
        }
        else
        {
            var row = scanline - _oamCopy;
            if (row >= 0 && row < height)
            {
                _spriteOverflow = true;
                _evalDone = true;
            }
            else
            {
                // The hardware bug: n and m both increment, so it checks the wrong bytes as Y.
                _evalM = (_evalM + 1) & 3;
                if (++_evalN == 64)
                {
                    _evalDone = true;
                }
            }
        }
    }

    /// <summary>
    /// Dots 257–320: fetch patterns for the sprites found for the next line. Empty slots fetch tile $FF,
    /// which matters for mappers that watch A12.
    /// </summary>
    private void LoadSpritesForNextLine(int scanline, bool visible)
    {
        _spriteCount = 0;
        _sprite0OnLine = false;
        var height = (_ctrl & 0x20) != 0 ? 16 : 8;

        if (!visible)
        {
            // The pre-render line fetches, but nothing is drawn on line 0.
            for (var i = 0; i < 8; i++)
            {
                FetchSpritePattern(0xFF, 0xFF, 0, height, out _, out _);
            }

            return;
        }

        var found = _spritesFound;
        for (var i = 0; i < 8; i++)
        {
            if (i < found)
            {
                var y = _secondaryOam[i * 4];
                var tile = _secondaryOam[i * 4 + 1];
                var attr = _secondaryOam[i * 4 + 2];
                FetchSpritePattern(tile, attr, scanline - y, height, out var lo, out var hi);
                _spritePatternLo[i] = lo;
                _spritePatternHi[i] = hi;
                _spriteAttr[i] = attr;
                _spriteX[i] = _secondaryOam[i * 4 + 3];
            }
            else
            {
                FetchSpritePattern(0xFF, 0xFF, 0, height, out _, out _);
            }
        }

        _spriteCount = found;
        _sprite0OnLine = _sprite0Next;

        if (RemoveSpriteLimit && found == 8)
        {
            AddSpritesBeyondLimit(scanline, height);
        }
    }

    /// <summary>Optional: draw sprites 9+ without touching the PPU bus, so emulated behavior is unchanged.</summary>
    private void AddSpritesBeyondLimit(int scanline, int height)
    {
        var inRange = 0;
        for (var n = 0; n < 64 && _spriteCount < 64; n++)
        {
            var y = _oam[n * 4];
            var row = scanline - y;
            if (row < 0 || row >= height)
            {
                continue;
            }

            if (++inRange <= 8)
            {
                continue; // already loaded by the hardware path
            }

            var tile = _oam[n * 4 + 1];
            var attr = _oam[n * 4 + 2];
            var address = SpritePatternAddress(tile, attr, row, height);
            var i = _spriteCount++;
            _spritePatternLo[i] = Flip(_mapper.PpuRead(address), attr);
            _spritePatternHi[i] = Flip(_mapper.PpuRead((ushort)(address + 8)), attr);
            _spriteAttr[i] = attr;
            _spriteX[i] = _oam[n * 4 + 3];
        }
    }

    private void FetchSpritePattern(byte tile, byte attr, int row, int height, out byte lo, out byte hi)
    {
        var address = SpritePatternAddress(tile, attr, row, height);
        lo = Flip(ReadVram(address), attr);
        hi = Flip(ReadVram((ushort)(address + 8)), attr);
    }

    private ushort SpritePatternAddress(byte tile, byte attr, int row, int height)
    {
        row &= height - 1;
        if ((attr & 0x80) != 0)
        {
            row = height - 1 - row; // vertical flip
        }

        if (height == 16)
        {
            var table = (tile & 1) << 12;
            var index = (tile & 0xFE) + (row >= 8 ? 1 : 0);
            return (ushort)(table | (index << 4) | (row & 7));
        }

        return (ushort)(((_ctrl & 0x08) << 9) | (tile << 4) | row);
    }

    private static byte Flip(byte pattern, byte attr)
    {
        if ((attr & 0x40) == 0)
        {
            return pattern;
        }

        // Horizontal flip: reverse the bit order.
        pattern = (byte)(((pattern & 0xF0) >> 4) | ((pattern & 0x0F) << 4));
        pattern = (byte)(((pattern & 0xCC) >> 2) | ((pattern & 0x33) << 2));
        return (byte)(((pattern & 0xAA) >> 1) | ((pattern & 0x55) << 1));
    }

    // ------------------------------------------------------------------ state

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteInt32(Scanline);
        writer.WriteInt32(Dot);
        writer.WriteInt64(FrameCount);
        writer.WriteBytes(_ciram);
        writer.WriteBytes(_palette);
        writer.WriteBytes(_oam);
        writer.WriteBytes(_secondaryOam);
        writer.WriteByte(_ctrl);
        writer.WriteByte(_mask);
        writer.WriteBool(_vblank);
        writer.WriteBool(_sprite0Hit);
        writer.WriteBool(_spriteOverflow);
        writer.WriteByte(_oamAddr);
        writer.WriteUInt16(_v);
        writer.WriteUInt16(_t);
        writer.WriteByte(_fineX);
        writer.WriteBool(_w);
        writer.WriteByte(_readBuffer);
        writer.WriteBool(_suppressVblank);
        writer.WriteByte(_ioLatch);
        foreach (var frame in _ioLatchRefreshed)
        {
            writer.WriteInt64(frame);
        }

        writer.WriteByte(_ntLatch);
        writer.WriteByte(_atLatch);
        writer.WriteByte(_patternLoLatch);
        writer.WriteByte(_patternHiLatch);
        writer.WriteUInt16(_bgShiftLo);
        writer.WriteUInt16(_bgShiftHi);
        writer.WriteUInt16(_atShiftLo);
        writer.WriteUInt16(_atShiftHi);
        writer.WriteByte(_oamCopy);
        writer.WriteInt32(_evalN);
        writer.WriteInt32(_evalM);
        writer.WriteInt32(_secondaryIndex);
        writer.WriteInt32(_spritesFound);
        writer.WriteBool(_evalDone);
        writer.WriteBool(_sprite0Next);
        writer.WriteInt32(_spriteCount);
        writer.WriteBool(_sprite0OnLine);
        writer.WriteBytes(_spritePatternLo);
        writer.WriteBytes(_spritePatternHi);
        writer.WriteBytes(_spriteAttr);
        writer.WriteBytes(_spriteX);
        writer.WriteBool(_lastA12);
        writer.WriteBool(_skipOddDot);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        Scanline = reader.ReadInt32();
        Dot = reader.ReadInt32();
        FrameCount = reader.ReadInt64();
        reader.ReadBytes(_ciram);
        reader.ReadBytes(_palette);
        reader.ReadBytes(_oam);
        reader.ReadBytes(_secondaryOam);
        _ctrl = reader.ReadByte();
        _mask = reader.ReadByte();
        _vblank = reader.ReadBool();
        _sprite0Hit = reader.ReadBool();
        _spriteOverflow = reader.ReadBool();
        _oamAddr = reader.ReadByte();
        _v = reader.ReadUInt16();
        _t = reader.ReadUInt16();
        _fineX = reader.ReadByte();
        _w = reader.ReadBool();
        _readBuffer = reader.ReadByte();
        _suppressVblank = reader.ReadBool();
        _ioLatch = reader.ReadByte();
        for (var i = 0; i < _ioLatchRefreshed.Length; i++)
        {
            _ioLatchRefreshed[i] = reader.ReadInt64();
        }

        _ntLatch = reader.ReadByte();
        _atLatch = reader.ReadByte();
        _patternLoLatch = reader.ReadByte();
        _patternHiLatch = reader.ReadByte();
        _bgShiftLo = reader.ReadUInt16();
        _bgShiftHi = reader.ReadUInt16();
        _atShiftLo = reader.ReadUInt16();
        _atShiftHi = reader.ReadUInt16();
        _oamCopy = reader.ReadByte();
        _evalN = reader.ReadInt32();
        _evalM = reader.ReadInt32();
        _secondaryIndex = reader.ReadInt32();
        _spritesFound = reader.ReadInt32();
        _evalDone = reader.ReadBool();
        _sprite0Next = reader.ReadBool();
        _spriteCount = reader.ReadInt32();
        _sprite0OnLine = reader.ReadBool();
        reader.ReadBytes(_spritePatternLo);
        reader.ReadBytes(_spritePatternHi);
        reader.ReadBytes(_spriteAttr);
        reader.ReadBytes(_spriteX);
        _lastA12 = reader.ReadBool();
        _skipOddDot = reader.ReadBool();
        reader.EndChunk();
        // The framebuffer is output, not state: it is fully redrawn by the next frame.
    }
}
