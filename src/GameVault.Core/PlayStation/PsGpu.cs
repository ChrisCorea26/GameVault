using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// The PlayStation GPU (SPEC §7.11, Phase 15): 1 MB of VRAM as 1024×512 16-bit pixels, the GP0 command stream (drawing
/// and VRAM transfers) and GP1 display control, with GPUSTAT and GPUREAD.
/// </summary>
/// <remarks>
/// Reference: psx-spx "GPU". GP0 commands are collected word by word until their packet is complete, using the
/// packet lengths psx-spx lists, so the stream stays in step even for commands that aren't drawn yet.
/// </remarks>
public sealed partial class PsGpu : IStateful
{
    public const int VramWidth = 1024;
    public const int VramHeight = 512;

    private static readonly StateTag Tag = StateTag.From("PGPU");

    private readonly ushort[] _vram = new ushort[VramWidth * VramHeight];
    private readonly uint[] _command = new uint[16];
    private int _commandLength;
    private int _commandWords;

    // A VRAM transfer in progress (CPU→VRAM through GP0, or VRAM→CPU through GPUREAD).
    private TransferMode _transfer;
    private int _transferX;
    private int _transferY;
    private int _transferWidth;
    private int _transferHeight;
    private int _transferIndex;

    // The polyline being collected: its vertices and colours arrive until the terminator.
    private bool _polyline;
    private bool _polyExpectColor;
    private int _polyVertices;
    private uint _polyColor;
    private uint _polyLastVertex;
    private uint _polyLastColor;

    private uint _gpuRead;

    // Draw mode and environment (GP0 E1–E6).
    private uint _drawMode;
    private uint _textureWindow;
    private int _drawLeft;
    private int _drawTop;
    private int _drawRight;
    private int _drawBottom;
    private int _offsetX;
    private int _offsetY;
    private bool _setMask;
    private bool _checkMask;

    // Display (GP1).
    private bool _displayDisabled = true;
    private uint _dmaDirection;
    private int _displayX;
    private int _displayY;
    private int _hStart = 0x200;
    private int _hEnd = 0xC00;
    private int _vStart = 0x10;
    private int _vEnd = 0x100;
    private uint _displayMode;
    private bool _irq;

    private enum TransferMode
    {
        None,
        ToVram,
        FromVram,
    }

    public ReadOnlySpan<ushort> Vram => _vram;

    /// <summary>The VRAM pixel at (<paramref name="x"/>, <paramref name="y"/>), in BGR555 with the mask bit.</summary>
    public ushort Pixel(int x, int y) => _vram[(y & (VramHeight - 1)) * VramWidth + (x & (VramWidth - 1))];

    /// <summary>The GPU's interrupt request (GP0 $1F), until GP1 $02 acknowledges it.</summary>
    public bool Irq => _irq;

    /// <summary>Set by GP1 $08: the display is PAL (50 Hz).</summary>
    public bool PalMode => (_displayMode & 0x08) != 0;

    public bool Interlaced => (_displayMode & 0x24) == 0x24;

    /// <summary>The field being shown in interlaced mode (GPUSTAT bit 13), set by the console each frame.</summary>
    internal bool OddField { get; set; }

    /// <summary>Whether the current line is odd (GPUSTAT bit 31): the field in interlaced mode, else each line; 0 in V-blank.</summary>
    internal bool OddLine { get; set; }

    public void Reset()
    {
        _commandWords = _commandLength = 0;
        _transfer = TransferMode.None;
        _polyline = false;
        _drawMode = 0;
        _textureWindow = 0;
        _drawLeft = _drawTop = _drawRight = _drawBottom = 0;
        _offsetX = _offsetY = 0;
        _setMask = _checkMask = false;
        _displayDisabled = true;
        _dmaDirection = 0;
        _displayX = _displayY = 0;
        _hStart = 0x200;
        _hEnd = 0xC00;
        _vStart = 0x10;
        _vEnd = 0x100;
        _displayMode = 0;
        _irq = false;
    }

    public void Power()
    {
        Array.Clear(_vram);
        Reset();
    }

    // ---- GPUSTAT and GPUREAD ----

    /// <summary>GPUSTAT ($1F801814). Commands finish at once, so the GPU always reports ready.</summary>
    public uint Status
    {
        get
        {
            var status = _drawMode & 0x7FF;
            status |= (_drawMode & 0x800) << 4; // texture disable → bit 15
            status |= _setMask ? 1u << 11 : 0;
            status |= _checkMask ? 1u << 12 : 0;
            status |= !Interlaced || OddField ? 1u << 13 : 0; // interlace field (always 1 when not interlaced)
            status |= (_displayMode & 0x40) << 10; // horizontal resolution 2 → bit 16
            status |= (_displayMode & 0x3F) << 17;
            status |= (_displayMode & 0x80) << 7; // "reverse" flag → bit 14
            status |= _displayDisabled ? 1u << 23 : 0;
            status |= _irq ? 1u << 24 : 0;
            status |= 1u << 26; // ready for a command
            status |= 1u << 27; // ready to send VRAM to the CPU
            status |= 1u << 28; // ready for a DMA block
            status |= _dmaDirection << 29;
            status |= OddLine ? 1u << 31 : 0;

            // Bit 25 is the DMA request, which depends on the direction.
            status |= _dmaDirection switch
            {
                1 => 1u << 25,
                2 => 1u << 25,
                3 => (status >> 27 & 1) << 25,
                _ => 0,
            };
            return status;
        }
    }

    /// <summary>GPUREAD ($1F801810): VRAM during a VRAM→CPU transfer, otherwise the last GP1 $10 answer.</summary>
    public uint ReadData()
    {
        if (_transfer != TransferMode.FromVram)
        {
            return _gpuRead;
        }

        var low = ReadTransferPixel();
        var high = ReadTransferPixel();
        _gpuRead = (uint)(low | high << 16);
        return _gpuRead;
    }

    private ushort ReadTransferPixel()
    {
        if (_transfer != TransferMode.FromVram)
        {
            return 0;
        }

        var x = _transferX + _transferIndex % _transferWidth;
        var y = _transferY + _transferIndex / _transferWidth;
        var value = Pixel(x, y);
        if (++_transferIndex >= _transferWidth * _transferHeight)
        {
            _transfer = TransferMode.None;
        }

        return value;
    }

    // ---- GP0 ----

    public void WriteGp0(uint word)
    {
        if (_transfer == TransferMode.ToVram)
        {
            WriteTransferPixel((ushort)word);
            WriteTransferPixel((ushort)(word >> 16));
            return;
        }

        if (_polyline)
        {
            CollectPolyline(word);
            return;
        }

        if (_commandWords == 0)
        {
            if ((word >> 29) == 2 && (word & 0x08000000) != 0)
            {
                _command[0] = word;
                _polyline = true;
                _polyColor = word & 0xFFFFFF;
                _polyVertices = 0;
                _polyExpectColor = false;
                return;
            }

            _commandLength = PacketLength(word);
        }

        _command[_commandWords++] = word;
        if (_commandWords >= _commandLength)
        {
            _commandWords = 0;
            ExecuteGp0();
        }
    }

    /// <summary>The words in a GP0 packet, from its first word (psx-spx: "GPU Command Summary").</summary>
    private static int PacketLength(uint word)
    {
        var op = word >> 24;
        switch (op >> 5)
        {
            case 1: // polygons: 3 or 4 vertices, each with optional colour and texture words
            {
                var vertices = (op & 0x08) != 0 ? 4 : 3;
                var perVertex = 1 + ((op & 0x04) != 0 ? 1 : 0) + ((op & 0x10) != 0 ? 1 : 0);
                return 1 + vertices * perVertex - ((op & 0x10) != 0 ? 1 : 0);
            }

            case 2: // lines (polylines are collected separately)
                return (op & 0x10) != 0 ? 4 : 3;
            case 3: // rectangles
                return 2 + ((op & 0x04) != 0 ? 1 : 0) + ((op & 0x18) == 0 ? 1 : 0);
            case 4: // VRAM→VRAM
                return 4;
            case 5: // CPU→VRAM
            case 6: // VRAM→CPU
                return 3;
            default:
                return op == 0x02 ? 3 : 1;
        }
    }

    /// <summary>
    /// Takes a polyline's next word: a colour (Gouraud lines, before each vertex after the first), a vertex, or the
    /// terminator, a word of the form 5xxx5xxx where a colour or vertex would start, once two vertices have come
    /// (psx-spx: "GPU Render Line Commands"). Each segment is drawn as its second vertex arrives.
    /// </summary>
    private void CollectPolyline(uint word)
    {
        var gouraud = (_command[0] & 0x10000000) != 0;
        if (_polyVertices >= 2 && (word & 0xF000F000) == 0x50005000)
        {
            _polyline = false;
            return;
        }

        if (gouraud && _polyExpectColor)
        {
            _polyColor = word & 0xFFFFFF;
            _polyExpectColor = false;
            return;
        }

        if (_polyVertices > 0)
        {
            DrawLineSegment(_polyLastVertex, _polyLastColor, word, _polyColor);
        }

        _polyLastVertex = word;
        _polyLastColor = _polyColor;
        _polyVertices++;
        _polyExpectColor = gouraud;
    }

    private void ExecuteGp0()
    {
        var word = _command[0];
        var op = word >> 24;
        switch (op >> 5)
        {
            case 1:
                DrawPolygon();
                return;
            case 2:
                DrawLine();
                return;
            case 3:
                DrawRectangle();
                return;
            case 4:
                CopyVramToVram();
                return;
            case 5:
                StartTransfer(TransferMode.ToVram);
                return;
            case 6:
                StartTransfer(TransferMode.FromVram);
                return;
        }

        switch (op)
        {
            case 0x02:
                FillRectangle();
                break;
            case 0x1F:
                _irq = true;
                break;
            case 0xE1:
                _drawMode = word & 0x3FFF;
                break;
            case 0xE2:
                _textureWindow = word & 0xFFFFF;
                break;
            case 0xE3:
                _drawLeft = (int)(word & 0x3FF);
                _drawTop = (int)((word >> 10) & 0x1FF);
                break;
            case 0xE4:
                _drawRight = (int)(word & 0x3FF);
                _drawBottom = (int)((word >> 10) & 0x1FF);
                break;
            case 0xE5:
                // Signed 11-bit offsets.
                _offsetX = (int)(word << 21) >> 21;
                _offsetY = (int)(word << 10) >> 21;
                break;
            case 0xE6:
                _setMask = (word & 1) != 0;
                _checkMask = (word & 2) != 0;
                break;

            // $00 (nop), $01 (clear texture cache) and the unused commands do nothing.
        }
    }

    /// <summary>GP0 $02: fill a rectangle with a colour, ignoring the draw area and mask (x in steps of 16).</summary>
    private void FillRectangle()
    {
        var color = Rgb24To15(_command[0]);
        var x0 = (int)(_command[1] & 0x3F0);
        var y0 = (int)((_command[1] >> 16) & 0x1FF);
        var width = (int)(((_command[2] & 0x3FF) + 0xF) & ~0xFu);
        var height = (int)((_command[2] >> 16) & 0x1FF);
        for (var y = 0; y < height; y++)
        {
            var row = ((y0 + y) & (VramHeight - 1)) * VramWidth;
            for (var x = 0; x < width; x++)
            {
                _vram[row + ((x0 + x) & (VramWidth - 1))] = color;
            }
        }
    }

    private void CopyVramToVram()
    {
        var sx = (int)(_command[1] & 0x3FF);
        var sy = (int)((_command[1] >> 16) & 0x1FF);
        var dx = (int)(_command[2] & 0x3FF);
        var dy = (int)((_command[2] >> 16) & 0x1FF);
        var (width, height) = TransferSize(_command[3]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = Pixel(sx + x, sy + y);
                WriteMasked((dx + x) & (VramWidth - 1), (dy + y) & (VramHeight - 1), value);
            }
        }
    }

    private void StartTransfer(TransferMode mode)
    {
        _transferX = (int)(_command[1] & 0x3FF);
        _transferY = (int)((_command[1] >> 16) & 0x1FF);
        (_transferWidth, _transferHeight) = TransferSize(_command[2]);
        _transferIndex = 0;
        _transfer = mode;
    }

    /// <summary>A transfer's size: 0 means the maximum (1024 or 512), as psx-spx describes.</summary>
    private static (int Width, int Height) TransferSize(uint word)
    {
        var width = (int)(((word & 0xFFFF) - 1) & 0x3FF) + 1;
        var height = (int)((((word >> 16) & 0xFFFF) - 1) & 0x1FF) + 1;
        return (width, height);
    }

    private void WriteTransferPixel(ushort value)
    {
        if (_transfer != TransferMode.ToVram)
        {
            return;
        }

        var x = (_transferX + _transferIndex % _transferWidth) & (VramWidth - 1);
        var y = (_transferY + _transferIndex / _transferWidth) & (VramHeight - 1);
        WriteMasked(x, y, value);
        if (++_transferIndex >= _transferWidth * _transferHeight)
        {
            _transfer = TransferMode.None;
        }
    }

    /// <summary>Writes a pixel with the mask settings: skipped over a masked pixel when checking, bit 15 forced when setting.</summary>
    private void WriteMasked(int x, int y, ushort value)
    {
        var index = y * VramWidth + x;
        if (_checkMask && (_vram[index] & 0x8000) != 0)
        {
            return;
        }

        _vram[index] = (ushort)(value | (_setMask ? 0x8000 : 0));
    }

    private static ushort Rgb24To15(uint color) =>
        (ushort)(((color >> 3) & 0x1F) | ((color >> 11) & 0x1F) << 5 | ((color >> 19) & 0x1F) << 10);

    // ---- GP1 ----

    public void WriteGp1(uint word)
    {
        var value = word & 0xFFFFFF;
        switch ((word >> 24) & 0x3F)
        {
            case 0x00:
                Reset();
                break;
            case 0x01:
                _commandWords = 0;
                _polyline = false;
                _transfer = TransferMode.None;
                break;
            case 0x02:
                _irq = false;
                break;
            case 0x03:
                _displayDisabled = (value & 1) != 0;
                break;
            case 0x04:
                _dmaDirection = value & 3;
                break;
            case 0x05:
                _displayX = (int)(value & 0x3FE);
                _displayY = (int)((value >> 10) & 0x1FF);
                break;
            case 0x06:
                _hStart = (int)(value & 0xFFF);
                _hEnd = (int)((value >> 12) & 0xFFF);
                break;
            case 0x07:
                _vStart = (int)(value & 0x3FF);
                _vEnd = (int)((value >> 10) & 0x3FF);
                break;
            case 0x08:
                _displayMode = value & 0xFF;
                break;
            case 0x10:
            case >= 0x11 and <= 0x1F:
                _gpuRead = GetInfo(value & 0xF);
                break;
        }
    }

    /// <summary>GP1 $10: the draw environment, read back through GPUREAD.</summary>
    private uint GetInfo(uint what) => what switch
    {
        2 => _textureWindow,
        3 => (uint)(_drawLeft | _drawTop << 10),
        4 => (uint)(_drawRight | _drawBottom << 10),
        5 => (uint)((_offsetX & 0x7FF) | (_offsetY & 0x7FF) << 11),
        7 => 2, // GPU version (the 160-pin GPU of most consoles)
        _ => _gpuRead,
    };

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var pixel in _vram)
        {
            writer.WriteUInt16(pixel);
        }

        foreach (var word in _command)
        {
            writer.WriteUInt32(word);
        }

        writer.WriteInt32(_commandLength);
        writer.WriteInt32(_commandWords);
        writer.WriteInt32((int)_transfer);
        writer.WriteInt32(_transferX);
        writer.WriteInt32(_transferY);
        writer.WriteInt32(_transferWidth);
        writer.WriteInt32(_transferHeight);
        writer.WriteInt32(_transferIndex);
        writer.WriteBool(_polyline);
        writer.WriteBool(_polyExpectColor);
        writer.WriteInt32(_polyVertices);
        writer.WriteUInt32(_polyColor);
        writer.WriteUInt32(_polyLastVertex);
        writer.WriteUInt32(_polyLastColor);
        writer.WriteUInt32(_gpuRead);
        writer.WriteUInt32(_drawMode);
        writer.WriteUInt32(_textureWindow);
        writer.WriteInt32(_drawLeft);
        writer.WriteInt32(_drawTop);
        writer.WriteInt32(_drawRight);
        writer.WriteInt32(_drawBottom);
        writer.WriteInt32(_offsetX);
        writer.WriteInt32(_offsetY);
        writer.WriteBool(_setMask);
        writer.WriteBool(_checkMask);
        writer.WriteBool(_displayDisabled);
        writer.WriteUInt32(_dmaDirection);
        writer.WriteInt32(_displayX);
        writer.WriteInt32(_displayY);
        writer.WriteInt32(_hStart);
        writer.WriteInt32(_hEnd);
        writer.WriteInt32(_vStart);
        writer.WriteInt32(_vEnd);
        writer.WriteUInt32(_displayMode);
        writer.WriteBool(_irq);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _vram.Length; i++)
        {
            _vram[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _command.Length; i++)
        {
            _command[i] = reader.ReadUInt32();
        }

        _commandLength = reader.ReadInt32();
        _commandWords = Math.Clamp(reader.ReadInt32(), 0, _command.Length);
        _transfer = (TransferMode)reader.ReadInt32();
        _transferX = reader.ReadInt32();
        _transferY = reader.ReadInt32();
        _transferWidth = reader.ReadInt32();
        _transferHeight = reader.ReadInt32();
        _transferIndex = reader.ReadInt32();
        _polyline = reader.ReadBool();
        _polyExpectColor = reader.ReadBool();
        _polyVertices = reader.ReadInt32();
        _polyColor = reader.ReadUInt32();
        _polyLastVertex = reader.ReadUInt32();
        _polyLastColor = reader.ReadUInt32();
        _gpuRead = reader.ReadUInt32();
        _drawMode = reader.ReadUInt32();
        _textureWindow = reader.ReadUInt32();
        _drawLeft = reader.ReadInt32();
        _drawTop = reader.ReadInt32();
        _drawRight = reader.ReadInt32();
        _drawBottom = reader.ReadInt32();
        _offsetX = reader.ReadInt32();
        _offsetY = reader.ReadInt32();
        _setMask = reader.ReadBool();
        _checkMask = reader.ReadBool();
        _displayDisabled = reader.ReadBool();
        _dmaDirection = reader.ReadUInt32();
        _displayX = reader.ReadInt32();
        _displayY = reader.ReadInt32();
        _hStart = reader.ReadInt32();
        _hEnd = reader.ReadInt32();
        _vStart = reader.ReadInt32();
        _vEnd = reader.ReadInt32();
        _displayMode = reader.ReadUInt32();
        _irq = reader.ReadBool();
        reader.EndChunk();
    }
}
