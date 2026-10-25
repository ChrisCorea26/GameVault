using System.Buffers.Binary;

namespace GameVault.Platform.Netplay;

/// <summary>
/// The wire format of <see cref="NetplayPacket"/> (version 1, little-endian):
/// <c>version, flags (1 = hello, 2 = checksum, 4 = hello seen), [rom hash u64, input delay u8, protocol u16],
/// first frame i32, input count u8, inputs, ack frame i32, [checksum frame i32, value u32]</c>.
/// </summary>
public static class NetplayCodec
{
    private const byte Version = 1;
    private const int MaxInputs = 64;

    public static byte[] Encode(NetplayPacket packet)
    {
        var count = Math.Min(packet.Inputs.Length, MaxInputs);
        var size = 2 + (packet.Hello is null ? 0 : 11) + 5 + count + 4 + (packet.Checksum is null ? 0 : 8);
        var data = new byte[size];
        var span = data.AsSpan();
        span[0] = Version;
        span[1] = (byte)((packet.Hello is null ? 0 : 1) | (packet.Checksum is null ? 0 : 2) | (packet.HelloSeen ? 4 : 0));
        var at = 2;
        if (packet.Hello is { } hello)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], hello.RomHash);
            span[at + 8] = (byte)hello.InputDelay;
            BinaryPrimitives.WriteUInt16LittleEndian(span[(at + 9)..], (ushort)hello.ProtocolVersion);
            at += 11;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[at..], packet.FirstFrame);
        span[at + 4] = (byte)count;
        packet.Inputs.AsSpan(0, count).CopyTo(span[(at + 5)..]);
        at += 5 + count;
        BinaryPrimitives.WriteInt32LittleEndian(span[at..], packet.AckFrame);
        at += 4;
        if (packet.Checksum is { } checksum)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], checksum.Frame);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], checksum.Value);
        }

        return data;
    }

    /// <summary>Reads a packet; anything malformed or from another version is ignored (false).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out NetplayPacket packet)
    {
        packet = null!;
        if (data.Length < 2 || data[0] != Version)
        {
            return false;
        }

        var flags = data[1];
        var at = 2;
        HelloMessage? hello = null;
        if ((flags & 1) != 0)
        {
            if (data.Length < at + 11)
            {
                return false;
            }

            hello = new HelloMessage(
                BinaryPrimitives.ReadUInt64LittleEndian(data[at..]),
                data[at + 8],
                BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 9)..]));
            at += 11;
        }

        if (data.Length < at + 5)
        {
            return false;
        }

        var first = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        var count = data[at + 4];
        at += 5;
        if (count > MaxInputs || data.Length < at + count + 4)
        {
            return false;
        }

        var inputs = data.Slice(at, count).ToArray();
        at += count;
        var ack = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += 4;
        FrameChecksum? checksum = null;
        if ((flags & 2) != 0)
        {
            if (data.Length < at + 8)
            {
                return false;
            }

            checksum = new FrameChecksum(BinaryPrimitives.ReadInt32LittleEndian(data[at..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]));
        }

        packet = new NetplayPacket { Hello = hello, HelloSeen = (flags & 4) != 0, FirstFrame = first, Inputs = inputs, AckFrame = ack, Checksum = checksum };
        return true;
    }
}
