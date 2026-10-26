using System.Buffers.Binary;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// The Reality Display Processor (SPEC §7.11, Phase 16): the N64's rasterizer, in software. Commands arrive through
/// the DPC registers from RDRAM (or from the RSP's DMEM over the XBUS) and run as soon as DPC_END is written, so the
/// result is deterministic. Triangles, rectangles, the texture unit with TMEM, the colour combiner, the blender and the
/// Z buffer follow the hardware's fixed-point rules where they are documented.
/// </summary>
/// <remarks>
/// References: n64brew "Reality Display Processor" (Commands, Pipeline, TMEM), the N64 programming manual's RDP
/// chapters, and PeterLemon's RDP test programs for the coordinate conventions. Anti-aliasing coverage is
/// approximated: a pixel touched by any coverage sample is drawn as fully covered.
/// </remarks>
public sealed partial class N64Rdp : IStateful, IN64DpRegisters
{
    public const uint RegisterBase = 0x04100000;
    public const int TmemSize = 4096;

    // DPC_STATUS bits (reads).
    public const uint StatusXbus = 1 << 0;
    public const uint StatusFreeze = 1 << 1;
    public const uint StatusFlush = 1 << 2;
    public const uint StatusCbufReady = 1 << 7;
    public const uint StatusStartValid = 1 << 10;

    private static readonly StateTag Tag = StateTag.From("RDP ");

    /// <summary>Command lengths in 64-bit words, by command id (n64brew: "Reality Display Processor/Commands").</summary>
    private static readonly byte[] CommandLength = BuildCommandLengths();

    private readonly IN64RcpHost _host;
    private readonly byte[] _tmem = new byte[TmemSize];

    // The words of the command being gathered; the longest (a shaded, textured, Z-buffered triangle) is 22.
    private readonly ulong[] _command = new ulong[22];
    private int _commandWords;

    private uint _start;
    private uint _end;
    private uint _current;
    private bool _startValid;
    private bool _xbus;
    private bool _freeze;
    private bool _flush;
    private uint _clock;

    public N64Rdp(IN64RcpHost host)
    {
        _host = host;
        _tiles = new Tile[8];
        ResetModes();
    }

    /// <summary>The 4 KB texture memory, big-endian.</summary>
    public ReadOnlySpan<byte> Tmem => _tmem;

    /// <summary>Commands executed since power-on (for tests and diagnostics).</summary>
    public long CommandsExecuted { get; private set; }

    public uint ReadRegister(uint address)
    {
        switch ((address >> 2) & 7)
        {
            case 0:
                return _start;
            case 1:
                return _end;
            case 2:
                return _current;
            case 3:
                return (_xbus ? StatusXbus : 0) | (_freeze ? StatusFreeze : 0) | (_flush ? StatusFlush : 0) |
                       StatusCbufReady | (_startValid ? StatusStartValid : 0);
            case 4:
                return _clock & 0xFFFFFF;
            default:
                // BUFBUSY, PIPEBUSY and TMEM: the RDP finishes every command at once, so nothing is ever busy.
                return 0;
        }
    }

    public void WriteRegister(uint address, uint value)
    {
        switch ((address >> 2) & 7)
        {
            case 0:
                // A new start waits until END is written; while one is pending, further writes are ignored.
                if (!_startValid)
                {
                    _start = value & 0xFFFFF8;
                    _startValid = true;
                }

                break;
            case 1:
                _end = value & 0xFFFFF8;
                if (_startValid)
                {
                    _current = _start;
                    _startValid = false;
                    _commandWords = 0;
                }

                Run();
                break;
            case 3:
                WriteStatus(value);
                break;
        }
    }

    private void WriteStatus(uint value)
    {
        if ((value & 1) != 0)
        {
            _xbus = false;
        }

        if ((value & 2) != 0)
        {
            _xbus = true;
        }

        if ((value & 4) != 0)
        {
            _freeze = false;
        }

        if ((value & 8) != 0)
        {
            _freeze = true;
        }

        if ((value & 0x10) != 0)
        {
            _flush = false;
        }

        if ((value & 0x20) != 0)
        {
            _flush = true;
        }

        if ((value & 0x200) != 0)
        {
            _clock = 0;
        }

        if (!_freeze)
        {
            Run();
        }
    }

    /// <summary>Runs the commands between CURRENT and END, keeping a command that isn't complete yet.</summary>
    private void Run()
    {
        if (_freeze)
        {
            return;
        }

        while (_current < _end)
        {
            _command[_commandWords++] = ReadCommandWord(_current);
            _current += 8;
            var id = (int)(_command[0] >> 56) & 0x3F;
            if (_commandWords >= CommandLength[id])
            {
                Execute(id);
                _commandWords = 0;
            }
        }
    }

    private ulong ReadCommandWord(uint address)
    {
        if (_xbus)
        {
            return BinaryPrimitives.ReadUInt64BigEndian(_host.Dmem[(int)(address & 0xFF8)..]);
        }

        return ReadRdram64(address);
    }

    private void Execute(int id)
    {
        CommandsExecuted++;
        var w = _command[0];
        switch (id)
        {
            case >= 0x08 and <= 0x0F:
                DrawTriangle(id);
                break;
            case 0x24:
            case 0x25:
                TextureRectangle(w, _command[1], id == 0x25);
                break;
            case 0x26: // Sync Load
            case 0x27: // Sync Pipe
            case 0x28: // Sync Tile
                break;
            case 0x29: // Sync Full
                _host.RaiseInterrupt(N64Interrupt.Dp);
                break;
            case 0x2A:
                SetKeyGb(w);
                break;
            case 0x2B:
                SetKeyR(w);
                break;
            case 0x2C:
                SetConvert(w);
                break;
            case 0x2D:
                SetScissor(w);
                break;
            case 0x2E:
                _primZ = (int)(w >> 16) & 0x7FFF;
                _primDeltaZ = (int)w & 0xFFFF;
                break;
            case 0x2F:
                SetOtherModes(w);
                break;
            case 0x30:
                LoadTlut(w);
                break;
            case 0x32:
                SetTileSize(w);
                break;
            case 0x33:
                LoadBlock(w);
                break;
            case 0x34:
                LoadTile(w);
                break;
            case 0x35:
                SetTile(w);
                break;
            case 0x36:
                FillRectangle(w);
                break;
            case 0x37:
                _fillColor = (uint)w;
                break;
            case 0x38:
                _fogColor = (uint)w;
                UpdateConstantInputs();
                break;
            case 0x39:
                _blendColor = (uint)w;
                UpdateConstantInputs();
                break;
            case 0x3A:
                _primColor = (uint)w;
                _primLodFraction = (int)(w >> 32) & 0xFF;
                _primMinLevel = (int)(w >> 40) & 0x1F;
                UpdateConstantInputs();
                break;
            case 0x3B:
                _envColor = (uint)w;
                UpdateConstantInputs();
                break;
            case 0x3C:
                SetCombine(w);
                break;
            case 0x3D:
                _textureImage = DecodeImage(w);
                break;
            case 0x3E:
                _zImageAddress = (uint)w & 0x3FFFFFF;
                break;
            case 0x3F:
                _colorImage = DecodeImage(w);
                break;
        }
    }

    private static byte[] BuildCommandLengths()
    {
        var lengths = new byte[64];
        Array.Fill(lengths, (byte)1);
        for (var id = 0x08; id <= 0x0F; id++)
        {
            // Edges (4), then shade (8), texture (8) and Z (2) coefficients, as the command's bits ask.
            lengths[id] = (byte)(4 + ((id & 4) != 0 ? 8 : 0) + ((id & 2) != 0 ? 8 : 0) + ((id & 1) != 0 ? 2 : 0));
        }

        lengths[0x24] = 2;
        lengths[0x25] = 2;
        return lengths;
    }

    // ---- RDRAM ------------------------------------------------------------------------------------------------

    private ulong ReadRdram64(uint address)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFF8);
        return a + 8 <= rdram.Length ? BinaryPrimitives.ReadUInt64BigEndian(rdram[a..]) : 0;
    }

    private byte ReadRdram8(uint address)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFF);
        return a < rdram.Length ? rdram[a] : (byte)0;
    }

    private ushort ReadRdram16(uint address)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFE);
        return a + 2 <= rdram.Length ? BinaryPrimitives.ReadUInt16BigEndian(rdram[a..]) : (ushort)0;
    }

    private uint ReadRdram32(uint address)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFC);
        return a + 4 <= rdram.Length ? BinaryPrimitives.ReadUInt32BigEndian(rdram[a..]) : 0;
    }

    private void WriteRdram8(uint address, byte value)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFF);
        if (a < rdram.Length)
        {
            rdram[a] = value;
        }
    }

    private void WriteRdram16(uint address, ushort value)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFE);
        if (a + 2 <= rdram.Length)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rdram[a..], value);
        }
    }

    private void WriteRdram32(uint address, uint value)
    {
        var rdram = _host.Rdram;
        var a = (int)(address & 0xFFFFFC);
        if (a + 4 <= rdram.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rdram[a..], value);
        }
    }

    private static byte Read8(Span<byte> rdram, uint address)
    {
        var a = (int)(address & 0xFFFFFF);
        return a < rdram.Length ? rdram[a] : (byte)0;
    }

    private static ushort Read16(Span<byte> rdram, uint address)
    {
        var a = (int)(address & 0xFFFFFE);
        return a + 2 <= rdram.Length ? BinaryPrimitives.ReadUInt16BigEndian(rdram[a..]) : (ushort)0;
    }

    private static uint Read32(Span<byte> rdram, uint address)
    {
        var a = (int)(address & 0xFFFFFC);
        return a + 4 <= rdram.Length ? BinaryPrimitives.ReadUInt32BigEndian(rdram[a..]) : 0;
    }

    private static void Write8(Span<byte> rdram, uint address, byte value)
    {
        var a = (int)(address & 0xFFFFFF);
        if (a < rdram.Length)
        {
            rdram[a] = value;
        }
    }

    private static void Write16(Span<byte> rdram, uint address, ushort value)
    {
        var a = (int)(address & 0xFFFFFE);
        if (a + 2 <= rdram.Length)
        {
            BinaryPrimitives.WriteUInt16BigEndian(rdram[a..], value);
        }
    }

    private static void Write32(Span<byte> rdram, uint address, uint value)
    {
        var a = (int)(address & 0xFFFFFC);
        if (a + 4 <= rdram.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rdram[a..], value);
        }
    }

    // ---- Save states ------------------------------------------------------------------------------------------

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt32(_start);
        writer.WriteUInt32(_end);
        writer.WriteUInt32(_current);
        writer.WriteBool(_startValid);
        writer.WriteBool(_xbus);
        writer.WriteBool(_freeze);
        writer.WriteBool(_flush);
        writer.WriteUInt32(_clock);
        writer.WriteInt32(_commandWords);
        for (var i = 0; i < _commandWords; i++)
        {
            writer.WriteUInt64(_command[i]);
        }

        writer.WriteBytes(_tmem);
        writer.WriteUInt64(_otherModes);
        writer.WriteUInt64(_combineMode);
        writer.WriteUInt32(_fillColor);
        writer.WriteUInt32(_fogColor);
        writer.WriteUInt32(_blendColor);
        writer.WriteUInt32(_primColor);
        writer.WriteUInt32(_envColor);
        writer.WriteInt32(_primLodFraction);
        writer.WriteInt32(_primMinLevel);
        writer.WriteInt32(_primZ);
        writer.WriteInt32(_primDeltaZ);
        writer.WriteUInt64(_keyGb);
        writer.WriteUInt64(_keyR);
        writer.WriteUInt64(_convert);
        writer.WriteUInt64(_scissor);
        WriteImage(writer, _textureImage);
        WriteImage(writer, _colorImage);
        writer.WriteUInt32(_zImageAddress);
        for (var i = 0; i < 8; i++)
        {
            ref var tile = ref _tiles[i];
            writer.WriteUInt64(tile.SetTileWord);
            writer.WriteInt32(tile.Sl);
            writer.WriteInt32(tile.Tl);
            writer.WriteInt32(tile.Sh);
            writer.WriteInt32(tile.Th);
        }

        writer.WriteUInt32(_noise);
        writer.WriteInt64(CommandsExecuted);

        // The combiner's inputs: one-cycle mode's COMBINED input carries the previous pixel's result.
        for (var i = 0; i < _ext.Length; i++)
        {
            writer.WriteInt32(_ext[i]);
            writer.WriteInt32(_mul[i]);
        }
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _start = reader.ReadUInt32();
        _end = reader.ReadUInt32();
        _current = reader.ReadUInt32();
        _startValid = reader.ReadBool();
        _xbus = reader.ReadBool();
        _freeze = reader.ReadBool();
        _flush = reader.ReadBool();
        _clock = reader.ReadUInt32();
        _commandWords = reader.ReadInt32();
        if ((uint)_commandWords > (uint)_command.Length)
        {
            throw new StateFormatException("The RDP's command buffer is out of range.");
        }

        for (var i = 0; i < _commandWords; i++)
        {
            _command[i] = reader.ReadUInt64();
        }

        reader.ReadBytes(_tmem);
        SetOtherModes(reader.ReadUInt64());
        SetCombine(reader.ReadUInt64());
        _fillColor = reader.ReadUInt32();
        _fogColor = reader.ReadUInt32();
        _blendColor = reader.ReadUInt32();
        _primColor = reader.ReadUInt32();
        _envColor = reader.ReadUInt32();
        _primLodFraction = reader.ReadInt32();
        _primMinLevel = reader.ReadInt32();
        _primZ = reader.ReadInt32();
        _primDeltaZ = reader.ReadInt32();
        SetKeyGb(reader.ReadUInt64());
        SetKeyR(reader.ReadUInt64());
        SetConvert(reader.ReadUInt64());
        SetScissor(reader.ReadUInt64());
        _textureImage = ReadImage(reader);
        _colorImage = ReadImage(reader);
        _zImageAddress = reader.ReadUInt32();
        for (var i = 0; i < 8; i++)
        {
            SetTile(reader.ReadUInt64() | ((ulong)i << 24));
            ref var tile = ref _tiles[i];
            tile.Sl = reader.ReadInt32();
            tile.Tl = reader.ReadInt32();
            tile.Sh = reader.ReadInt32();
            tile.Th = reader.ReadInt32();
            tile.UpdateDerived();
        }

        _noise = reader.ReadUInt32();
        CommandsExecuted = reader.ReadInt64();
        for (var i = 0; i < _ext.Length; i++)
        {
            _ext[i] = reader.ReadInt32();
            _mul[i] = reader.ReadInt32();
        }
        UpdateConstantInputs();
        reader.EndChunk();
    }

    private static void WriteImage(StateWriter writer, Image image)
    {
        writer.WriteInt32(image.Format);
        writer.WriteInt32(image.Size);
        writer.WriteInt32(image.Width);
        writer.WriteUInt32(image.Address);
    }

    private static Image ReadImage(StateReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32());
}
