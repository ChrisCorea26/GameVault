using System.Buffers.Binary;

namespace GameVault.Core.State;

/// <summary>
/// Writes binary save-state data into a reusable in-memory buffer. All values are little-endian.
/// </summary>
/// <remarks>
/// Components group their fields into chunks (<see cref="BeginChunk"/> / <see cref="EndChunk"/>).
/// A chunk is laid out as <c>tag:u32, version:u16, length:u32, payload</c>. The version lets a
/// component read older layouts after its fields change.
/// Reuse one writer (call <see cref="Reset"/>) on hot paths such as rewind and run-ahead to avoid
/// allocations.
/// </remarks>
public sealed class StateWriter
{
    private const int MaxChunkDepth = 16;

    private byte[] _buffer;
    private int _length;
    private readonly int[] _chunkStarts = new int[MaxChunkDepth];
    private int _chunkDepth;

    public StateWriter(int initialCapacity = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _buffer = new byte[initialCapacity];
    }

    public int Length => _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public void Reset()
    {
        _length = 0;
        _chunkDepth = 0;
    }

    public byte[] ToArray()
    {
        if (_chunkDepth != 0)
        {
            throw new InvalidOperationException($"{_chunkDepth} state chunk(s) were not ended.");
        }

        return WrittenSpan.ToArray();
    }

    public void BeginChunk(StateTag tag, ushort version)
    {
        if (_chunkDepth == MaxChunkDepth)
        {
            throw new InvalidOperationException("State chunks are nested too deeply.");
        }

        WriteUInt32(tag.Value);
        WriteUInt16(version);
        _chunkStarts[_chunkDepth++] = _length;
        WriteUInt32(0); // Payload length, patched in EndChunk.
    }

    public void EndChunk()
    {
        if (_chunkDepth == 0)
        {
            throw new InvalidOperationException("EndChunk called without a matching BeginChunk.");
        }

        var lengthOffset = _chunkStarts[--_chunkDepth];
        var payloadLength = _length - lengthOffset - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(lengthOffset), (uint)payloadLength);
    }

    public void WriteByte(byte value) => Reserve(1)[0] = value;

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);

    /// <summary>Writes raw bytes with no length prefix. The reader must know the size (for example RAM).</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    private Span<byte> Reserve(int count)
    {
        var required = _length + count;
        if (required > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }

        var span = _buffer.AsSpan(_length, count);
        _length = required;
        return span;
    }
}
