using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameVault.Platform.Netplay;

/// <summary>How an invite code connects.</summary>
public enum InviteKind
{
    /// <summary>The host on the same network: the joiner sends to it and it answers whoever does.</summary>
    Lan,

    /// <summary>The host's public address over the internet: the joiner sends a reply code back so the host can reach it.</summary>
    Internet,

    /// <summary>A room on a relay server, which passes the packets between the two players.</summary>
    Relay,
}

/// <summary>
/// Invite codes (SPEC §7.7, outstanding-work plan §13): the host's IPv4 address and UDP port in 10 characters of
/// Crockford base 32, shown as <c>ABCDE-FGHJK</c> (bit 48 set for an internet host's public address), or a relay's
/// address, port and room number in 16 characters, <c>ABCD-EFGH-JKMN-PQRS</c>. Typing is forgiving: case, spaces and
/// dashes are ignored, and O/I/L read as 0/1/1.
/// </summary>
public static class InviteCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const ulong InternetFlag = 1UL << 48;

    public static string Encode(IPEndPoint endPoint, bool internet = false)
    {
        var address = endPoint.Address.MapToIPv4().GetAddressBytes();
        var value = ((ulong)address[0] << 40) | ((ulong)address[1] << 32) | ((ulong)address[2] << 24) | ((ulong)address[3] << 16) | (ulong)(ushort)endPoint.Port;
        value |= internet ? InternetFlag : 0;
        var chars = new char[10];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    /// <summary>A relay's address and port, and the room, in 16 characters.</summary>
    public static string EncodeRelay(IPEndPoint relay, uint room)
    {
        var address = relay.Address.MapToIPv4().GetAddressBytes();
        var high = ((ulong)address[0] << 24) | ((ulong)address[1] << 16) | ((ulong)address[2] << 8) | address[3];
        var value = (UInt128)high << 48 | (UInt128)(ushort)relay.Port << 32 | room;
        var chars = new char[16];
        for (var i = 15; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}-{new string(chars, 12, 4)}";
    }

    /// <summary>Reads any invite code: a host's (LAN or internet) or a relay room's.</summary>
    public static bool TryDecode(string? text, out IPEndPoint endPoint, out InviteKind kind, out uint room)
    {
        room = 0;
        kind = InviteKind.Lan;
        var cleaned = Clean(text);
        if (cleaned.Length == 16)
        {
            endPoint = null!;
            kind = InviteKind.Relay;
            UInt128 big = 0;
            foreach (var c in cleaned)
            {
                var digit = Alphabet.IndexOf(c);
                if (digit < 0)
                {
                    return false;
                }

                big = (big << 5) | (uint)digit;
            }

            var port = (int)(ushort)(big >> 32);
            var high = (uint)(big >> 48);
            if (port == 0 || big >> 80 != 0)
            {
                return false;
            }

            room = (uint)big;
            endPoint = new IPEndPoint(new IPAddress([(byte)(high >> 24), (byte)(high >> 16), (byte)(high >> 8), (byte)high]), port);
            return true;
        }

        if (!TryDecode(text, out endPoint, out var internet))
        {
            return false;
        }

        kind = internet ? InviteKind.Internet : InviteKind.Lan;
        return true;
    }

    public static bool TryDecode(string? text, out IPEndPoint endPoint) => TryDecode(text, out endPoint, out bool _);

    private static char[] Clean(string? text) =>
        (text ?? string.Empty).ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray();

    public static bool TryDecode(string? text, out IPEndPoint endPoint, out bool internet)
    {
        endPoint = null!;
        internet = false;
        var cleaned = Clean(text);
        if (cleaned.Length != 10)
        {
            return false;
        }

        ulong value = 0;
        foreach (var c in cleaned)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
            {
                return false;
            }

            value = (value << 5) | (uint)digit;
        }

        if (value >> 49 != 0)
        {
            return false;
        }

        internet = (value & InternetFlag) != 0;
        value &= InternetFlag - 1;

        var address = new IPAddress([(byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16)]);
        var port = (int)(value & 0xFFFF);
        if (port == 0)
        {
            return false;
        }

        endPoint = new IPEndPoint(address, port);
        return true;
    }

    /// <summary>This PC's address on the local network: a private IPv4 address of a working adapter, if any.</summary>
    public static IPAddress? LocalNetworkAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToList();
        return candidates.FirstOrDefault(IsPrivate) ?? candidates.FirstOrDefault();
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
