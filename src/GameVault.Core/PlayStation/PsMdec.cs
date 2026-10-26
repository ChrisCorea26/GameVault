using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// The PlayStation's Macroblock Decoder (SPEC §7.11, Phase 15): run-length decoding with the quant tables, the IDCT
/// with the scale table, and YUV→RGB at 4, 8, 15 or 24 bits, fed through DMA0 (MDECin) and read through DMA1
/// (MDECout).
/// </summary>
/// <remarks>
/// <para>Reference: psx-spx "Macroblock Decoder (MDEC)" for the commands, status bits, rl_decode_block, the zigzag
/// order and y_to_mono / yuv_to_rgb.</para>
/// <para>psx-spx notes that its IDCT pseudocode doesn't round exactly like the hardware. Here the dequantized
/// coefficients keep three fraction bits (the "/8" moves into the IDCT), the first pass truncates and the second rounds;
/// that arrangement fits the hardware log of ps1-tests <c>mdec/8bit</c> best: 56 of 64 pixels exact, the rest ±1.
/// The YUV→RGB precision is likewise undocumented (psx-spx) and uses 8 fraction bits.</para>
/// <para>Parameter words are buffered and blocks are decoded as output space frees up, so a whole frame can arrive
/// through DMA at once while the output buffer stays one macroblock deep. Decoding takes no time. Output is always in
/// macroblock order (rows of 16 pixels for colour), as DMA1 delivers it; the 8×8 block order of plain register reads
/// isn't modelled.</para>
/// </remarks>
public sealed class PsMdec : IStateful
{
    private const int InputCapacity = 0x10000;
    private const int OutputCapacity = 2 * MacroblockWords;
    private const int MacroblockWords = 192; // 16×16 pixels at 24 bits
    private const int StartOfBlock = 64;

    private static readonly StateTag Tag = StateTag.From("MDEC");

    /// <summary>zagzig: the raster position of each coefficient in stream order (psx-spx: zagzig[zigzag[i]] = i).</summary>
    private static readonly byte[] ZagZig = BuildZagZig();

    private readonly byte[] _quantY = new byte[64];
    private readonly byte[] _quantUv = new byte[64];
    private readonly short[] _scale = new short[64];

    private readonly uint[] _input = new uint[InputCapacity];
    private int _inputHead;
    private int _inputCount;

    private readonly uint[] _output = new uint[OutputCapacity];
    private int _outputHead;
    private int _outputCount;

    // The block being run-length decoded, and the IDCT's buffers.
    private readonly int[] _block = new int[64];
    private readonly long[] _idctTemp = new long[64];
    private readonly int[] _cr = new int[64];
    private readonly int[] _cb = new int[64];
    private readonly int[] _y = new int[64];
    private int _coefficient = StartOfBlock;
    private int _qScale;

    // A colour macroblock's pixels (signed 8-bit R, G, B packed as 0x00BBGGRR) until Y4 completes it.
    private readonly uint[] _macroblock = new uint[256];

    private uint _commandWord;
    private int _command;
    private int _remaining;
    private int _parameterIndex;
    private int _currentBlock = 4;
    private bool _dmaInEnabled;
    private bool _dmaOutEnabled;

    public PsMdec()
    {
        Reset();
    }

    private int Depth => (int)(_commandWord >> 27) & 3;

    private bool SignedOutput => (_commandWord & (1 << 26)) != 0;

    private bool SetBit15 => (_commandWord & (1 << 25)) != 0;

    private bool Colour => Depth >= 2;

    /// <summary>DMA0 may send: it's enabled and a command is waiting for parameter words.</summary>
    public bool DataInRequest => _dmaInEnabled && _remaining > 0 && _inputCount < InputCapacity;

    /// <summary>DMA1 may read: it's enabled and decoded data is waiting.</summary>
    public bool DataOutRequest
    {
        get
        {
            Pump();
            return _dmaOutEnabled && _outputCount > 0;
        }
    }

    /// <summary>The status register at $1F801824 (psx-spx: "1F801824h - MDEC1 - MDEC Status Register").</summary>
    public uint Status
    {
        get
        {
            Pump();
            var status = 0u;
            if (_outputCount == 0)
            {
                status |= 1u << 31;
            }

            if (_command != 0 && _remaining == 0 && Busy)
            {
                status |= 1u << 30; // the last parameter word has been received
            }

            if (Busy)
            {
                status |= 1u << 29;
            }

            if (DataInRequest)
            {
                status |= 1u << 28;
            }

            if (_dmaOutEnabled && _outputCount > 0)
            {
                status |= 1u << 27;
            }

            status |= ((_commandWord >> 25) & 0xF) << 23;
            status |= (uint)_currentBlock << 16;
            status |= _command == 0 ? _commandWord & 0xFFFF : (uint)(_remaining - 1) & 0xFFFF;
            return status;
        }
    }

    /// <summary>A command is receiving parameters, or a decode still has data in or out.</summary>
    private bool Busy => _remaining > 0 || (_command == 1 && (_inputCount > 0 || _outputCount > 0));

    public void Reset()
    {
        _commandWord = 0;
        _command = 0;
        _remaining = 0;
        _parameterIndex = 0;
        _inputHead = _inputCount = 0;
        _outputHead = _outputCount = 0;
        _coefficient = StartOfBlock;
        _qScale = 0;
        _currentBlock = 4;
    }

    /// <summary>The control register: bit 31 resets, bits 30 and 29 enable the DMA requests.</summary>
    public void WriteControl(uint word)
    {
        if ((word & 0x80000000) != 0)
        {
            Reset();
        }

        _dmaInEnabled = (word & (1 << 30)) != 0;
        _dmaOutEnabled = (word & (1 << 29)) != 0;
    }

    /// <summary>A command word, or a parameter word for the command in progress.</summary>
    public void WriteCommand(uint word)
    {
        if (_remaining == 0)
        {
            StartCommand(word);
            return;
        }

        _remaining--;
        switch (_command)
        {
            case 1:
                _input[(_inputHead + _inputCount) & (InputCapacity - 1)] = word;
                _inputCount++;
                Pump();
                break;
            case 2:
                for (var i = 0; i < 4; i++, _parameterIndex++)
                {
                    var value = (byte)(word >> (i * 8));
                    if (_parameterIndex < 64)
                    {
                        _quantY[_parameterIndex] = value;
                    }
                    else
                    {
                        _quantUv[_parameterIndex - 64] = value;
                    }
                }

                break;
            case 3:
                _scale[_parameterIndex++] = (short)word;
                _scale[_parameterIndex++] = (short)(word >> 16);
                break;
        }
    }

    private void StartCommand(uint word)
    {
        _commandWord = word;
        _command = (int)(word >> 29);
        _parameterIndex = 0;
        switch (_command)
        {
            case 1:
                _remaining = (int)(word & 0xFFFF);
                _inputHead = _inputCount = 0;
                _coefficient = StartOfBlock;
                _currentBlock = 4; // Cr first in colour; the single Y block in monochrome
                break;
            case 2:
                _remaining = (word & 1) != 0 ? 32 : 16;
                break;
            case 3:
                _remaining = 32;
                break;
            default:
                // MDEC(0) and MDEC(4–7) take no parameters; bits 0–15 show in the status as they are.
                _command = 0;
                _remaining = 0;
                break;
        }
    }

    /// <summary>The data register at $1F801820: decoded pixels, in the order DMA1 delivers them.</summary>
    public uint ReadData()
    {
        Pump();
        if (_outputCount == 0)
        {
            return 0xFFFFFFFF;
        }

        var value = _output[_outputHead];
        _outputHead = (_outputHead + 1) % OutputCapacity;
        _outputCount--;
        Pump();
        return value;
    }

    // ---- Decoding ----

    /// <summary>Decodes buffered input while there is room for another macroblock of output.</summary>
    private void Pump()
    {
        while (_command == 1 && _inputCount > 0 && _outputCount <= OutputCapacity - MacroblockWords)
        {
            var word = _input[_inputHead];
            _inputHead = (_inputHead + 1) & (InputCapacity - 1);
            _inputCount--;
            Feed((ushort)word);
            Feed((ushort)(word >> 16));
        }
    }

    /// <summary>One halfword of run-length data (psx-spx: rl_decode_block).</summary>
    private void Feed(ushort n)
    {
        var quant = Colour && _currentBlock >= 4 ? _quantUv : _quantY;
        int index;
        int value;
        if (_coefficient == StartOfBlock)
        {
            if (n == 0xFE00)
            {
                return; // padding between blocks
            }

            Array.Clear(_block);
            _qScale = (n >> 10) & 0x3F;
            _coefficient = 0;
            index = 0;

            // The first value has no q_scale and no /8; with three fraction bits that is ×8.
            value = Signed10(n) * quant[0] * 8;
        }
        else
        {
            _coefficient += ((n >> 10) & 0x3F) + 1;
            index = _coefficient;
            value = index < 64 ? Signed10(n) * quant[index] * _qScale : 0;
        }

        if (index < 64)
        {
            if (_qScale == 0)
            {
                value = Signed10(n) * 2 * 8; // the special mode without the quant table
            }

            // Saturate to the signed 11-bit range (here with three fraction bits).
            value = Math.Clamp(value, -0x400 * 8, 0x3FF * 8);
            _block[_qScale > 0 ? ZagZig[index] : index] = value;
        }

        if (_coefficient >= 63)
        {
            _coefficient = StartOfBlock;
            FinishBlock();
        }
    }

    private static int Signed10(ushort n) => (n & 0x3FF) << 22 >> 22;

    private void FinishBlock()
    {
        if (!Colour)
        {
            Idct(_y);
            OutputMonochrome();
            return;
        }

        switch (_currentBlock)
        {
            case 4:
                Idct(_cr);
                _currentBlock = 5;
                break;
            case 5:
                Idct(_cb);
                _currentBlock = 0;
                break;
            default:
                Idct(_y);
                YuvToRgb((_currentBlock & 1) * 8, (_currentBlock >> 1) * 8);
                if (_currentBlock == 3)
                {
                    OutputColour();
                    _currentBlock = 4;
                }
                else
                {
                    _currentBlock++;
                }

                break;
        }
    }

    /// <summary>
    /// The IDCT: the block times the scale table, column by column, in two passes with the result mirrored each time
    /// (psx-spx: real_idct_core). The output is clipped to signed 9 bits, as the hardware passes it on.
    /// </summary>
    private void Idct(int[] destination)
    {
        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                long sum = 0;
                for (var z = 0; z < 8; z++)
                {
                    sum += (long)_block[y + z * 8] * _scale[x + z * 8];
                }

                _idctTemp[x + y * 8] = sum / (1 << 18);
            }
        }

        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                long sum = 0;
                for (var z = 0; z < 8; z++)
                {
                    sum += _idctTemp[y + z * 8] * _scale[x + z * 8];
                }

                destination[x + y * 8] = (int)((sum + (1 << 16)) >> 17) << 23 >> 23;
            }
        }
    }

    /// <summary>y_to_mono: one 8×8 block at 4 or 8 bits.</summary>
    private void OutputMonochrome()
    {
        var flip = SignedOutput ? 0 : 0x80;
        if (Depth == 1)
        {
            for (var i = 0; i < 64; i += 4)
            {
                var word = 0u;
                for (var j = 0; j < 4; j++)
                {
                    word |= (uint)((Math.Clamp(_y[i + j], -128, 127) ^ flip) & 0xFF) << (j * 8);
                }

                PushOutput(word);
            }

            return;
        }

        // 4 bits: the 8-bit value rounded to its top four bits, first pixel in the low nibble (ps1-tests mdec/4bit).
        var nibbleFlip = SignedOutput ? 0 : 8;
        for (var i = 0; i < 64; i += 8)
        {
            var word = 0u;
            for (var j = 0; j < 8; j++)
            {
                var y = Math.Clamp(_y[i + j], -128, 127);
                var nibble = Math.Clamp((y + 8) >> 4, -8, 7) ^ nibbleFlip;
                word |= (uint)(nibble & 0xF) << (j * 4);
            }

            PushOutput(word);
        }
    }

    /// <summary>yuv_to_rgb for one Y block at (<paramref name="xx"/>, <paramref name="yy"/>) of the macroblock.</summary>
    private void YuvToRgb(int xx, int yy)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var chroma = (x + xx) / 2 + (y + yy) / 2 * 8;
                var cr = _cr[chroma];
                var cb = _cb[chroma];

                // R = 1.402 Cr, G = −0.3437 Cb − 0.7143 Cr, B = 1.772 Cb, in 8 fraction bits.
                var luma = _y[x + y * 8];
                var r = Math.Clamp(luma + ((359 * cr + 128) >> 8), -128, 127);
                var g = Math.Clamp(luma + ((-88 * cb - 183 * cr + 128) >> 8), -128, 127);
                var b = Math.Clamp(luma + ((454 * cb + 128) >> 8), -128, 127);
                _macroblock[x + xx + (y + yy) * 16] = (uint)(r & 0xFF) | (uint)(g & 0xFF) << 8 | (uint)(b & 0xFF) << 16;
            }
        }
    }

    /// <summary>A finished 16×16 macroblock at 15 or 24 bits, in rows.</summary>
    private void OutputColour()
    {
        var flip = SignedOutput ? 0u : 0x808080u;
        if (Depth == 3)
        {
            var bit15 = SetBit15 ? 0x8000u : 0;
            for (var i = 0; i < 256; i += 2)
            {
                PushOutput(To15(_macroblock[i] ^ flip) | bit15 | (To15(_macroblock[i + 1] ^ flip) | bit15) << 16);
            }

            return;
        }

        // 24 bits: R, G, B bytes per pixel, packed into words.
        var accumulator = 0u;
        var bytes = 0;
        for (var i = 0; i < 256; i++)
        {
            var rgb = _macroblock[i] ^ flip;
            for (var c = 0; c < 3; c++)
            {
                accumulator |= ((rgb >> (c * 8)) & 0xFF) << (bytes * 8);
                if (++bytes == 4)
                {
                    PushOutput(accumulator);
                    accumulator = 0;
                    bytes = 0;
                }
            }
        }
    }

    private static uint To15(uint rgb) =>
        ((rgb >> 3) & 0x1F) | ((rgb >> 11) & 0x1F) << 5 | ((rgb >> 19) & 0x1F) << 10;

    private void PushOutput(uint word)
    {
        _output[(_outputHead + _outputCount) % OutputCapacity] = word;
        _outputCount++;
    }

    private static byte[] BuildZagZig()
    {
        // zigzag[raster position] = stream order (psx-spx: "zigzag[0..63]").
        ReadOnlySpan<byte> zigzag =
        [
            0, 1, 5, 6, 14, 15, 27, 28,
            2, 4, 7, 13, 16, 26, 29, 42,
            3, 8, 12, 17, 25, 30, 41, 43,
            9, 11, 18, 24, 31, 40, 44, 53,
            10, 19, 23, 32, 39, 45, 52, 54,
            20, 22, 33, 38, 46, 51, 55, 60,
            21, 34, 37, 47, 50, 56, 59, 61,
            35, 36, 48, 49, 57, 58, 62, 63,
        ];
        var zagzig = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            zagzig[zigzag[i]] = (byte)i;
        }

        return zagzig;
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_quantY);
        writer.WriteBytes(_quantUv);
        foreach (var s in _scale)
        {
            writer.WriteUInt16((ushort)s);
        }

        writer.WriteInt32(_inputCount);
        for (var i = 0; i < _inputCount; i++)
        {
            writer.WriteUInt32(_input[(_inputHead + i) & (InputCapacity - 1)]);
        }

        writer.WriteInt32(_outputCount);
        for (var i = 0; i < _outputCount; i++)
        {
            writer.WriteUInt32(_output[(_outputHead + i) % OutputCapacity]);
        }

        WriteInts(writer, _block);
        WriteInts(writer, _cr);
        WriteInts(writer, _cb);
        foreach (var pixel in _macroblock)
        {
            writer.WriteUInt32(pixel);
        }

        writer.WriteInt32(_coefficient);
        writer.WriteInt32(_qScale);
        writer.WriteUInt32(_commandWord);
        writer.WriteInt32(_command);
        writer.WriteInt32(_remaining);
        writer.WriteInt32(_parameterIndex);
        writer.WriteInt32(_currentBlock);
        writer.WriteBool(_dmaInEnabled);
        writer.WriteBool(_dmaOutEnabled);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_quantY);
        reader.ReadBytes(_quantUv);
        for (var i = 0; i < _scale.Length; i++)
        {
            _scale[i] = (short)reader.ReadUInt16();
        }

        _inputHead = 0;
        _inputCount = Math.Clamp(reader.ReadInt32(), 0, InputCapacity);
        for (var i = 0; i < _inputCount; i++)
        {
            _input[i] = reader.ReadUInt32();
        }

        _outputHead = 0;
        _outputCount = Math.Clamp(reader.ReadInt32(), 0, OutputCapacity);
        for (var i = 0; i < _outputCount; i++)
        {
            _output[i] = reader.ReadUInt32();
        }

        ReadInts(reader, _block);
        ReadInts(reader, _cr);
        ReadInts(reader, _cb);
        for (var i = 0; i < _macroblock.Length; i++)
        {
            _macroblock[i] = reader.ReadUInt32();
        }

        _coefficient = Math.Clamp(reader.ReadInt32(), 0, StartOfBlock);
        _qScale = reader.ReadInt32() & 0x3F;
        _commandWord = reader.ReadUInt32();
        _command = reader.ReadInt32() & 7;
        _remaining = reader.ReadInt32() & 0xFFFF;
        _parameterIndex = Math.Clamp(reader.ReadInt32(), 0, 128);
        _currentBlock = Math.Clamp(reader.ReadInt32(), 0, 5);
        _dmaInEnabled = reader.ReadBool();
        _dmaOutEnabled = reader.ReadBool();
        reader.EndChunk();
    }

    private static void WriteInts(StateWriter writer, int[] values)
    {
        foreach (var value in values)
        {
            writer.WriteInt32(value);
        }
    }

    private static void ReadInts(StateReader reader, int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadInt32();
        }
    }
}
