using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace GameVault.Platform.Netplay;

/// <summary>
/// STUN Binding requests and responses (RFC 5389), just enough to learn the public address and port a router gives
/// the netplay socket (outstanding-work plan §13).
/// </summary>
public static class StunMessage
{
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort MappedAddress = 0x0001;
    private const ushort XorMappedAddress = 0x0020;
    private const uint MagicCookie = 0x2112A442;

    /// <summary>Public STUN servers asked in turn (only when the player hosts or joins over the internet).</summary>
    public static IReadOnlyList<(string Host, int Port)> PublicServers { get; } =
    [
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    ];

    /// <summary>A Binding request with a fresh 96-bit transaction ID.</summary>
    public static byte[] Request(out byte[] transaction)
    {
        transaction = RandomNumberGenerator.GetBytes(12);
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet, BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        transaction.CopyTo(packet, 8);
        return packet;
    }

    /// <summary>True for any STUN message (so netplay can tell them from its own packets).</summary>
    public static bool IsStun(ReadOnlySpan<byte> packet) =>
        packet.Length >= 20 && (packet[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) == MagicCookie;

    /// <summary>The mapped address in a Binding success response to <paramref name="transaction"/>, if this is one.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> transaction, out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (!IsStun(packet) || BinaryPrimitives.ReadUInt16BigEndian(packet) != BindingSuccess || !packet.Slice(8, 12).SequenceEqual(transaction))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var attributes = packet.Slice(20, Math.Min(length, packet.Length - 20));
        IPEndPoint? plain = null;
        while (attributes.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(attributes);
            var size = BinaryPrimitives.ReadUInt16BigEndian(attributes[2..]);
            if (4 + size > attributes.Length)
            {
                break;
            }

            var value = attributes.Slice(4, size);
            if (size >= 8 && value[1] == 0x01) // IPv4
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                var address = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                if (type == XorMappedAddress)
                {
                    endPoint = new IPEndPoint(new IPAddress(BigEndian(address ^ MagicCookie)), port ^ (int)(MagicCookie >> 16));
                    return true;
                }

                if (type == MappedAddress)
                {
                    plain = new IPEndPoint(new IPAddress(BigEndian(address)), port);
                }
            }

            attributes = attributes[(4 + ((size + 3) & ~3))..];
        }

        if (plain is not null)
        {
            endPoint = plain;
            return true;
        }

        return false;
    }

    /// <summary>A Binding success response telling <paramref name="client"/> its address (tests' stand-in server).</summary>
    public static byte[] Response(ReadOnlySpan<byte> request, IPEndPoint client)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet, BindingSuccess);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 12);
        request.Slice(4, 16).CopyTo(packet.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), XorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 8);
        packet[25] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), (ushort)(client.Port ^ (int)(MagicCookie >> 16)));
        var address = BinaryPrimitives.ReadUInt32BigEndian(client.Address.MapToIPv4().GetAddressBytes());
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28), address ^ MagicCookie);
        return packet;
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
