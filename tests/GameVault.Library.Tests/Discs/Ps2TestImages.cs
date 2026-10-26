using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GameVault.Library.Discs;

namespace GameVault.Library.Tests.Discs;

/// <summary>
/// Builds small PlayStation 2 disc images for tests: an ISO 9660 volume of 2,048-byte sectors with SYSTEM.CNF, a boot
/// ELF and other files (one level of subdirectory allowed), as an .iso, raw 2,352-byte sectors (.bin/.cue) or a DVD CHD.
/// </summary>
internal static class Ps2TestImages
{
    public const string Serial = "SLUS_200.62";

    public static string SystemCnf(string boot2 = @"cdrom0:\SLUS_200.62;1", string vmode = "NTSC") =>
        $"BOOT2 = {boot2}\r\nVER = 1.00\r\nVMODE = {vmode}\r\n";

    /// <summary>A tiny little-endian MIPS ELF header followed by <paramref name="body"/> bytes of a pattern.</summary>
    public static byte[] Elf(int body = 3000)
    {
        var elf = new byte[52 + body];
        "\u007FELF"u8.CopyTo(elf);
        elf[4] = 1; // 32-bit
        elf[5] = 1; // little-endian
        elf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(16), 2); // executable
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 8); // MIPS
        for (var i = 52; i < elf.Length; i++)
        {
            elf[i] = (byte)(i * 7 + 3);
        }

        return elf;
    }

    /// <summary>
    /// An ISO of 2,048-byte sectors. <paramref name="files"/> maps names (optionally "DIR\NAME") to contents; SYSTEM.CNF
    /// and the boot ELF are added unless <paramref name="systemCnf"/> is null. <paramref name="padSectors"/> zero sectors
    /// follow the files.
    /// </summary>
    public static byte[] Iso(string? systemCnf = null, Dictionary<string, byte[]>? files = null, int padSectors = 0, bool ps2 = true)
    {
        var all = new List<(string Path, byte[] Data)>();
        if (ps2)
        {
            all.Add(("SYSTEM.CNF", Encoding.ASCII.GetBytes(systemCnf ?? SystemCnf())));
            all.Add((Serial, Elf()));
        }
        else if (systemCnf is not null)
        {
            all.Add(("SYSTEM.CNF", Encoding.ASCII.GetBytes(systemCnf)));
        }

        if (files is not null)
        {
            all.AddRange(files.Select(f => (f.Key, f.Value)));
        }

        // Layout: 0–15 system area, 16 PVD, 17 terminator, 18 root directory, 19 the subdirectory (if any), then files.
        var sectors = new List<byte[]>();
        for (var i = 0; i < 20; i++)
        {
            sectors.Add(new byte[2048]);
        }

        var root = new List<byte[]> { Record("\0", 18, 2048, true), Record("\u0001", 18, 2048, true) };
        var sub = new List<byte[]> { Record("\0", 19, 2048, true), Record("\u0001", 18, 2048, true) };
        string? subName = null;
        foreach (var (path, data) in all)
        {
            var lba = sectors.Count;
            for (var offset = 0; offset < Math.Max(1, data.Length); offset += 2048)
            {
                var sector = new byte[2048];
                data.AsSpan(offset, Math.Min(2048, data.Length - offset)).CopyTo(sector);
                sectors.Add(sector);
            }

            var slash = path.IndexOf('\\');
            if (slash < 0)
            {
                root.Add(Record(path + ";1", lba, data.Length, false));
            }
            else
            {
                subName = path[..slash];
                sub.Add(Record(path[(slash + 1)..] + ";1", lba, data.Length, false));
            }
        }

        if (subName is not null)
        {
            root.Add(Record(subName, 19, 2048, true));
            Fill(sectors[19], sub);
        }

        Fill(sectors[18], root);
        for (var i = 0; i < padSectors; i++)
        {
            sectors.Add(new byte[2048]);
        }

        var pvd = sectors[16];
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd.AsSpan(1));
        pvd[6] = 1;
        Encoding.ASCII.GetBytes("PLAYSTATION                     ").CopyTo(pvd, 8);
        BinaryPrimitives.WriteInt32LittleEndian(pvd.AsSpan(80), sectors.Count);
        BinaryPrimitives.WriteInt32BigEndian(pvd.AsSpan(84), sectors.Count);
        BinaryPrimitives.WriteInt16LittleEndian(pvd.AsSpan(128), 2048);
        Record("\0", 18, 2048, true).CopyTo(pvd, 156);
        sectors[17][0] = 0xFF;
        "CD001"u8.CopyTo(sectors[17].AsSpan(1));

        return sectors.SelectMany(s => s).ToArray();
    }

    /// <summary>A dual-layer image: <paramref name="layer0"/>, then a second layer with its own volume descriptor at its sector 16.</summary>
    public static byte[] DualLayer(byte[] layer0, int layer1Sectors = 40)
    {
        var layer1 = new byte[layer1Sectors * 2048];
        layer1[16 * 2048] = 1;
        "CD001"u8.CopyTo(layer1.AsSpan(16 * 2048 + 1));
        return [.. layer0, .. layer1];
    }

    /// <summary>The image's sectors as raw mode 2 form 1 sectors, from disc address 150.</summary>
    public static byte[] Raw(byte[] iso)
    {
        var count = iso.Length / 2048;
        var raw = new byte[count * 2352];
        for (var i = 0; i < count; i++)
        {
            CdSector.Build(iso.AsSpan(i * 2048, 2048), 150 + i, mode2: true, raw.AsSpan(i * 2352, 2352));
        }

        return raw;
    }

    public static string Cue(string bin) => $"FILE \"{bin}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n";

    private static byte[] Record(string name, int lba, int size, bool directory)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var length = 33 + nameBytes.Length + (nameBytes.Length % 2 == 0 ? 1 : 0);
        var record = new byte[length];
        record[0] = (byte)length;
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(2), lba);
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(6), lba);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(10), size);
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(14), size);
        record[25] = directory ? (byte)2 : (byte)0;
        record[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(record, 33);
        return record;
    }

    private static void Fill(byte[] sector, List<byte[]> records)
    {
        var position = 0;
        foreach (var record in records)
        {
            record.CopyTo(sector, position);
            position += record.Length;
        }
    }

    // ---- DVD CHDs ----

    /// <summary>How one hunk is stored: compressed by codec 0–3 (with its bytes), uncompressed (4) or as a copy of an earlier hunk (5).</summary>
    public readonly record struct Hunk(int Type, byte[] Payload, int CopyOf = 0);

    /// <summary>
    /// A CHD v5 holding <paramref name="image"/> as a DVD (2,048-byte units, a "DVD " metadata entry), with the given
    /// codec tags and a compressed hunk map. <paramref name="store"/> says how each hunk is stored.
    /// </summary>
    public static byte[] DvdChd(byte[] image, int hunkBytes, string[] codecs, Func<int, byte[], Hunk> store)
    {
        var hunkCount = (image.Length + hunkBytes - 1) / hunkBytes;
        var hunks = new List<(Hunk Hunk, ushort Crc)>();
        for (var n = 0; n < hunkCount; n++)
        {
            var data = new byte[hunkBytes];
            image.AsSpan(n * hunkBytes, Math.Min(hunkBytes, image.Length - n * hunkBytes)).CopyTo(data);
            hunks.Add((store(n, data), ChdFile.Crc16(data)));
        }

        var file = new MemoryStream();
        var header = new byte[124];
        "MComprHD"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 124);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 5);
        for (var i = 0; i < codecs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16 + i * 4), BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(codecs[i])));
        }

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32), image.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), (uint)hunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(60), 2048);
        file.Write(header);

        // The metadata: one empty "DVD " entry.
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(48), file.Position);
        var meta = new byte[16];
        "DVD "u8.CopyTo(meta);
        BinaryPrimitives.WriteUInt32BigEndian(meta.AsSpan(4), 0x01 << 24);
        file.Write(meta);

        // The map: types through a Huffman tree where every one of the 16 symbols has a 4-bit code (so the code is the
        // symbol), then each hunk's length/offset and CRC.
        var mapOffset = file.Position;
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(40), mapOffset);
        const int lengthBits = 24, selfBits = 16;
        var bits = new BitWriter();
        for (var i = 0; i < 16; i++)
        {
            bits.Write(4, 4);
        }

        foreach (var (hunk, _) in hunks)
        {
            bits.Write((uint)hunk.Type, 4);
        }

        var rawMap = new byte[hunkCount * 12];
        var mapBits = bits;
        long dataOffset = 0; // relative to the first hunk; fixed up below
        var offsets = new long[hunkCount];
        for (var n = 0; n < hunkCount; n++)
        {
            var (hunk, crc) = hunks[n];
            long offset;
            int length = 0;
            ushort storedCrc = 0;
            switch (hunk.Type)
            {
                case <= 4:
                    length = hunk.Payload.Length;
                    offset = dataOffset;
                    offsets[n] = dataOffset;
                    dataOffset += length;
                    storedCrc = crc;
                    if (hunk.Type < 4)
                    {
                        mapBits.Write((uint)length, lengthBits);
                    }

                    mapBits.Write(crc, 16);
                    break;
                default:
                    offset = hunk.CopyOf;
                    mapBits.Write((uint)hunk.CopyOf, selfBits);
                    break;
            }

            rawMap[n * 12] = (byte)hunk.Type;
            rawMap[n * 12 + 1] = (byte)(length >> 16);
            rawMap[n * 12 + 2] = (byte)(length >> 8);
            rawMap[n * 12 + 3] = (byte)length;
            offsets[n] = offset;
            rawMap[n * 12 + 10] = (byte)(storedCrc >> 8);
            rawMap[n * 12 + 11] = (byte)storedCrc;
        }

        var compressedMap = mapBits.ToArray();
        var firstOffset = mapOffset + 16 + compressedMap.Length;
        for (var n = 0; n < hunkCount; n++)
        {
            var offset = hunks[n].Hunk.Type <= 4 ? offsets[n] + firstOffset : offsets[n];
            for (var b = 0; b < 6; b++)
            {
                rawMap[n * 12 + 4 + b] = (byte)(offset >> (40 - 8 * b));
            }
        }

        var mapHeader = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mapHeader, (uint)compressedMap.Length);
        BinaryPrimitives.WriteUInt16BigEndian(mapHeader.AsSpan(4), (ushort)(firstOffset >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(mapHeader.AsSpan(6), (uint)firstOffset);
        BinaryPrimitives.WriteUInt16BigEndian(mapHeader.AsSpan(10), ChdFile.Crc16(rawMap));
        mapHeader[12] = lengthBits;
        mapHeader[13] = selfBits;
        file.Write(mapHeader);
        file.Write(compressedMap);
        foreach (var (hunk, _) in hunks)
        {
            if (hunk.Type <= 4)
            {
                file.Write(hunk.Payload);
            }
        }

        var bytes = file.ToArray();
        header.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>
    /// CHD's "huff" codec with every byte value coded in 8 bits: the small tree gives symbols 0 (repeat) and 9 (length
    /// 8) one bit each; the code lengths are "length 8", then "repeat it 255 times"; then the bytes as they are.
    /// </summary>
    public static byte[] Huff(byte[] data)
    {
        var bits = new BitWriter();
        bits.Write(1, 3); // symbol 0: length 1
        bits.Write(7, 3); // the next lengths start at symbol 8
        bits.Write(0, 3); // symbol 8: none
        bits.Write(1, 3); // symbol 9: length 1
        bits.Write(7, 3); // symbol 10: none, and so the rest
        bits.Write(1, 1); // symbol 9 (code 1): code 0 has length 8
        bits.Write(0, 1); // symbol 0 (code 0): repeat
        bits.Write(7, 3); // 7 + 2 ...
        bits.Write(246, 8); // ... + 246 = 255 more
        foreach (var b in data)
        {
            bits.Write(b, 8);
        }

        return bits.ToArray();
    }

    /// <summary>CHD's generic "flac" codec: 'L' (samples taken little-endian), then FLAC frames.</summary>
    public static byte[] Flac(byte[] frames) => [(byte)'L', .. frames];

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;

        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (_bit % 8 == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_bit % 8));
                }

                _bit++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
