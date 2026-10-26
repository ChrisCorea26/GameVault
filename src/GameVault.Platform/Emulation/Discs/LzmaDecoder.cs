namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// A raw LZMA (LZMA1) decoder for a stream whose decompressed size is known, as CHD's <c>lzma</c> and <c>cdlz</c>
/// codecs store it: no header, the literal/position parameters given separately (CHD uses lc=3, lp=0, pb=2), and
/// usually no end marker. Written from the LZMA specification (Igor Pavlov, "lzma-specification.txt").
/// </summary>
public sealed class LzmaDecoder
{
    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int MatchMinLen = 2;
    private const ushort ProbInit = 1024;

    private readonly int _lc;
    private readonly int _lp;
    private readonly int _pb;

    private readonly ushort[] _literal;
    private readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
    private readonly ushort[] _posDecoders = new ushort[1 + NumFullDistances - EndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << NumAlignBits];
    private readonly LengthDecoder _lenDecoder = new();
    private readonly LengthDecoder _repLenDecoder = new();

    // Range decoder state.
    private byte[] _input = [];
    private int _inputPos;
    private int _inputEnd;
    private uint _range;
    private uint _code;

    public LzmaDecoder(int lc = 3, int lp = 0, int pb = 2)
    {
        _lc = lc;
        _lp = lp;
        _pb = pb;
        _literal = new ushort[0x300 << (lc + lp)];
    }

    /// <summary>Decodes <paramref name="source"/> until <paramref name="destination"/> is full (or an end marker).</summary>
    /// <returns>The bytes written.</returns>
    /// <exception cref="InvalidDataException">The stream is corrupt or ends early.</exception>
    public int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        _input = source.ToArray();
        _inputPos = 0;
        _inputEnd = _input.Length;
        Reset();
        if (_inputEnd < 5 || NextByte() != 0)
        {
            throw new InvalidDataException("Corrupt LZMA data: bad range coder start.");
        }

        _range = 0xFFFFFFFF;
        _code = 0;
        for (var i = 0; i < 4; i++)
        {
            _code = (_code << 8) | NextByte();
        }

        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        var state = 0;
        var pos = 0;
        var pbMask = (1 << _pb) - 1;
        while (pos < destination.Length)
        {
            var posState = pos & pbMask;
            if (DecodeBit(_isMatch, (state << NumPosBitsMax) + posState) == 0)
            {
                destination[pos] = DecodeLiteral(destination, pos, state, rep0);
                pos++;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int len;
            if (DecodeBit(_isRep, state) != 0)
            {
                if (pos == 0)
                {
                    throw Corrupt();
                }

                if (DecodeBit(_isRepG0, state) == 0)
                {
                    if (DecodeBit(_isRep0Long, (state << NumPosBitsMax) + posState) == 0)
                    {
                        // Short rep: one byte from rep0.
                        state = state < 7 ? 9 : 11;
                        destination[pos] = destination[pos - (int)rep0 - 1];
                        pos++;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (DecodeBit(_isRepG1, state) == 0)
                    {
                        distance = rep1;
                    }
                    else
                    {
                        if (DecodeBit(_isRepG2, state) == 0)
                        {
                            distance = rep2;
                        }
                        else
                        {
                            distance = rep3;
                            rep3 = rep2;
                        }

                        rep2 = rep1;
                    }

                    rep1 = rep0;
                    rep0 = distance;
                }

                len = _repLenDecoder.Decode(this, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = _lenDecoder.Decode(this, posState);
                state = state < 7 ? 7 : 10;
                rep0 = DecodeDistance(len);
                if (rep0 == 0xFFFFFFFF)
                {
                    return pos; // end marker
                }

                if (rep0 >= pos)
                {
                    throw Corrupt();
                }
            }

            len += MatchMinLen;
            if (len > destination.Length - pos)
            {
                throw Corrupt();
            }

            var from = pos - (int)rep0 - 1;
            for (var i = 0; i < len; i++)
            {
                destination[pos + i] = destination[from + i];
            }

            pos += len;
        }

        return pos;
    }

    private void Reset()
    {
        Array.Fill(_literal, ProbInit);
        Array.Fill(_isMatch, ProbInit);
        Array.Fill(_isRep, ProbInit);
        Array.Fill(_isRepG0, ProbInit);
        Array.Fill(_isRepG1, ProbInit);
        Array.Fill(_isRepG2, ProbInit);
        Array.Fill(_isRep0Long, ProbInit);
        Array.Fill(_posSlot, ProbInit);
        Array.Fill(_posDecoders, ProbInit);
        Array.Fill(_align, ProbInit);
        _lenDecoder.Reset();
        _repLenDecoder.Reset();
    }

    private byte DecodeLiteral(Span<byte> output, int pos, int state, uint rep0)
    {
        var previous = pos > 0 ? output[pos - 1] : 0;
        var litState = ((pos & ((1 << _lp) - 1)) << _lc) + (previous >> (8 - _lc));
        var probs = 0x300 * litState;
        var symbol = 1;
        if (state >= 7)
        {
            var matchByte = (int)output[pos - (int)rep0 - 1];
            do
            {
                var matchBit = (matchByte >> 7) & 1;
                matchByte <<= 1;
                var bit = DecodeBit(_literal, probs + ((1 + matchBit) << 8) + symbol);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit)
                {
                    break;
                }
            }
            while (symbol < 0x100);
        }

        while (symbol < 0x100)
        {
            symbol = (symbol << 1) | DecodeBit(_literal, probs + symbol);
        }

        return (byte)symbol;
    }

    private uint DecodeDistance(int len)
    {
        var lenState = Math.Min(len, NumLenToPosStates - 1);
        var posSlot = BitTree(_posSlot, lenState << 6, 6);
        if (posSlot < 4)
        {
            return (uint)posSlot;
        }

        var directBits = (posSlot >> 1) - 1;
        var distance = (uint)((2 | (posSlot & 1)) << directBits);
        if (posSlot < EndPosModelIndex)
        {
            distance += (uint)ReverseBitTree(_posDecoders, (int)distance - posSlot, directBits);
        }
        else
        {
            distance += DirectBits(directBits - NumAlignBits) << NumAlignBits;
            distance += (uint)ReverseBitTree(_align, 0, NumAlignBits);
        }

        return distance;
    }

    // ---- Range decoder ----

    private byte NextByte()
    {
        if (_inputPos >= _inputEnd)
        {
            throw new InvalidDataException("Corrupt LZMA data: the stream ends early.");
        }

        return _input[_inputPos++];
    }

    private int DecodeBit(ushort[] probs, int index)
    {
        var prob = probs[index];
        var bound = (_range >> 11) * prob;
        int bit;
        if (_code < bound)
        {
            probs[index] = (ushort)(prob + ((2048 - prob) >> 5));
            _range = bound;
            bit = 0;
        }
        else
        {
            probs[index] = (ushort)(prob - (prob >> 5));
            _code -= bound;
            _range -= bound;
            bit = 1;
        }

        Normalize();
        return bit;
    }

    private void Normalize()
    {
        if (_range < 1u << 24)
        {
            _range <<= 8;
            _code = (_code << 8) | NextByte();
        }
    }

    private uint DirectBits(int count)
    {
        uint result = 0;
        for (var i = 0; i < count; i++)
        {
            _range >>= 1;
            _code -= _range;
            var t = 0 - (_code >> 31);
            _code += _range & t;
            Normalize();
            result = (result << 1) + (t + 1);
        }

        return result;
    }

    private int BitTree(ushort[] probs, int offset, int bits)
    {
        var m = 1;
        for (var i = 0; i < bits; i++)
        {
            m = (m << 1) + DecodeBit(probs, offset + m);
        }

        return m - (1 << bits);
    }

    private int ReverseBitTree(ushort[] probs, int offset, int bits)
    {
        var m = 1;
        var symbol = 0;
        for (var i = 0; i < bits; i++)
        {
            var bit = DecodeBit(probs, offset + m);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    private static InvalidDataException Corrupt() => new("Corrupt LZMA data: a match points before the start.");

    private sealed class LengthDecoder
    {
        private readonly ushort[] _choice = new ushort[2];
        private readonly ushort[] _low = new ushort[1 << NumPosBitsMax << 3];
        private readonly ushort[] _mid = new ushort[1 << NumPosBitsMax << 3];
        private readonly ushort[] _high = new ushort[256];

        public void Reset()
        {
            Array.Fill(_choice, ProbInit);
            Array.Fill(_low, ProbInit);
            Array.Fill(_mid, ProbInit);
            Array.Fill(_high, ProbInit);
        }

        public int Decode(LzmaDecoder rc, int posState)
        {
            if (rc.DecodeBit(_choice, 0) == 0)
            {
                return rc.BitTree(_low, posState << 3, 3);
            }

            if (rc.DecodeBit(_choice, 1) == 0)
            {
                return 8 + rc.BitTree(_mid, posState << 3, 3);
            }

            return 16 + rc.BitTree(_high, 0, 8);
        }
    }
}
