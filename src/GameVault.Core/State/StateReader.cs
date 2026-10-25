using System.Buffers.Binary;

namespace GameVault.Core.State;

/// <summary>Reads data produced by <see cref="StateWriter"/>. See that type for the chunk layout.</summary>
public sealed class StateReader
{
    private const int MaxChunkDepth = 16;

    private readonly ReadOnlyMemory<byte> _data;
    private int _position;
    private readonly int[] _chunkEnds = new int[MaxChunkDepth];
    private int _chunkDepth;

    public StateReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public bool IsAtEnd => _position == _data.Length;

    /// <summary>Opens the next chunk, checks its tag, and returns the layout version it was written with.</summary>
    public ushort BeginChunk(StateTag expected)
    {
        var tag = new StateTag(ReadUInt32());
        if (tag != expected)
        {
            throw new StateFormatException($"Expected state chunk '{expected}' but found '{tag}'.");
        }

        var version = ReadUInt16();
        var length = ReadUInt32();
        var end = (long)_position + length;
        if (end > _data.Length || (_chunkDepth > 0 && end > _chunkEnds[_chunkDepth - 1]))
        {
            throw new StateFormatException($"State chunk '{tag}' extends past the end of its container.");
        }

        if (_chunkDepth == MaxChunkDepth)
        {
            throw new StateFormatException("State chunks are nested too deeply.");
        }

        _chunkEnds[_chunkDepth++] = (int)end;
        return version;
    }

    /// <summary>Closes the current chunk. Every byte of it must have been read.</summary>
    public void EndChunk()
    {
        if (_chunkDepth == 0)
        {
            throw new StateFormatException("EndChunk called without a matching BeginChunk.");
        }

        var end = _chunkEnds[--_chunkDepth];
        if (_position != end)
        {
            throw new StateFormatException(
                $"State chunk ended at offset {_position} but its declared end is {end}.");
        }
    }

    public byte ReadByte() => Take(1)[0];

    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    /// <summary>Fills <paramref name="destination"/> completely.</summary>
    public void ReadBytes(Span<byte> destination) => Take(destination.Length).CopyTo(destination);

    private ReadOnlySpan<byte> Take(int count)
    {
        var limit = _chunkDepth > 0 ? _chunkEnds[_chunkDepth - 1] : _data.Length;
        if (_position + count > limit)
        {
            throw new StateFormatException("Unexpected end of save-state data.");
        }

        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }
}
