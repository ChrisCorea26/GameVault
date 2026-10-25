namespace GameVault.Core.Cartridge;

/// <summary>
/// A parsed iNES or NES 2.0 image (NESdev Wiki: "INES", "NES 2.0"). The core receives bytes only;
/// loading files is the caller's job.
/// </summary>
public sealed record INesRom
{
    public const int HeaderSize = 16;
    private const int TrainerSize = 512;

    public required byte[] PrgRom { get; init; }

    /// <summary>Empty when the board uses CHR-RAM.</summary>
    public required byte[] ChrRom { get; init; }

    public byte[]? Trainer { get; init; }

    public int Mapper { get; init; }

    public int Submapper { get; init; }

    public Mirroring Mirroring { get; init; }

    public bool HasBattery { get; init; }

    public bool IsNes2 { get; init; }

    /// <summary>Volatile plus battery-backed PRG-RAM in bytes.</summary>
    public int PrgRamSize { get; init; }

    /// <summary>CHR-RAM in bytes (8 KB when the iNES header has no CHR-ROM).</summary>
    public int ChrRamSize { get; init; }

    public TvSystem TvSystem { get; init; }

    public static INesRom Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A)
        {
            throw new RomFormatException("This is not an iNES file (missing \"NES\\x1A\" header).");
        }

        var flags6 = data[6];
        var flags7 = data[7];
        var isNes2 = (flags7 & 0x0C) == 0x08;

        int mapper, submapper = 0, prgRamSize, chrRamSize;
        long prgSize, chrSize;
        var tv = TvSystem.Ntsc;

        if (isNes2)
        {
            mapper = (flags6 >> 4) | (flags7 & 0xF0) | ((data[8] & 0x0F) << 8);
            submapper = data[8] >> 4;
            prgSize = Nes2RomSize(data[4], data[9] & 0x0F, 16 * 1024);
            chrSize = Nes2RomSize(data[5], data[9] >> 4, 8 * 1024);
            prgRamSize = ShiftSize(data[10] & 0x0F) + ShiftSize(data[10] >> 4);
            chrRamSize = ShiftSize(data[11] & 0x0F) + ShiftSize(data[11] >> 4);
            tv = (TvSystem)(data[12] & 0x03);
        }
        else
        {
            // Old dumping tools wrote junk ("DiskDude!") into bytes 7-15; ignore flags 7 in that case.
            var junk = data[12] != 0 || data[13] != 0 || data[14] != 0 || data[15] != 0;
            mapper = (flags6 >> 4) | (junk ? 0 : flags7 & 0xF0);
            prgSize = data[4] * 16L * 1024;
            chrSize = data[5] * 8L * 1024;
            prgRamSize = Math.Max(1, (int)data[8]) * 8 * 1024;
            chrRamSize = chrSize == 0 ? 8 * 1024 : 0;
        }

        var offset = HeaderSize;
        byte[]? trainer = null;
        if ((flags6 & 0x04) != 0)
        {
            trainer = Slice(data, ref offset, TrainerSize, "trainer");
        }

        var prg = Slice(data, ref offset, prgSize, "PRG-ROM");
        var chr = Slice(data, ref offset, chrSize, "CHR-ROM");

        if (prg.Length == 0)
        {
            throw new RomFormatException("The ROM has no PRG-ROM.");
        }

        var mirroring = (flags6 & 0x08) != 0 ? Mirroring.FourScreen
            : (flags6 & 0x01) != 0 ? Mirroring.Vertical
            : Mirroring.Horizontal;

        return new INesRom
        {
            PrgRom = prg,
            ChrRom = chr,
            Trainer = trainer,
            Mapper = mapper,
            Submapper = submapper,
            Mirroring = mirroring,
            HasBattery = (flags6 & 0x02) != 0,
            IsNes2 = isNes2,
            PrgRamSize = prgRamSize,
            ChrRamSize = chrRamSize,
            TvSystem = tv,
        };
    }

    private static byte[] Slice(ReadOnlySpan<byte> data, ref int offset, long size, string what)
    {
        if (offset + size > data.Length)
        {
            throw new RomFormatException($"The file is too short: {what} needs {size} bytes at offset {offset}.");
        }

        var result = data.Slice(offset, (int)size).ToArray();
        offset += (int)size;
        return result;
    }

    /// <summary>NES 2.0 ROM size: plain 12-bit unit count, or exponent-multiplier form when the MSB nibble is $F.</summary>
    private static long Nes2RomSize(byte lsb, int msb, int unit)
    {
        if (msb == 0x0F)
        {
            var exponent = lsb >> 2;
            var multiplier = (lsb & 0x03) * 2 + 1;
            return (1L << exponent) * multiplier;
        }

        return ((msb << 8) | lsb) * (long)unit;
    }

    private static int ShiftSize(int shift) => shift == 0 ? 0 : 64 << shift;
}
