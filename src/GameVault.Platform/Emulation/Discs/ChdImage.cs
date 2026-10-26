using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using GameVault.Core.PlayStation;

namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// A CD stored as a CHD ("MAME Compressed Hunks of Data", version 5), as chdman's <c>createcd</c> writes it: hunks
/// of CD frames (2,352 bytes of sector plus 96 of subcode), each compressed with one of up to four codecs, a
/// Huffman-coded hunk map, and track metadata. Supports the CD codecs <c>cdzl</c> (deflate), <c>cdlz</c> (LZMA,
/// subcode in deflate) and <c>cdfl</c> (FLAC audio, subcode in deflate), plain <c>zlib</c> and <c>lzma</c>,
/// uncompressed hunks and copies of earlier hunks. CHDs that need a parent CHD are refused.
/// </summary>
/// <remarks>
/// Written from the CHD v5 format as chdman produces it. Audio in a CHD is stored big-endian and is swapped back on
/// reading. Each track is padded to a multiple of four frames in the CHD; pregaps whose type starts with "V" are
/// stored at the front of the track, others are not stored.
/// </remarks>
public sealed class ChdImage : ExtentCdImage
{
    private const int HeaderSize = 124;
    private const int FrameSize = 2448;
    private const int SectorDataSize = 2352;
    private const int SubcodeSize = 96;
    private const int TrackPadding = 4;

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

    private readonly FileStream _stream;
    private readonly uint[] _codecs = new uint[4];
    private int _hunkBytes;
    private int _hunkCount;
    private MapEntry[] _map = [];

    private readonly LzmaDecoder _lzma = new();
    private readonly FlacDecoder _flac = new();
    private byte[] _hunk = [];
    private byte[] _scratch = [];
    private byte[] _compressed = [];
    private int _cachedHunk = -1;

    private readonly record struct MapEntry(byte Compression, int Length, long Offset, ushort Crc, bool HasCrc = true);

    /// <summary>Sectors whose sync and ECC were regenerated (tests check the ECC path ran).</summary>
    internal int RegeneratedSectors { get; private set; }

    /// <summary>Hunks decoded per codec name ("none" and "self" for stored and copied hunks), for tests.</summary>
    internal Dictionary<string, int> DecodedHunks { get; } = [];

    private void Count(string kind) => DecodedHunks[kind] = DecodedHunks.GetValueOrDefault(kind) + 1;

    private ChdImage(FileStream stream)
    {
        _stream = stream;
    }

    /// <exception cref="InvalidDataException">The file isn't a CD CHD GameVault can read.</exception>
    public static ChdImage Open(string path)
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

        var image = new ChdImage(stream);
        try
        {
            image.Load(Path.GetFileName(path));
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void Load(string name)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExactly(0, header[..16], name);
        if (!header[..8].SequenceEqual("MComprHD"u8))
        {
            throw new InvalidDataException($"'{name}' isn't a CHD file.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (version != 5)
        {
            throw new InvalidDataException(
                $"'{name}' is a version {version} CHD. GameVault reads version 5; update it with \"chdman copy -i old.chd -o new.chd\".");
        }

        ReadExactly(0, header, name);
        for (var i = 0; i < 4; i++)
        {
            _codecs[i] = BinaryPrimitives.ReadUInt32BigEndian(header[(16 + i * 4)..]);
        }

        var logicalBytes = BinaryPrimitives.ReadInt64BigEndian(header[32..]);
        var mapOffset = BinaryPrimitives.ReadInt64BigEndian(header[40..]);
        var metaOffset = BinaryPrimitives.ReadInt64BigEndian(header[48..]);
        _hunkBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[56..]);
        var unitBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header[60..]);
        if (_hunkBytes <= 0 || _hunkBytes % FrameSize != 0 || unitBytes != FrameSize)
        {
            throw new InvalidDataException($"'{name}' isn't a CD image (its hunks aren't made of CD frames).");
        }

        _hunkCount = (int)((logicalBytes + _hunkBytes - 1) / _hunkBytes);
        foreach (var codec in _codecs)
        {
            if (codec != 0 && CodecName(codec) is not ("cdzl" or "cdlz" or "cdfl" or "zlib" or "lzma"))
            {
                throw new InvalidDataException($"'{name}' uses the {CodecName(codec)} codec, which GameVault doesn't read.");
            }
        }

        _hunk = new byte[_hunkBytes];
        _scratch = new byte[_hunkBytes];
        if (_codecs[0] == 0)
        {
            ReadUncompressedMap(mapOffset, name);
        }
        else
        {
            ReadCompressedMap(mapOffset, name, unitBytes);
        }

        BuildLayout(ReadTrackMetadata(metaOffset, name), name);
    }

    private static string CodecName(uint codec) =>
        Encoding.ASCII.GetString([(byte)(codec >> 24), (byte)(codec >> 16), (byte)(codec >> 8), (byte)codec]);

    private void ReadExactly(long offset, Span<byte> buffer, string name)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(_stream.SafeFileHandle, buffer[total..], offset + total);
            if (read <= 0)
            {
                throw new InvalidDataException($"'{name}' is truncated.");
            }

            total += read;
        }
    }

    // ---- The hunk map ----

    private void ReadUncompressedMap(long mapOffset, string name)
    {
        var raw = new byte[_hunkCount * 4];
        ReadExactly(mapOffset, raw, name);
        _map = new MapEntry[_hunkCount];
        for (var i = 0; i < _hunkCount; i++)
        {
            var block = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4));
            _map[i] = new MapEntry(CompressionNone, block == 0 ? 0 : _hunkBytes, (long)block * _hunkBytes, 0, HasCrc: false);
        }
    }

    /// <summary>
    /// The v5 compressed map: a 16-byte header, then a Huffman-coded list of each hunk's compression type (with run
    /// lengths), then each hunk's length, offset or reference and CRC, verified by a CRC-16 over the expanded map.
    /// </summary>
    private void ReadCompressedMap(long mapOffset, string name, int unitBytes)
    {
        Span<byte> mapHeader = stackalloc byte[16];
        ReadExactly(mapOffset, mapHeader, name);
        var mapBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(mapHeader);
        var firstOffset = (long)BinaryPrimitives.ReadUInt16BigEndian(mapHeader[4..]) << 32 | BinaryPrimitives.ReadUInt32BigEndian(mapHeader[6..]);
        var mapCrc = BinaryPrimitives.ReadUInt16BigEndian(mapHeader[10..]);
        int lengthBits = mapHeader[12], selfBits = mapHeader[13], parentBits = mapHeader[14];

        var compressed = new byte[mapBytes];
        ReadExactly(mapOffset + 16, compressed, name);
        var bits = new BitReader(compressed);

        var huffman = new HuffmanDecoder(16, 8);
        huffman.ImportTreeRle(ref bits);
        var types = new byte[_hunkCount];
        byte last = 0;
        var repeat = 0;
        for (var i = 0; i < _hunkCount; i++)
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

        _map = new MapEntry[_hunkCount];
        var raw = new byte[_hunkCount * 12];
        var current = firstOffset;
        long lastSelf = 0, lastParent = 0;
        for (var i = 0; i < _hunkCount; i++)
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
                    length = _hunkBytes;
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
                    lastParent = offset = (long)i * _hunkBytes / unitBytes;
                    break;
                case CompressionParent1:
                case CompressionParent0:
                    if (type == CompressionParent1)
                    {
                        lastParent += _hunkBytes / unitBytes;
                    }

                    type = CompressionParent;
                    offset = lastParent;
                    break;
                default:
                    throw new InvalidDataException($"'{name}' has a corrupt hunk map.");
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
            throw new InvalidDataException($"'{name}' has a corrupt hunk map (CRC mismatch).");
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

    // ---- Track metadata ----

    private sealed record ChdTrack(int Number, string Type, int Frames, int Pregap, string PregapType, int Postgap);

    private List<ChdTrack> ReadTrackMetadata(long offset, string name)
    {
        var tracks = new List<ChdTrack>();
        Span<byte> entryHeader = stackalloc byte[16];
        var guard = 0;
        while (offset != 0 && guard++ < 10_000)
        {
            ReadExactly(offset, entryHeader, name);
            var tag = CodecName(BinaryPrimitives.ReadUInt32BigEndian(entryHeader));
            var length = (int)(BinaryPrimitives.ReadUInt32BigEndian(entryHeader[4..]) & 0xFFFFFF);
            var next = BinaryPrimitives.ReadInt64BigEndian(entryHeader[8..]);
            if (tag is "CHT2" or "CHTR" or "CHCD" or "CHGD")
            {
                if (tag is "CHCD")
                {
                    throw new InvalidDataException($"'{name}' uses old binary CD metadata; update it with \"chdman copy\".");
                }

                if (tag is "CHGD")
                {
                    throw new InvalidDataException($"'{name}' is a GD-ROM image, not a CD.");
                }

                var data = new byte[length];
                ReadExactly(offset + 16, data, name);
                tracks.Add(ParseTrack(Encoding.ASCII.GetString(data).TrimEnd('\0'), name));
            }

            offset = next;
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException($"'{name}' has no CD track list; it may be a hard disk or LaserDisc CHD.");
        }

        tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return tracks;
    }

    private static ChdTrack ParseTrack(string text, string name)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon > 0)
            {
                fields[part[..colon]] = part[(colon + 1)..];
            }
        }

        int Number(string key) =>
            fields.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        if (!fields.TryGetValue("TYPE", out var type) || Number("TRACK") <= 0)
        {
            throw new InvalidDataException($"'{name}' has corrupt track metadata: \"{text}\".");
        }

        return new ChdTrack(Number("TRACK"), type, Number("FRAMES"), Number("PREGAP"), fields.GetValueOrDefault("PGTYPE", string.Empty), Number("POSTGAP"));
    }

    private static (CdTrackType Type, int SectorSize) TrackFormat(string type, string name) => type.ToUpperInvariant() switch
    {
        "AUDIO" => (CdTrackType.Audio, 2352),
        "MODE1_RAW" => (CdTrackType.Mode1, 2352),
        "MODE1" => (CdTrackType.Mode1, 2048),
        "MODE2_RAW" => (CdTrackType.Mode2, 2352),
        "MODE2" or "MODE2_FORM_MIX" => (CdTrackType.Mode2, 2336),
        "MODE2_FORM1" => (CdTrackType.Mode2, 2048),
        "MODE2_FORM2" => (CdTrackType.Mode2, 2324),
        _ => throw new InvalidDataException($"'{name}' has a track of type {type}, which GameVault doesn't read."),
    };

    private void BuildLayout(List<ChdTrack> chdTracks, string name)
    {
        var tracks = new List<CdTrack>();
        var extents = new List<DiscExtent>();
        var chdFrame = 0L;
        var cursor = -1;
        foreach (var t in chdTracks)
        {
            var (type, size) = TrackFormat(t.Type, name);
            var pregapStored = t.PregapType.StartsWith('V') ? t.Pregap : 0;
            if (cursor < 0)
            {
                cursor = Math.Max(0, 150 - t.Pregap);
            }

            // A pregap that isn't stored reads as zeros; a stored one is the first frames of the track.
            cursor += t.Pregap - pregapStored;
            if (pregapStored > 0)
            {
                extents.Add(new DiscExtent(cursor, pregapStored, true, 0, chdFrame, size, type, type == CdTrackType.Audio));
            }

            var start = cursor + pregapStored;
            var length = t.Frames - pregapStored;
            if (length > 0)
            {
                extents.Add(new DiscExtent(start, length, true, 0, chdFrame + pregapStored, size, type, type == CdTrackType.Audio));
            }

            tracks.Add(new CdTrack(t.Number, type, start, Math.Max(0, length), t.Pregap));
            cursor = start + Math.Max(0, length) + t.Postgap;
            chdFrame += (t.Frames + TrackPadding - 1) / TrackPadding * TrackPadding;
        }

        SetLayout(tracks, extents, cursor);
    }

    // ---- Hunks ----

    private protected override void ReadStored(DiscExtent extent, int index, Span<byte> data)
    {
        var frame = extent.Source + index;
        var framesPerHunk = _hunkBytes / FrameSize;
        var hunk = (int)(frame / framesPerHunk);
        if (hunk >= _hunkCount)
        {
            data.Clear();
            return;
        }

        LoadHunk(hunk);
        var offset = (int)(frame % framesPerHunk) * FrameSize;
        _hunk.AsSpan(offset, data.Length).CopyTo(data);
    }

    private void LoadHunk(int hunk)
    {
        if (hunk == _cachedHunk)
        {
            return;
        }

        _cachedHunk = -1;
        DecodeHunk(hunk, _hunk, depth: 0);
        _cachedHunk = hunk;
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

                ReadExactly(entry.Offset, destination.AsSpan(0, _hunkBytes), "CHD");
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
                ReadExactly(entry.Offset, source, "CHD");
                var codec = CodecName(_codecs[entry.Compression]);
                Decompress(codec, source, destination.AsSpan(0, _hunkBytes));
                Count(codec);
                break;
        }

        if (entry.HasCrc && Crc16(destination.AsSpan(0, _hunkBytes)) != entry.Crc)
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
        }

        // The CD codecs: a bit per frame saying the sector's sync and ECC were stripped, the length of the
        // compressed sector data, then the sector data and the subcode, compressed separately.
        var frames = destination.Length / FrameSize;
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
            var frame = destination.Slice(f * FrameSize, FrameSize);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
