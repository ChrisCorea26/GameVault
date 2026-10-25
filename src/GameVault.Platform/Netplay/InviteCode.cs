using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameVault.Platform.Netplay;

/// <summary>
/// LAN invite codes (SPEC §7.7, LAN-only for now): the host's IPv4 address and UDP port in 10 characters
/// of Crockford base 32, shown as <c>ABCDE-FGHJK</c>. Typing is forgiving: case, spaces and dashes are
/// ignored, and O/I/L read as 0/1/1.
/// </summary>
public static class InviteCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(IPEndPoint endPoint)
    {
        var address = endPoint.Address.MapToIPv4().GetAddressBytes();
        var value = ((ulong)address[0] << 40) | ((ulong)address[1] << 32) | ((ulong)address[2] << 24) | ((ulong)address[3] << 16) | (ulong)(ushort)endPoint.Port;
        var chars = new char[10];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    public static bool TryDecode(string? text, out IPEndPoint endPoint)
    {
        endPoint = null!;
        var cleaned = (text ?? string.Empty).ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray();
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

        if (value >> 48 != 0)
        {
            return false;
        }

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
