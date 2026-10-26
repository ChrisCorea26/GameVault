using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GameVault.Library.Discs;

/// <summary>
/// The container side of a CHD ("MAME Compressed Hunks of Data", version 5): the header, the hunk map and the hunks,
/// each compressed with one of up to four codecs, plus the metadata chain. <see cref="ChdImage"/> reads CDs through
/// it (hunks of 2,448-byte frames) and <see cref="ChdDvd"/> DVDs (hunks of 2,048-byte sectors).
/// </summary>
/// <remarks>
/// Codecs: the generic <c>zlib</c> (deflate), <c>lzma</c>, <c>huff</c> (8-bit Huffman) and <c>flac</c> (the hunk as
/// 16-bit stereo samples), and the CD codecs <c>cdzl</c>, <c>cdlz</c> and <c>cdfl</c>; uncompressed hunks and copies
/// of earlier hunks. CHDs that need a parent CHD are refused. Not thread-safe: callers serialize reads.
/// </remarks>
internal sealed class ChdFile : IDisposable
{
    public const int CdFrameSize = 2448;
    private const int HeaderSize = 124;
    private const int SectorDataSize = 2352;
    private const int SubcodeSize = 96;

    // Hunk compression types in the map.
    private const int CompressionNone = 4;
    private const int CompressionSelf = 5;
    private const int CompressionParent = 6;
    private const int CompressionRleSmall = 7;
    private const int CompressionRleLarge = 8;
    private const int CompressionSelf0 = 9;
    private const int CompressionSelf1 = 10;
    private const int CompressionParentSelf = 11;
    private const int CompressionParent0 = 12;
    private const int CompressionParent1 = 13;

    private static readonly string[] SupportedCodecs = ["cdzl", "cdlz", "cdfl", "zlib", "lzma", "huff", "flac"];

    private readonly FileStream _stream;
    private readonly string _name;
    private readonly uint[] _codecs = new uint[4];
    private MapEntry[] _map = [];
    private long _metaOffset;

    private readonly LzmaDecoder _lzma = new();
    private readonly FlacDecoder _flac = new();
    private readonly HuffmanDecoder _huffman = new(256, 16);
    private byte[] _hunk = [];
    private byte[] _scratch = [];
    private byte[] _compressed = [];
    private int _cachedHunk = -1;

    private readonly record struct MapEntry(byte Compression, int Length, long Offset, ushort Crc, bool HasCrc = true);

    private ChdFile(FileStream stream, string name)
    {
        _stream = stream;
        _name = name;
    }

    public int HunkBytes { get; private set; }

    public int UnitBytes { get; private set; }

    public int HunkCount { get; private set; }

    public long LogicalBytes { get; private set; }

    /// <summary>CD sectors whose sync and ECC were regenerated (tests check the ECC path ran).</summary>
    public int RegeneratedSectors { get; private set; }

    /// <summary>Hunks decoded per codec name ("none" and "self" for stored and copied hunks), for tests.</summary>
    public Dictionary<string, int> DecodedHunks { get; } = [];

    private void Count(string kind) => DecodedHunks[kind] = DecodedHunks.GetValueOrDefault(kind) + 1;

    /// <exception cref="InvalidDataException">The file isn't a version 5 CHD with codecs GameVault reads.</exception>
    public static ChdFile Open(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' can't be opened: {ex.Message}", ex);
        }

        var file = new ChdFile(stream, Path.GetFileName(path));
        try
        {
            file.Load();
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private void Load()
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExactly(0, header[..16]);
        if (!header[..8].SequenceEqual("MComprHD"u8))
        {
            throw new InvalidDataException($"'{_name}' isn't a CHD file.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (version != 5)
        {
            throw new InvalidDataException(
                $"'{_name}' is a version {version} CHD. GameVault reads version 5; update it with \"chdman copy -i old.chd -o new.chd\".");
        }

        ReadExactly(0, header);
        for (var i = 0; i < 4; i++)
        {
            _codecs[i] = BinaryPrimitives.ReadUInt32BigEndian(header[(16 + i * 4)..]);
        }

        LogicalBytes = BinaryPrimitives.ReadInt64BigEndian(header[32..]);
        var mapOffset = BinaryPrimitives.ReadInt64BigEndian(header[40..]);
        _metaOffset = BinaryPrimitives.ReadInt64BigEndian(header[48..]);
        HunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[56..]);
        UnitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[60..]);
        if (HunkBytes <= 0 || UnitBytes <= 0 || HunkBytes % UnitBytes != 0 || LogicalBytes < 0)
        {
            throw new InvalidDataException($"'{_name}' has a corrupt header.");
        }

        HunkCount = (int)((LogicalBytes + HunkBytes - 1) / HunkBytes);
        foreach (var codec in _codecs)
        {
            if (codec != 0 && Array.IndexOf(SupportedCodecs, CodecName(codec)) < 0)
            {
                throw new InvalidDataException($"'{_name}' uses the {CodecName(codec)} codec, which GameVault doesn't read.");
            }
        }

        _hunk = new byte[HunkBytes];
        _scratch = new byte[HunkBytes];
        if (_codecs[0] == 0)
        {
            ReadUncompressedMap(mapOffset);
        }
        else
        {
            ReadCompressedMap(mapOffset);
        }
    }

    public static string CodecName(uint codec) =>
        Encoding.ASCII.GetString([(byte)(codec >> 24), (byte)(codec >> 16), (byte)(codec >> 8), (byte)codec]);

    private void ReadExactly(long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(_stream.SafeFileHandle, buffer[total..], offset + total);
            if (read <= 0)
            {
                throw new InvalidDataException($"'{_name}' is truncated.");
            }

            total += read;
        }
    }

    // ---- Metadata ----

    /// <summary>The metadata entries in file order: each one's four-character tag and data.</summary>
    public List<(string Tag, byte[] Data)> ReadMetadata()
    {
        var entries = new List<(string, byte[])>();
        Span<byte> entryHeader = stackalloc byte[16];
        var offset = _metaOffset;
        var guard = 0;
        while (offset != 0 && guard++ < 10_000)
        {
            ReadExactly(offset, entryHeader);
            var tag = CodecName(BinaryPrimitives.ReadUInt32BigEndian(entryHeader));
            var length = (int)(BinaryPrimitives.ReadUInt32BigEndian(entryHeader[4..]) & 0xFFFFFF);
            var next = BinaryPrimitives.ReadInt64BigEndian(entryHeader[8..]);
            var data = new byte[length];
            ReadExactly(offset + 16, data);
            entries.Add((tag, data));
            offset = next;
        }

        return entries;
    }

    // ---- The hunk map ----

    private void ReadUncompressedMap(long mapOffset)
    {
        var raw = new byte[HunkCount * 4];
        ReadExactly(mapOffset, raw);
        _map = new MapEntry[HunkCount];
        for (var i = 0; i < HunkCount; i++)
        {
            var block = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));
            _map[i] = new MapEntry(CompressionNone, block == 0 ? 0 : HunkBytes, (long)block * HunkBytes, 0, HasCrc: false);
        }
    }

    /// <summary>
    /// The v5 compressed map: a 16-byte header, then a Huffman-coded list of each hunk's compression type (with run
    /// lengths), then each hunk's length, offset or reference and CRC, verified by a CRC-16 over the expanded map.
    /// </summary>
    private void ReadCompressedMap(long mapOffset)
    {
        Span<byte> mapHeader = stackalloc byte[16];
        ReadExactly(mapOffset, mapHeader);
        var mapBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(mapHeader);
        var firstOffset = (long)BinaryPrimitives.ReadUInt16BigEndian(mapHeader[4..]) << 32 | BinaryPrimitives.ReadUInt32BigEndian(mapHeader[6..]);
        var mapCrc = BinaryPrimitives.ReadUInt16BigEndian(mapHeader[10..]);
        int lengthBits = mapHeader[12], selfBits = mapHeader[13], parentBits = mapHeader[14];

        var compressed = new byte[mapBytes];
        ReadExactly(mapOffset + 16, compressed);
        var bits = new BitReader(compressed);

        var huffman = new HuffmanDecoder(16, 8);
        huffman.ImportTreeRle(ref bits);
        var types = new byte[HunkCount];
        byte last = 0;
        var repeat = 0;
        for (var i = 0; i < HunkCount; i++)
        {
            if (repeat > 0)
            {
                types[i] = last;
                repeat--;
                continue;
            }

            var value = huffman.Decode(ref bits);
            if (value == CompressionRleSmall)
            {
                types[i] = last;
                repeat = 2 + huffman.Decode(ref bits);
            }
            else if (value == CompressionRleLarge)
            {
                types[i] = last;
                repeat = 2 + 16 + (huffman.Decode(ref bits) << 4);
                repeat += huffman.Decode(ref bits);
            }
            else
            {
                types[i] = last = (byte)value;
            }
        }

        _map = new MapEntry[HunkCount];
        var raw = new byte[HunkCount * 12];
        var current = firstOffset;
        long lastSelf = 0, lastParent = 0;
        for (var i = 0; i < HunkCount; i++)
        {
            var type = types[i];
            long offset = current;
            var length = 0;
            ushort crc = 0;
            switch (type)
            {
                case <= 3:
                    length = (int)bits.Read(lengthBits);
                    current += length;
                    crc = (ushort)bits.Read(16);
                    break;
                case CompressionNone:
                    length = HunkBytes;
                    current += length;
                    crc = (ushort)bits.Read(16);
                    break;
                case CompressionSelf:
                    lastSelf = offset = bits.Read(selfBits);
                    break;
                case CompressionParent:
                    lastParent = offset = bits.Read(parentBits);
                    break;
                case CompressionSelf1:
                case CompressionSelf0:
                    if (type == CompressionSelf1)
                    {
                        lastSelf++;
                    }

                    type = CompressionSelf;
                    offset = lastSelf;
                    break;
                case CompressionParentSelf:
                    type = CompressionParent;
                    lastParent = offset = (long)i * HunkBytes / UnitBytes;
                    break;
                case CompressionParent1:
                case CompressionParent0:
                    if (type == CompressionParent1)
                    {
                        lastParent += HunkBytes / UnitBytes;
                    }

                    type = CompressionParent;
                    offset = lastParent;
                    break;
                default:
                    throw new InvalidDataException($"'{_name}' has a corrupt hunk map.");
            }

            _map[i] = new MapEntry(type, length, offset, crc);
            var entry = raw.AsSpan(i * 12, 12);
            entry[0] = type;
            entry[1] = (byte)(length >> 16);
            entry[2] = (byte)(length >> 8);
            entry[3] = (byte)length;
            for (var b = 0; b < 6; b++)
            {
                entry[4 + b] = (byte)(offset >> (40 - 8 * b));
            }

            entry[10] = (byte)(crc >> 8);
            entry[11] = (byte)crc;
        }

        if (bits.Overrun || Crc16(raw) != mapCrc)
        {
            throw new InvalidDataException($"'{_name}' has a corrupt hunk map (CRC mismatch).");
        }
    }

    /// <summary>CRC-16/CCITT (polynomial 0x1021, starting at 0xFFFF), as CHD uses for the map and each hunk.</summary>
    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return crc;
    }

    // ---- Hunks ----

    /// <summary>The decoded bytes of <paramref name="hunk"/> (valid until the next call); zeros past the last hunk.</summary>
    public ReadOnlySpan<byte> Hunk(int hunk)
    {
        if (hunk < 0 || hunk >= HunkCount)
        {
            _cachedHunk = -1;
            Array.Clear(_hunk);
            return _hunk;
        }

        if (hunk != _cachedHunk)
        {
            _cachedHunk = -1;
            DecodeHunk(hunk, _hunk, depth: 0);
            _cachedHunk = hunk;
        }

        return _hunk;
    }

    private void DecodeHunk(int hunk, byte[] destination, int depth)
    {
        var entry = _map[hunk];
        switch (entry.Compression)
        {
            case CompressionNone:
                if (entry.Length == 0)
                {
                    Array.Clear(destination);
                    return;
                }

                ReadExactly(entry.Offset, destination.AsSpan(0, HunkBytes));
                Count("none");
                break;
            case CompressionSelf:
                if (entry.Offset >= hunk || depth > 16)
                {
                    throw new InvalidDataException("The CHD's hunk map refers forward.");
                }

                Count("self");
                DecodeHunk((int)entry.Offset, destination, depth + 1);
                return;
            case CompressionParent:
                throw new InvalidDataException("This CHD needs its parent CHD, which GameVault doesn't support; merge it with \"chdman copy\".");
            default:
                if (_compressed.Length < entry.Length)
                {
                    _compressed = new byte[entry.Length];
                }

                var source = _compressed.AsSpan(0, entry.Length);
                ReadExactly(entry.Offset, source);
                var codec = CodecName(_codecs[entry.Compression]);
                Decompress(codec, source, destination.AsSpan(0, HunkBytes));
                Count(codec);
                break;
        }

        if (entry.HasCrc && Crc16(destination.AsSpan(0, HunkBytes)) != entry.Crc)
        {
            throw new InvalidDataException($"The CHD's hunk {hunk} is corrupt (CRC mismatch).");
        }
    }

    private void Decompress(string codec, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        switch (codec)
        {
            case "zlib":
                Inflate(source, destination);
                return;
            case "lzma":
                if (_lzma.Decode(source, destination) != destination.Length)
                {
                    throw new InvalidDataException("A CHD hunk's LZMA data ends early.");
                }

                return;
            case "huff":
                _huffman.DecodeBytes(source, destination);
                return;
            case "flac":
                // The hunk as 16-bit stereo samples: 'L' or 'B' for the byte order they were taken in, then FLAC frames
                // (MAME chdcodec.cpp, chd_flac_decompressor).
                if (source.IsEmpty || source[0] is not ((byte)'L' or (byte)'B'))
                {
                    throw new InvalidDataException("A CHD hunk's FLAC data is corrupt.");
                }

                _flac.DecodeStereo16(source[1..], destination.Length / 4, destination, bigEndian: source[0] == 'B');
                return;
        }

        if (UnitBytes != CdFrameSize)
        {
            throw new InvalidDataException($"'{_name}' uses a CD codec ({codec}) for data that isn't CD frames.");
        }

        // The CD codecs: a bit per frame saying the sector's sync and ECC were stripped, the length of the
        // compressed sector data, then the sector data and the subcode, compressed separately.
        var frames = destination.Length / CdFrameSize;
        var sectorBytes = frames * SectorDataSize;
        var subcodeBytes = frames * SubcodeSize;
        var sectors = _scratch.AsSpan(0, sectorBytes);
        var subcode = _scratch.AsSpan(sectorBytes, subcodeBytes);
        var eccBytes = (frames + 7) / 8;

        if (codec == "cdfl")
        {
            // FLAC audio in blocks of up to 2,352 samples, then the subcode in deflate. No ECC flags.
            var consumed = _flac.DecodeStereo16(source, sectorBytes / 4, sectors, bigEndian: true);
            Inflate(source[consumed..], subcode);
            eccBytes = 0;
        }
        else
        {
            var lengthBytes = destination.Length < 65536 ? 2 : 3;
            var headerBytes = eccBytes + lengthBytes;
            var baseLength = (int)(source[eccBytes] << 8 | source[eccBytes + 1]);
            if (lengthBytes > 2)
            {
                baseLength = baseLength << 8 | source[eccBytes + 2];
            }

            var baseData = source.Slice(headerBytes, baseLength);
            if (codec == "cdlz")
            {
                if (_lzma.Decode(baseData, sectors) != sectorBytes)
                {
                    throw new InvalidDataException("A CHD hunk's LZMA data ends early.");
                }
            }
            else
            {
                Inflate(baseData, sectors);
            }

            Inflate(source[(headerBytes + baseLength)..], subcode);
        }

        for (var f = 0; f < frames; f++)
        {
            var frame = destination.Slice(f * CdFrameSize, CdFrameSize);
            sectors.Slice(f * SectorDataSize, SectorDataSize).CopyTo(frame);
            subcode.Slice(f * SubcodeSize, SubcodeSize).CopyTo(frame[SectorDataSize..]);
            if (eccBytes > 0 && (source[f / 8] & (1 << (f % 8))) != 0)
            {
                CdSector.Sync.CopyTo(frame);
                CdSector.GenerateEcc(frame);
                RegeneratedSectors++;
            }
        }
    }

    private static void Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        var total = 0;
        while (total < destination.Length)
        {
            var read = inflater.Read(destination[total..]);
            if (read == 0)
            {
                // A short stream leaves zeros; the hunk's CRC check catches real corruption.
                destination[total..].Clear();
                return;
            }

            total += read;
        }
    }

    public void Dispose() => _stream.Dispose();
}
