using System.Buffers.Binary;
using GameVault.Core.Cartridge;

namespace GameVault.Core.Apple;

/// <summary>
/// A 5.25" floppy as the Disk II sees it (SPEC §7.11, Phase 12): up to 160 quarter-track positions, each mapped
/// to a track of raw bits. <c>.dsk</c>/<c>.do</c>/<c>.po</c> images are nibblized with 6-and-2 GCR on load,
/// <c>.nib</c> images hold the nibbles, and <c>.woz</c> images hold the exact bits (so copy-protected disks work).
/// </summary>
/// <remarks>
/// References: "Beneath Apple DOS" (Worth and Lechner), chapter 3; the WOZ disk image reference (applesaucefdc.com).
/// </remarks>
public sealed class AppleDisk
{
    public const int Tracks = 35;
    public const int SectorsPerTrack = 16;
    public const int TrackBytes = SectorsPerTrack * 256;
    public const int DiskBytes = Tracks * TrackBytes;
    public const int NibTrackBytes = 6656;

    /// <summary>Physical sector → DOS 3.3 logical sector (Beneath Apple DOS, table 3.3).</summary>
    private static readonly int[] DosOrder = [0, 7, 14, 6, 13, 5, 12, 4, 11, 3, 10, 2, 9, 1, 8, 15];

    /// <summary>Physical sector → ProDOS block half.</summary>
    private static readonly int[] ProDosOrder = [0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15];

    /// <summary>The 64 valid disk bytes for 6-and-2 encoding.</summary>
    private static readonly byte[] Translate =
    [
        0x96, 0x97, 0x9A, 0x9B, 0x9D, 0x9E, 0x9F, 0xA6, 0xA7, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB2, 0xB3,
        0xB4, 0xB5, 0xB6, 0xB7, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xCB, 0xCD, 0xCE, 0xCF, 0xD3,
        0xD6, 0xD7, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE5, 0xE6, 0xE7, 0xE9, 0xEA, 0xEB, 0xEC,
        0xED, 0xEE, 0xEF, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
    ];

    private const int PatchMagic = 0x53324147; // "GA2S"

    private readonly int[] _quarterTrackMap = new int[160];

    private AppleDisk(List<DiskTrack> tracks, int[] quarterTrackMap, bool writeProtected)
    {
        TrackData = tracks;
        quarterTrackMap.CopyTo(_quarterTrackMap, 0);
        WriteProtected = writeProtected;
    }

    /// <summary>The tracks' bits; <see cref="TrackAt"/> picks one by head position.</summary>
    public IReadOnlyList<DiskTrack> TrackData { get; }

    public bool WriteProtected { get; }

    /// <summary>True once the computer has written to the disk.</summary>
    public bool IsModified { get; internal set; }

    /// <summary>A copy of every track's bits, to compare against later for <see cref="CreatePatch"/>.</summary>
    public byte[][] SnapshotTracks()
    {
        var result = new byte[TrackData.Count][];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = [.. TrackData[i].Bits];
        }

        return result;
    }

    /// <summary>
    /// The tracks that differ from <paramref name="original"/> (a <see cref="SnapshotTracks"/> of this disk), as
    /// "track index, byte count, bits" records; the player's image file is never rewritten.
    /// </summary>
    public byte[] CreatePatch(byte[][] original)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PatchMagic);
        for (var i = 0; i < TrackData.Count && i < original.Length; i++)
        {
            var bits = TrackData[i].Bits;
            if (!bits.AsSpan().SequenceEqual(original[i]))
            {
                writer.Write(i);
                writer.Write(bits.Length);
                writer.Write(bits);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Puts the tracks a <see cref="CreatePatch"/> saved back on this disk.</summary>
    public void ApplyPatch(ReadOnlySpan<byte> patch)
    {
        if (patch.Length < 4 || BinaryPrimitives.ReadInt32LittleEndian(patch) != PatchMagic)
        {
            throw new InvalidDataException("This isn't a GameVault Apple II disk save.");
        }

        var offset = 4;
        while (offset + 8 <= patch.Length)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(patch[offset..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(patch[(offset + 4)..]);
            offset += 8;
            if (index < 0 || index >= TrackData.Count || length != TrackData[index].Bits.Length || offset + length > patch.Length)
            {
                throw new InvalidDataException("This Apple II disk save doesn't match the disk.");
            }

            patch.Slice(offset, length).CopyTo(TrackData[index].Bits);
            offset += length;
        }
    }

    /// <summary>The track under the head at <paramref name="quarterTrack"/> (0–159), or null over an empty position.</summary>
    public DiskTrack? TrackAt(int quarterTrack)
    {
        var index = quarterTrack is >= 0 and < 160 ? _quarterTrackMap[quarterTrack] : -1;
        return index >= 0 && index < TrackData.Count ? TrackData[index] : null;
    }

    /// <summary>Opens an image by its extension: .dsk, .do, .po, .nib or .woz.</summary>
    public static AppleDisk Open(ReadOnlySpan<byte> image, string extension, bool writeProtected = false)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".woz":
                return FromWoz(image);
            case ".nib":
                return FromNib(image, writeProtected);
            case ".po":
                return FromSectors(image, proDosOrder: true, writeProtected);
            default:
                // .dsk is usually DOS order, but a ProDOS volume header in block 2 says otherwise.
                return FromSectors(image, proDosOrder: extension.Equals(".dsk", StringComparison.OrdinalIgnoreCase) && LooksProDos(image), writeProtected);
        }
    }

    /// <summary>140 KB sector images: each track is nibblized with the standard DOS 3.3 format, volume 254.</summary>
    public static AppleDisk FromSectors(ReadOnlySpan<byte> image, bool proDosOrder, bool writeProtected = false)
    {
        if (image.Length != DiskBytes)
        {
            throw new RomFormatException("A .dsk, .do or .po disk image is 143,360 bytes.");
        }

        var order = proDosOrder ? ProDosOrder : DosOrder;
        var tracks = new List<DiskTrack>(Tracks);
        for (var t = 0; t < Tracks; t++)
        {
            var writer = new BitWriter();
            writer.Sync(48); // gap 1
            for (var physical = 0; physical < SectorsPerTrack; physical++)
            {
                var data = image.Slice(t * TrackBytes + order[physical] * 256, 256);
                WriteSector(writer, 254, t, physical, data);
            }

            tracks.Add(writer.ToTrack());
        }

        return new AppleDisk(tracks, WholeTrackMap(Tracks), writeProtected);
    }

    /// <summary>Reads 16 sectors back from a nibblized track (tests and image export).</summary>
    public static byte[] ReadSectors(DiskTrack track, bool proDosOrder)
    {
        var nibbles = track.ToNibbles();
        var result = new byte[TrackBytes];
        var order = proDosOrder ? ProDosOrder : DosOrder;
        var found = 0;
        for (var i = 0; i < nibbles.Length && found < SectorsPerTrack; i++)
        {
            if (!Match(nibbles, i, 0xD5, 0xAA, 0x96))
            {
                continue;
            }

            var sector = Decode44(nibbles, i + 7);
            var dataStart = Find(nibbles, i + 14, 0xD5, 0xAA, 0xAD);
            if (dataStart < 0 || sector >= SectorsPerTrack)
            {
                continue;
            }

            Decode62(nibbles, dataStart + 3, result.AsSpan(order[sector] * 256, 256));
            found++;
        }

        return result;
    }

    private static void WriteSector(BitWriter writer, int volume, int track, int sector, ReadOnlySpan<byte> data)
    {
        // Address field
        writer.Bytes(0xD5, 0xAA, 0x96);
        Write44(writer, volume);
        Write44(writer, track);
        Write44(writer, sector);
        Write44(writer, volume ^ track ^ sector);
        writer.Bytes(0xDE, 0xAA, 0xEB);
        writer.Sync(6);

        // Data field: 6-and-2 (Beneath Apple DOS, figure 3.18)
        writer.Bytes(0xD5, 0xAA, 0xAD);
        Span<byte> buffer = stackalloc byte[342];
        for (var i = 0; i < 86; i++)
        {
            var value = 0;
            value |= Reverse2(data[i] & 3);
            value |= Reverse2(data[i + 86] & 3) << 2;
            if (i + 172 < 256)
            {
                value |= Reverse2(data[i + 172] & 3) << 4;
            }

            buffer[i] = (byte)value;
        }

        for (var i = 0; i < 256; i++)
        {
            buffer[86 + i] = (byte)(data[i] >> 2);
        }

        var previous = 0;
        for (var i = 0; i < 342; i++)
        {
            writer.Byte(Translate[buffer[i] ^ previous]);
            previous = buffer[i];
        }

        writer.Byte(Translate[previous]);
        writer.Bytes(0xDE, 0xAA, 0xEB);
        writer.Sync(27);
    }

    private static int Reverse2(int bits) => ((bits & 1) << 1) | ((bits >> 1) & 1);

    private static void Write44(BitWriter writer, int value)
    {
        writer.Byte((byte)((value >> 1) | 0xAA));
        writer.Byte((byte)(value | 0xAA));
    }

    private static int Decode44(byte[] nibbles, int at) => ((nibbles[at % nibbles.Length] << 1) | 1) & nibbles[(at + 1) % nibbles.Length];

    private static void Decode62(byte[] nibbles, int at, Span<byte> sector)
    {
        Span<byte> buffer = stackalloc byte[342];
        var previous = 0;
        for (var i = 0; i < 342; i++)
        {
            var value = Array.IndexOf(Translate, nibbles[(at + i) % nibbles.Length]);
            previous ^= value < 0 ? 0 : value;
            buffer[i] = (byte)previous;
        }

        for (var i = 0; i < 256; i++)
        {
            var low = buffer[i % 86] >> (i / 86 * 2);
            sector[i] = (byte)((buffer[86 + i] << 2) | Reverse2(low & 3));
        }
    }

    private static bool Match(byte[] nibbles, int at, byte a, byte b, byte c) =>
        nibbles[at % nibbles.Length] == a && nibbles[(at + 1) % nibbles.Length] == b && nibbles[(at + 2) % nibbles.Length] == c;

    private static int Find(byte[] nibbles, int from, byte a, byte b, byte c)
    {
        for (var i = from; i < from + 64; i++)
        {
            if (Match(nibbles, i, a, b, c))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LooksProDos(ReadOnlySpan<byte> image)
    {
        // ProDOS block 2 (the volume directory key block) sits at offset $400 in a ProDOS-order image:
        // a zero previous-block pointer and a volume header storage type ($F) with a 1–15 character name.
        if (image.Length != DiskBytes)
        {
            return false;
        }

        var header = image.Slice(0x400, 8);
        return header[0] == 0 && header[1] == 0 && (header[4] & 0xF0) == 0xF0 && (header[4] & 0x0F) is > 0 and <= 15;
    }

    private static AppleDisk FromNib(ReadOnlySpan<byte> image, bool writeProtected)
    {
        if (image.Length != Tracks * NibTrackBytes)
        {
            throw new RomFormatException("A .nib disk image is 232,960 bytes.");
        }

        var tracks = new List<DiskTrack>(Tracks);
        for (var t = 0; t < Tracks; t++)
        {
            var writer = new BitWriter();
            foreach (var b in image.Slice(t * NibTrackBytes, NibTrackBytes))
            {
                writer.Byte(b);
            }

            tracks.Add(writer.ToTrack());
        }

        return new AppleDisk(tracks, WholeTrackMap(Tracks), writeProtected);
    }

    /// <summary>WOZ 1 and 2 (applesaucefdc.com/woz/reference2): INFO, TMAP and TRKS chunks.</summary>
    private static AppleDisk FromWoz(ReadOnlySpan<byte> image)
    {
        if (image.Length < 12 || !(image.StartsWith("WOZ1"u8) || image.StartsWith("WOZ2"u8)) || image[4] != 0xFF)
        {
            throw new RomFormatException("That isn't a WOZ disk image.");
        }

        var version = image[3] == (byte)'2' ? 2 : 1;
        var writeProtected = false;
        int[]? map = null;
        var tracks = new List<DiskTrack>();
        var offset = 12;
        while (offset + 8 <= image.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(image.Slice(offset, 4));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 4, 4));
            var chunk = image.Slice(offset + 8, Math.Min(size, image.Length - offset - 8));
            switch (id)
            {
                case "INFO":
                    if (chunk[1] != 1)
                    {
                        throw new RomFormatException("GameVault reads 5.25-inch WOZ images; this one is for 3.5-inch disks.");
                    }

                    writeProtected = chunk[2] != 0;
                    break;
                case "TMAP":
                    map = new int[160];
                    for (var q = 0; q < 160; q++)
                    {
                        map[q] = chunk[q] == 0xFF ? -1 : chunk[q];
                    }

                    break;
                case "TRKS" when version == 1:
                    for (var t = 0; t + 6656 <= chunk.Length; t += 6656)
                    {
                        var bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(t + 6648, 2));
                        tracks.Add(new DiskTrack(chunk.Slice(t, (bits + 7) / 8).ToArray(), bits));
                    }

                    break;
                case "TRKS":
                    for (var t = 0; t < 160; t++)
                    {
                        var entry = chunk.Slice(t * 8, 8);
                        var startBlock = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                        var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
                        var bitCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                        if (startBlock == 0 || bitCount == 0)
                        {
                            tracks.Add(new DiskTrack(new byte[6250], 50000));
                            continue;
                        }

                        var start = startBlock * 512;
                        var length = Math.Min(blockCount * 512, image.Length - start);
                        tracks.Add(new DiskTrack(image.Slice(start, Math.Max(0, length)).ToArray(), bitCount));
                    }

                    break;
            }

            offset += 8 + size;
        }

        if (map is null || tracks.Count == 0)
        {
            throw new RomFormatException("The WOZ image is missing its track map or tracks.");
        }

        return new AppleDisk(tracks, map, writeProtected);
    }

    /// <summary>Whole tracks only: track t sits at quarter-track 4t and is still read one quarter-track either side.</summary>
    private static int[] WholeTrackMap(int tracks)
    {
        var map = new int[160];
        Array.Fill(map, -1);
        for (var t = 0; t < tracks; t++)
        {
            for (var q = Math.Max(0, t * 4 - 1); q <= Math.Min(159, t * 4 + 1); q++)
            {
                map[q] = t;
            }
        }

        return map;
    }

    /// <summary>Builds a track bit by bit; sync bytes are $FF followed by two 0 bits (10-bit self-sync).</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Bit(bool one)
        {
            if (_bits % 8 == 0)
            {
                _bytes.Add(0);
            }

            if (one)
            {
                _bytes[^1] |= (byte)(0x80 >> (_bits % 8));
            }

            _bits++;
        }

        public void Byte(byte value)
        {
            for (var i = 7; i >= 0; i--)
            {
                Bit(((value >> i) & 1) != 0);
            }
        }

        public void Bytes(params byte[] values)
        {
            foreach (var value in values)
            {
                Byte(value);
            }
        }

        public void Sync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Byte(0xFF);
                Bit(false);
                Bit(false);
            }
        }

        public DiskTrack ToTrack() => new([.. _bytes], _bits);
    }
}

/// <summary>One track's bits, most significant bit first, as they pass under the head.</summary>
public sealed class DiskTrack(byte[] bits, int bitCount)
{
    public byte[] Bits { get; } = bits;

    public int BitCount { get; } = Math.Max(1, bitCount);

    public bool this[int index]
    {
        get => (Bits[index >> 3] & (0x80 >> (index & 7))) != 0;
        set
        {
            if (value)
            {
                Bits[index >> 3] |= (byte)(0x80 >> (index & 7));
            }
            else
            {
                Bits[index >> 3] &= (byte)~(0x80 >> (index & 7));
            }
        }
    }

    /// <summary>The disk bytes a controller would read in one revolution (for tests and decoding).</summary>
    public byte[] ToNibbles()
    {
        var result = new List<byte>();
        var latch = 0;
        for (var i = 0; i < BitCount * 2 && result.Count < BitCount / 8 + 1; i++)
        {
            latch = (latch << 1) | (this[i % BitCount] ? 1 : 0);
            if ((latch & 0x80) != 0)
            {
                if (i >= BitCount)
                {
                    result.Add((byte)latch);
                }

                latch = 0;
            }
        }

        return [.. result];
    }
}
