using System.Buffers.Binary;

namespace GameVault.Platform.Netplay;

/// <summary>
/// The wire format of <see cref="NetplayPacket"/> (version 3, little-endian):
/// <c>version, flags (1 = hello, 2 = checksum, 4 = hello seen), [rom hash u64, input delay u8, protocol u16],
/// first frame i32, input count u8, inputs u32 each, ack frame i32, [checksum frame i32, value u32]</c>.
/// Version 2 widened the inputs to 16 bits for the Genesis pad (Phase 13), version 3 to 32 bits for the N64's
/// analog stick (Phase 16).
/// </summary>
public static class NetplayCodec
{
    private const byte Version = 3;
    private const int MaxInputs = 64;

    public static byte[] Encode(NetplayPacket packet)
    {
        var count = Math.Min(packet.Inputs.Length, MaxInputs);
        var size = 2 + (packet.Hello is null ? 0 : 11) + 5 + count * 4 + 4 + (packet.Checksum is null ? 0 : 8);
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
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 5 + i * 4)..], packet.Inputs[i]);
        }

        at += 5 + count * 4;
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
        if (count > MaxInputs || data.Length < at + count * 4 + 4)
        {
            return false;
        }

        var inputs = new uint[count];
        for (var i = 0; i < count; i++)
        {
            inputs[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + i * 4)..]);
        }

        at += count * 4;
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
