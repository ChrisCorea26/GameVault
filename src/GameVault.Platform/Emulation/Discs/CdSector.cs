using System.Buffers.Binary;

namespace GameVault.Platform.Emulation.Discs;

/// <summary>
/// Raw CD sector helpers (ECMA-130): the sync pattern, MSF headers, the EDC checksum and the P/Q error-correction
/// codes. Used to rebuild full 2,352-byte sectors from images that store only the user data, and to regenerate the
/// ECC that CHD files strip.
/// </summary>
public static class CdSector
{
    public const int RawSize = 2352;

    private const int EccPOffset = 0x81C;
    private const int EccQOffset = 0x8C8;

    /// <summary>00 FF×10 00, the start of every data sector.</summary>
    public static ReadOnlySpan<byte> Sync => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private static readonly uint[] EdcTable = BuildEdcTable();
    private static readonly byte[] EccForward = new byte[256];
    private static readonly byte[] EccBackward = new byte[256];

    static CdSector()
    {
        // GF(2^8) with the polynomial x^8 + x^4 + x^3 + x^2 + 1: "forward" multiplies by 2, "backward" divides by 3.
        for (var i = 0; i < 256; i++)
        {
            var doubled = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            EccForward[i] = (byte)doubled;
            EccBackward[i ^ doubled] = (byte)i;
        }
    }

    /// <summary>Writes the MSF address of disc address <paramref name="lba"/> in BCD, as a sector header holds it.</summary>
    public static void WriteMsf(Span<byte> destination, int lba)
    {
        destination[0] = ToBcd(lba / 4500);
        destination[1] = ToBcd(lba / 75 % 60);
        destination[2] = ToBcd(lba % 75);
    }

    private static byte ToBcd(int value) => (byte)((value / 10 % 10) << 4 | value % 10);

    /// <summary>
    /// Builds a raw sector at disc address <paramref name="lba"/> from <paramref name="data"/>, which is the part of
    /// the sector an image stores for its track type: 2,352 bytes (copied), 2,048 (mode 1 user data, or mode 2 form 1
    /// when <paramref name="mode2"/> is set), 2,336 (mode 2: subheader onwards) or 2,324 (mode 2 form 2 user data).
    /// </summary>
    public static void Build(ReadOnlySpan<byte> data, int lba, bool mode2, Span<byte> sector)
    {
        sector = sector[..RawSize];
        if (data.Length == RawSize)
        {
            data.CopyTo(sector);
            return;
        }

        sector.Clear();
        Sync.CopyTo(sector);
        WriteMsf(sector[12..], lba);
        switch (data.Length)
        {
            case 2048 when !mode2:
                sector[15] = 1;
                data.CopyTo(sector[16..]);
                BinaryPrimitives.WriteUInt32LittleEndian(sector[0x810..], Edc(sector[..0x810]));
                GenerateEcc(sector);
                break;
            case 2336:
                sector[15] = 2;
                data.CopyTo(sector[16..]);
                break;
            case 2048:
                // Mode 2 form 1: a subheader marking data, then the user data, EDC and ECC (computed with a zero header).
                sector[15] = 2;
                sector[18] = sector[22] = 0x08;
                data.CopyTo(sector[24..]);
                BinaryPrimitives.WriteUInt32LittleEndian(sector[0x818..], Edc(sector[16..0x818]));
                GenerateMode2Ecc(sector);
                break;
            case 2324:
                sector[15] = 2;
                sector[18] = sector[22] = 0x20;
                data.CopyTo(sector[24..]);
                BinaryPrimitives.WriteUInt32LittleEndian(sector[0x92C..], Edc(sector[16..0x92C]));
                break;
            default:
                throw new ArgumentException($"Unsupported sector data size {data.Length}.", nameof(data));
        }
    }

    /// <summary>The EDC: a CRC-32 with the polynomial x^32 + x^31 + x^16 + x^15 + x^4 + x^3 + x + 1 (reflected).</summary>
    public static uint Edc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        foreach (var b in data)
        {
            edc = (edc >> 8) ^ EdcTable[(edc ^ b) & 0xFF];
        }

        return edc;
    }

    private static uint[] BuildEdcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var edc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0);
            }

            table[i] = edc;
        }

        return table;
    }

    /// <summary>
    /// Computes the P and Q parity of a mode 1 sector (or any sector, over its bytes as they stand). The 2,064 bytes
    /// from the header on are two planes (even and odd bytes) of 24×43 words; P covers each plane's 43 columns of 24,
    /// then Q each plane's 26 diagonals of 43 across the data and P (ECMA-130 Annex A).
    /// </summary>
    public static void GenerateEcc(Span<byte> sector)
    {
        for (var b = 0; b < 86; b++)
        {
            byte p1 = 0, p2 = 0;
            for (var i = 0; i < 24; i++)
            {
                Accumulate(sector[12 + b + 86 * i], ref p1, ref p2);
            }

            Finish(ref p1, ref p2);
            sector[EccPOffset + b] = p1;
            sector[EccPOffset + 86 + b] = p2;
        }

        for (var b = 0; b < 52; b++)
        {
            var plane = b & 1;
            var diagonal = b >> 1;
            byte q1 = 0, q2 = 0;
            for (var j = 0; j < 43; j++)
            {
                var word = (43 * diagonal + 44 * j) % 1118;
                Accumulate(sector[12 + 2 * word + plane], ref q1, ref q2);
            }

            Finish(ref q1, ref q2);
            sector[EccQOffset + b] = q1;
            sector[EccQOffset + 52 + b] = q2;
        }
    }

    /// <summary>Mode 2 form 1's ECC treats the four header bytes as zero.</summary>
    public static void GenerateMode2Ecc(Span<byte> sector)
    {
        Span<byte> header = stackalloc byte[4];
        sector.Slice(12, 4).CopyTo(header);
        sector.Slice(12, 4).Clear();
        GenerateEcc(sector);
        header.CopyTo(sector[12..]);
    }

    private static void Accumulate(byte value, ref byte v1, ref byte v2)
    {
        v1 ^= value;
        v2 ^= value;
        v1 = EccForward[v1];
    }

    private static void Finish(ref byte v1, ref byte v2)
    {
        v1 = EccBackward[EccForward[v1] ^ v2];
        v2 ^= v1;
    }
}
