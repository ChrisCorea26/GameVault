namespace GameVault.Core.Tests.Harness;

/// <summary>Builds synthetic iNES images in memory, so unit tests need no real game data (SPEC §11.1).</summary>
public static class TestRoms
{
    /// <summary>
    /// An NROM image whose 16 KB PRG bank starts with <paramref name="program"/> at $C000 (mirrored at
    /// $8000). The reset, NMI and IRQ vectors all point to $C000 unless overridden.
    /// </summary>
    public static byte[] Nrom(byte[] program, int prgBanks = 1, int chrBanks = 1, byte flags6 = 0, ushort? nmi = null)
    {
        var prgSize = prgBanks * 0x4000;
        var image = new byte[16 + prgSize + chrBanks * 0x2000];
        image[0] = (byte)'N';
        image[1] = (byte)'E';
        image[2] = (byte)'S';
        image[3] = 0x1A;
        image[4] = (byte)prgBanks;
        image[5] = (byte)chrBanks;
        image[6] = flags6;

        var prg = image.AsSpan(16, prgSize);
        var start = prgSize - 0x4000; // last 16 KB bank maps to $C000
        program.CopyTo(prg[start..]);
        WriteVector(prg, prgSize - 6, nmi ?? 0xC000);
        WriteVector(prg, prgSize - 4, 0xC000);
        WriteVector(prg, prgSize - 2, 0xC000);
        return image;
    }

    private static void WriteVector(Span<byte> prg, int offset, ushort target)
    {
        prg[offset] = (byte)target;
        prg[offset + 1] = (byte)(target >> 8);
    }
}
