namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// The canonical Huffman decoder CHD uses for its compressed hunk map. The tree is sent as a run-length-coded list
/// of code lengths; codes are then assigned from the longest length down, and decoded through a lookup table
/// indexed by the next <c>maxBits</c> bits.
/// </summary>
internal sealed class HuffmanDecoder
{
    private readonly int _numCodes;
    private readonly int _maxBits;
    private readonly byte[] _lengths;
    private readonly uint[] _codes;
    private readonly ushort[] _lookup; // symbol << 5 | length

    public HuffmanDecoder(int numCodes, int maxBits)
    {
        _numCodes = numCodes;
        _maxBits = maxBits;
        _lengths = new byte[numCodes];
        _codes = new uint[numCodes];
        _lookup = new ushort[1 << maxBits];
    }

    /// <summary>
    /// Reads the code lengths: each is a small number (4 bits for up to 8-bit codes), where 1 escapes: "1 1" is a
    /// literal 1, and "1 n count" repeats length n count + 3 times.
    /// </summary>
    public void ImportTreeRle(ref BitReader bits)
    {
        var fieldBits = _maxBits >= 16 ? 5 : _maxBits >= 8 ? 4 : 3;
        var node = 0;
        while (node < _numCodes)
        {
            var length = (int)bits.Read(fieldBits);
            if (length != 1)
            {
                _lengths[node++] = (byte)length;
                continue;
            }

            length = (int)bits.Read(fieldBits);
            if (length == 1)
            {
                _lengths[node++] = 1;
                continue;
            }

            var repeat = (int)bits.Read(fieldBits) + 3;
            while (repeat-- > 0)
            {
                if (node >= _numCodes)
                {
                    throw new InvalidDataException("Corrupt Huffman tree in a CHD map.");
                }

                _lengths[node++] = (byte)length;
            }
        }

        AssignCodes();
        BuildLookup();
    }

    private void AssignCodes()
    {
        Span<uint> histogram = stackalloc uint[33];
        foreach (var length in _lengths)
        {
            if (length > _maxBits)
            {
                throw new InvalidDataException("Corrupt Huffman tree in a CHD map.");
            }

            histogram[length]++;
        }

        // Starting codes for each length, from the longest down.
        uint start = 0;
        for (var length = 32; length > 0; length--)
        {
            var next = (start + histogram[length]) >> 1;
            if (length != 1 && next * 2 != start + histogram[length])
            {
                throw new InvalidDataException("Corrupt Huffman tree in a CHD map.");
            }

            histogram[length] = start;
            start = next;
        }

        for (var symbol = 0; symbol < _numCodes; symbol++)
        {
            var length = _lengths[symbol];
            if (length > 0)
            {
                _codes[symbol] = histogram[length]++;
            }
        }
    }

    private void BuildLookup()
    {
        Array.Clear(_lookup);
        for (var symbol = 0; symbol < _numCodes; symbol++)
        {
            var length = _lengths[symbol];
            if (length == 0)
            {
                continue;
            }

            var shift = _maxBits - length;
            var first = (int)(_codes[symbol] << shift);
            var value = (ushort)(symbol << 5 | length);
            for (var i = 0; i < 1 << shift; i++)
            {
                _lookup[first + i] = value;
            }
        }
    }

    public int Decode(ref BitReader bits)
    {
        var entry = _lookup[bits.Peek(_maxBits)];
        var length = entry & 0x1F;
        if (length == 0)
        {
            throw new InvalidDataException("Corrupt Huffman code in a CHD map.");
        }

        bits.Skip(length);
        return entry >> 5;
    }
}
