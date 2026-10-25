namespace GameVault.Core.Cartridge.Fds;

/// <summary>
/// The disk as the drive sees it: each side becomes a track of bytes with the lead-in gap, a gap before
/// every block ending in the $80 "1 bit", the block data, and a CRC-16/KERMIT (NESdev Wiki: "FDS disk
/// format"). The drive reads and writes these tracks a byte at a time; writes never touch the image.
/// </summary>
public static class FdsDisk
{
    /// <summary>Bytes per track: about 7 seconds per pass at 149 CPU cycles a byte, like a real side.</summary>
    public const int TrackSize = 76000;

    /// <summary>"At least 26150 bits, 28300 typical."</summary>
    public const int LeadInGap = 28300 / 8;

    /// <summary>"At least 480 bits, 976 typical."</summary>
    public const int BlockGap = 976 / 8;

    public static byte[] BuildTrack(ReadOnlySpan<byte> side)
    {
        var track = new byte[TrackSize];
        var position = LeadInGap;
        var offset = 0;
        var expected = 1;
        while (offset < side.Length)
        {
            var code = side[offset];
            var length = BlockLength(side, offset, expected);
            if (length == 0 || offset + length > side.Length || position + length + 3 + BlockGap > TrackSize)
            {
                break;
            }

            track[position] = 0x80;
            side.Slice(offset, length).CopyTo(track.AsSpan(position + 1));
            var crc = Crc(track.AsSpan(position, length + 1));
            track[position + 1 + length] = (byte)crc;
            track[position + 2 + length] = (byte)(crc >> 8);
            position += length + 3 + BlockGap;
            offset += length;
            expected = code switch { 1 => 2, 2 or 4 => 3, _ => 4 };
        }

        return track;
    }

    /// <summary>
    /// Block sizes: 1 (disk info) 56 bytes, 2 (file amount) 2, 3 (file header) 16, 4 (file data) 1 + the size
    /// from the preceding header. Anything else ends the side's data.
    /// </summary>
    private static int BlockLength(ReadOnlySpan<byte> side, int offset, int expected)
    {
        var code = side[offset];
        if (code != expected)
        {
            return 0;
        }

        return code switch
        {
            1 => 56,
            2 => 2,
            3 => 16,
            4 when offset >= 16 => 1 + (side[offset - 16 + 13] | (side[offset - 16 + 14] << 8)),
            _ => 0,
        };
    }

    /// <summary>CRC-16/KERMIT: reflected polynomial $8408, initial value 0, stored low byte first.</summary>
    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var b in data)
        {
            crc = Update(crc, b);
        }

        return (ushort)crc;
    }

    public static int Update(int crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
        }

        return crc;
    }
}

/// <summary>
/// Disk writes saved apart from the image (Phase 7 plan): a list of changed byte runs per track, so the
/// player's image file is never modified. Format: per run, side (1 byte), offset (4), length (4), bytes.
/// </summary>
public static class FdsDiskPatch
{
    public static byte[] Create(IReadOnlyList<byte[]> original, IReadOnlyList<byte[]> current)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GVFP"u8);
        for (var side = 0; side < current.Count; side++)
        {
            var a = original[side];
            var b = current[side];
            var i = 0;
            while (i < b.Length)
            {
                if (a[i] == b[i])
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < b.Length && a[i] != b[i])
                {
                    i++;
                }

                writer.Write((byte)side);
                writer.Write(start);
                writer.Write(i - start);
                writer.Write(b, start, i - start);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Applies a patch to freshly built tracks. A patch for another disk is rejected as a whole.</summary>
    public static void Apply(ReadOnlySpan<byte> patch, IReadOnlyList<byte[]> tracks)
    {
        if (patch.Length < 4 || !patch[..4].SequenceEqual("GVFP"u8))
        {
            throw new InvalidDataException("This isn't a GameVault disk save.");
        }

        var runs = new List<(int Side, int Offset, int Start, int Length)>();
        var position = 4;
        while (position < patch.Length)
        {
            if (position + 9 > patch.Length)
            {
                throw new InvalidDataException("The disk save is cut short.");
            }

            var side = patch[position];
            var offset = BitConverter.ToInt32(patch.Slice(position + 1, 4));
            var length = BitConverter.ToInt32(patch.Slice(position + 5, 4));
            position += 9;
            if (side >= tracks.Count || offset < 0 || length < 0 || offset + length > tracks[side].Length || position + length > patch.Length)
            {
                throw new InvalidDataException("The disk save doesn't match this disk.");
            }

            runs.Add((side, offset, position, length));
            position += length;
        }

        foreach (var run in runs)
        {
            patch.Slice(run.Start, run.Length).CopyTo(tracks[run.Side].AsSpan(run.Offset));
        }
    }
}
