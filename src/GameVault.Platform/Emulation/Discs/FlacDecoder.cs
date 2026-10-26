namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// Decodes a sequence of FLAC frames with no stream header, as CHD's <c>flac</c> and <c>cdfl</c> codecs store
/// them (16-bit stereo for CD audio). Supports every subframe type (constant, verbatim, fixed and LPC predictors),
/// partitioned Rice residuals with escapes, wasted bits and the three stereo decorrelation modes. Written from the
/// FLAC format specification (RFC 9639).
/// </summary>
public sealed class FlacDecoder
{
    private readonly int _defaultBitsPerSample;

    private int[][] _channels = [new int[4096], new int[4096]];

    /// <param name="bitsPerSample">The sample size for frames that leave it to the (absent) stream header.</param>
    public FlacDecoder(int bitsPerSample = 16)
    {
        _defaultBitsPerSample = bitsPerSample;
    }

    /// <summary>
    /// Decodes frames from <paramref name="source"/> until <paramref name="samples"/> interleaved stereo 16-bit samples
    /// have been written to <paramref name="destination"/> (four bytes each), big-endian when
    /// <paramref name="bigEndian"/> is set.
    /// </summary>
    /// <returns>The bytes of <paramref name="source"/> the frames took up.</returns>
    /// <exception cref="InvalidDataException">The frames are corrupt.</exception>
    public int DecodeStereo16(ReadOnlySpan<byte> source, int samples, Span<byte> destination, bool bigEndian)
    {
        var reader = new BitReader(source);
        var written = 0;
        while (written < samples)
        {
            var blockSize = DecodeFrame(ref reader, out var channelCount);
            if (channelCount != 2)
            {
                throw new InvalidDataException("FLAC: CD audio must have two channels.");
            }

            var count = Math.Min(blockSize, samples - written);
            for (var i = 0; i < count; i++)
            {
                var offset = (written + i) * 4;
                WriteSample(destination[offset..], _channels[0][i], bigEndian);
                WriteSample(destination[(offset + 2)..], _channels[1][i], bigEndian);
            }

            written += count;
        }

        return reader.BytePosition;
    }

    private static void WriteSample(Span<byte> destination, int sample, bool bigEndian)
    {
        var value = (ushort)(short)sample;
        if (bigEndian)
        {
            destination[0] = (byte)(value >> 8);
            destination[1] = (byte)value;
        }
        else
        {
            destination[0] = (byte)value;
            destination[1] = (byte)(value >> 8);
        }
    }

    /// <summary>Decodes one frame into <see cref="_channels"/>; returns its block size.</summary>
    private int DecodeFrame(ref BitReader reader, out int channelCount)
    {
        var frameStart = reader.BytePosition;
        if (reader.Read(14) != 0x3FFE)
        {
            throw new InvalidDataException("FLAC: frame sync not found.");
        }

        reader.Read(1); // reserved
        reader.Read(1); // blocking strategy
        var blockSizeCode = (int)reader.Read(4);
        var sampleRateCode = (int)reader.Read(4);
        var channelAssignment = (int)reader.Read(4);
        var sampleSizeCode = (int)reader.Read(3);
        reader.Read(1);
        ReadCodedNumber(ref reader);

        var blockSize = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)reader.Read(8) + 1,
            7 => (int)reader.Read(16) + 1,
            >= 8 => 256 << (blockSizeCode - 8),
            _ => throw new InvalidDataException("FLAC: reserved block size."),
        };

        switch (sampleRateCode)
        {
            case 12:
                reader.Read(8);
                break;
            case 13:
            case 14:
                reader.Read(16);
                break;
            case 15:
                throw new InvalidDataException("FLAC: invalid sample rate.");
        }

        var bitsPerSample = sampleSizeCode switch
        {
            0 => _defaultBitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => throw new InvalidDataException("FLAC: reserved sample size."),
        };

        var headerEnd = reader.BytePosition;
        var crc8 = (byte)reader.Read(8);
        if (Crc8(reader.Source[frameStart..headerEnd]) != crc8)
        {
            throw new InvalidDataException("FLAC: frame header CRC mismatch.");
        }

        channelCount = channelAssignment <= 7 ? channelAssignment + 1 : channelAssignment <= 10 ? 2 : throw new InvalidDataException("FLAC: reserved channel assignment.");
        if (_channels.Length < channelCount || _channels[0].Length < blockSize)
        {
            _channels = new int[Math.Max(channelCount, 2)][];
            for (var c = 0; c < _channels.Length; c++)
            {
                _channels[c] = new int[Math.Max(blockSize, 4096)];
            }
        }

        for (var c = 0; c < channelCount; c++)
        {
            // The side channel carries one more bit.
            var side = (channelAssignment == 8 && c == 1) || (channelAssignment == 9 && c == 0) || (channelAssignment == 10 && c == 1);
            DecodeSubframe(ref reader, _channels[c].AsSpan(0, blockSize), bitsPerSample + (side ? 1 : 0));
        }

        Decorrelate(channelAssignment, blockSize);

        reader.AlignToByte();
        var frameEnd = reader.BytePosition;
        var crc16 = (ushort)reader.Read(16);
        if (Crc16(reader.Source[frameStart..frameEnd]) != crc16)
        {
            throw new InvalidDataException("FLAC: frame CRC mismatch.");
        }

        if (reader.Overrun)
        {
            throw new InvalidDataException("FLAC: the frame runs past the end of the data.");
        }

        return blockSize;
    }

    private void Decorrelate(int assignment, int blockSize)
    {
        var a = _channels[0];
        var b = _channels[1];
        switch (assignment)
        {
            case 8: // left/side
                for (var i = 0; i < blockSize; i++)
                {
                    b[i] = a[i] - b[i];
                }

                break;
            case 9: // side/right
                for (var i = 0; i < blockSize; i++)
                {
                    a[i] += b[i];
                }

                break;
            case 10: // mid/side
                for (var i = 0; i < blockSize; i++)
                {
                    var side = b[i];
                    var mid = (a[i] << 1) | (side & 1);
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                }

                break;
        }
    }

    private static void ReadCodedNumber(ref BitReader reader)
    {
        // UTF-8-style: the leading ones of the first byte give the count of continuation bytes.
        var first = reader.Read(8);
        var extra = 0;
        while (extra < 7 && (first & (0x80u >> extra)) != 0)
        {
            extra++;
        }

        if (extra == 1 || extra > 7)
        {
            throw new InvalidDataException("FLAC: bad frame number.");
        }

        for (var i = 1; i < extra; i++)
        {
            if ((reader.Read(8) & 0xC0) != 0x80)
            {
                throw new InvalidDataException("FLAC: bad frame number.");
            }
        }
    }

    private static void DecodeSubframe(ref BitReader reader, Span<int> samples, int bitsPerSample)
    {
        if (reader.Read(1) != 0)
        {
            throw new InvalidDataException("FLAC: bad subframe padding.");
        }

        var type = (int)reader.Read(6);
        var wasted = 0;
        if (reader.Read(1) != 0)
        {
            wasted = 1;
            while (reader.Read(1) == 0)
            {
                wasted++;
            }

            bitsPerSample -= wasted;
        }

        switch (type)
        {
            case 0:
                samples.Fill(reader.ReadSigned(bitsPerSample));
                break;
            case 1:
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = reader.ReadSigned(bitsPerSample);
                }

                break;
            case >= 8 and <= 12:
                DecodeFixed(ref reader, samples, bitsPerSample, type - 8);
                break;
            case >= 32:
                DecodeLpc(ref reader, samples, bitsPerSample, type - 31);
                break;
            default:
                throw new InvalidDataException("FLAC: reserved subframe type.");
        }

        if (wasted > 0)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private static void DecodeFixed(ref BitReader reader, Span<int> samples, int bitsPerSample, int order)
    {
        for (var i = 0; i < order; i++)
        {
            samples[i] = reader.ReadSigned(bitsPerSample);
        }

        DecodeResidual(ref reader, samples, order);
        for (var i = order; i < samples.Length; i++)
        {
            long prediction = order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2L * samples[i - 1] - samples[i - 2],
                3 => 3L * samples[i - 1] - 3L * samples[i - 2] + samples[i - 3],
                _ => 4L * samples[i - 1] - 6L * samples[i - 2] + 4L * samples[i - 3] - samples[i - 4],
            };
            samples[i] = (int)(samples[i] + prediction);
        }
    }

    private static void DecodeLpc(ref BitReader reader, Span<int> samples, int bitsPerSample, int order)
    {
        for (var i = 0; i < order; i++)
        {
            samples[i] = reader.ReadSigned(bitsPerSample);
        }

        var precision = (int)reader.Read(4) + 1;
        if (precision == 16)
        {
            throw new InvalidDataException("FLAC: invalid LPC precision.");
        }

        var shift = reader.ReadSigned(5);
        if (shift < 0)
        {
            throw new InvalidDataException("FLAC: negative LPC shift.");
        }

        Span<int> coefficients = stackalloc int[32];
        for (var i = 0; i < order; i++)
        {
            coefficients[i] = reader.ReadSigned(precision);
        }

        DecodeResidual(ref reader, samples, order);
        for (var i = order; i < samples.Length; i++)
        {
            long sum = 0;
            for (var j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * samples[i - 1 - j];
            }

            samples[i] = (int)(samples[i] + (sum >> shift));
        }
    }

    /// <summary>Reads the residual into <c>samples[order..]</c>, to be added to the predictions.</summary>
    private static void DecodeResidual(ref BitReader reader, Span<int> samples, int order)
    {
        var method = reader.Read(2);
        if (method > 1)
        {
            throw new InvalidDataException("FLAC: reserved residual coding method.");
        }

        var parameterBits = method == 0 ? 4 : 5;
        var escape = (1u << parameterBits) - 1;
        var partitionOrder = (int)reader.Read(4);
        var partitions = 1 << partitionOrder;
        var perPartition = samples.Length >> partitionOrder;
        if (perPartition << partitionOrder != samples.Length || perPartition < order)
        {
            throw new InvalidDataException("FLAC: bad residual partitioning.");
        }

        var index = order;
        for (var p = 0; p < partitions; p++)
        {
            var count = p == 0 ? perPartition - order : perPartition;
            var parameter = reader.Read(parameterBits);
            if (parameter == escape)
            {
                var bits = (int)reader.Read(5);
                for (var i = 0; i < count; i++)
                {
                    samples[index++] = bits == 0 ? 0 : reader.ReadSigned(bits);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var quotient = reader.ReadUnary();
                    var value = (quotient << (int)parameter) | reader.Read((int)parameter);
                    samples[index++] = (int)(value >> 1) ^ -(int)(value & 1);
                }
            }
        }
    }

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }

        return crc;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
        }

        return crc;
    }
}
