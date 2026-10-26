namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// Reads bits most-significant first (FLAC frames and CHD's compressed hunk maps). Reads past the end give zeros and
/// set <see cref="Overrun"/>.
/// </summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    public readonly ReadOnlySpan<byte> Source => _data;

    /// <summary>The byte the next bit is in, counting a partly read byte as consumed.</summary>
    public readonly int BytePosition => (_bitPosition + 7) >> 3;

    public readonly bool Overrun => _bitPosition > _data.Length * 8;

    /// <summary>Reads <paramref name="count"/> bits (0–32) as an unsigned number.</summary>
    public uint Read(int count)
    {
        uint value = 0;
        for (var i = 0; i < count; i++)
        {
            var index = _bitPosition >> 3;
            var bit = index < _data.Length ? (_data[index] >> (7 - (_bitPosition & 7))) & 1 : 0;
            value = (value << 1) | (uint)bit;
            _bitPosition++;
        }

        return value;
    }

    /// <summary>Reads <paramref name="count"/> bits as a two's-complement number.</summary>
    public int ReadSigned(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var value = Read(count);
        return count == 32 ? (int)value : (int)(value << (32 - count)) >> (32 - count);
    }

    /// <summary>Counts zero bits up to the next one bit (which is consumed).</summary>
    public uint ReadUnary()
    {
        uint zeros = 0;
        while (Read(1) == 0)
        {
            if (Overrun)
            {
                throw new InvalidDataException("Unexpected end of data in a unary code.");
            }

            zeros++;
        }

        return zeros;
    }

    /// <summary>The next <paramref name="count"/> bits, without consuming them.</summary>
    public readonly uint Peek(int count)
    {
        var copy = this;
        return copy.Read(count);
    }

    public void Skip(int count) => _bitPosition += count;

    public void AlignToByte() => _bitPosition = (_bitPosition + 7) & ~7;
}
