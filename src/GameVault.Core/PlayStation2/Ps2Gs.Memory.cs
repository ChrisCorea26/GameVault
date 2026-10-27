using System.Runtime.CompilerServices;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// Local memory and its swizzling (the GS User's Manual's "Data Formats in the Local Memory"): memory is split into
/// 8 KB pages, each page into 32 blocks of 256 bytes, each block into 4 columns of 64 bytes. A buffer is a grid of
/// pages (its width in 64-pixel units); inside a page, blocks and the pixels inside a column follow per-format tables.
/// The tables are ported from Play! (BSD-2-Clause): Source/gs/GsPixelFormats.cpp/.h.
/// </summary>
public sealed partial class Ps2Gs
{
    public const int LocalMemorySize = 4 * 1024 * 1024;

    // Pixel storage modes (ps2tek: "GS Transfers", BITBLTBUF).
    public const int PsmCt32 = 0x00;
    public const int PsmCt24 = 0x01;
    public const int PsmCt16 = 0x02;
    public const int PsmCt16S = 0x0A;
    public const int PsmT8 = 0x13;
    public const int PsmT4 = 0x14;
    public const int PsmT8H = 0x1B;
    public const int PsmT4HL = 0x24;
    public const int PsmT4HH = 0x2C;
    public const int PsmZ32 = 0x30;
    public const int PsmZ24 = 0x31;
    public const int PsmZ16 = 0x32;
    public const int PsmZ16S = 0x3A;

    private const int MemoryMask = LocalMemorySize - 1;

    // Byte offsets inside a page, indexed [y * pageWidth + x] (PSMT4: nibble offsets).
    private static readonly int[] PageCt32 = BuildPage32(BlockCt32);
    private static readonly int[] PageZ32 = BuildPage32(BlockZ32);
    private static readonly int[] PageCt16 = BuildPage16(BlockCt16);
    private static readonly int[] PageCt16S = BuildPage16(BlockCt16S);
    private static readonly int[] PageZ16 = BuildPage16(BlockZ16);
    private static readonly int[] PageZ16S = BuildPage16(BlockZ16S);
    private static readonly int[] PageT8 = BuildPageT8();
    private static readonly int[] PageT4 = BuildPageT4();

    // Block numbers inside a page by [blockY, blockX].
    private static int[,] BlockCt32 => new[,]
    {
        { 0, 1, 4, 5, 16, 17, 20, 21 },
        { 2, 3, 6, 7, 18, 19, 22, 23 },
        { 8, 9, 12, 13, 24, 25, 28, 29 },
        { 10, 11, 14, 15, 26, 27, 30, 31 },
    };

    private static int[,] BlockZ32 => new[,]
    {
        { 24, 25, 28, 29, 8, 9, 12, 13 },
        { 26, 27, 30, 31, 10, 11, 14, 15 },
        { 16, 17, 20, 21, 0, 1, 4, 5 },
        { 18, 19, 22, 23, 2, 3, 6, 7 },
    };

    private static int[,] BlockCt16 => new[,]
    {
        { 0, 2, 8, 10 }, { 1, 3, 9, 11 }, { 4, 6, 12, 14 }, { 5, 7, 13, 15 },
        { 16, 18, 24, 26 }, { 17, 19, 25, 27 }, { 20, 22, 28, 30 }, { 21, 23, 29, 31 },
    };

    private static int[,] BlockCt16S => new[,]
    {
        { 0, 2, 16, 18 }, { 1, 3, 17, 19 }, { 8, 10, 24, 26 }, { 9, 11, 25, 27 },
        { 4, 6, 20, 22 }, { 5, 7, 21, 23 }, { 12, 14, 28, 30 }, { 13, 15, 29, 31 },
    };

    private static int[,] BlockZ16 => new[,]
    {
        { 24, 26, 16, 18 }, { 25, 27, 17, 19 }, { 28, 30, 20, 22 }, { 29, 31, 21, 23 },
        { 8, 10, 0, 2 }, { 9, 11, 1, 3 }, { 12, 14, 4, 6 }, { 13, 15, 5, 7 },
    };

    private static int[,] BlockZ16S => new[,]
    {
        { 24, 26, 8, 10 }, { 25, 27, 9, 11 }, { 16, 18, 0, 2 }, { 17, 19, 1, 3 },
        { 28, 30, 12, 14 }, { 29, 31, 13, 15 }, { 20, 22, 4, 6 }, { 21, 23, 5, 7 },
    };

    // Word order inside a column for PSMT8/PSMT4, by [table, y & 1, x & 7].
    private static int[,,] ColumnWords => new[,,]
    {
        { { 0, 1, 4, 5, 8, 9, 12, 13 }, { 2, 3, 6, 7, 10, 11, 14, 15 } },
        { { 8, 9, 12, 13, 0, 1, 4, 5 }, { 10, 11, 14, 15, 2, 3, 6, 7 } },
    };

    private static int[] BuildPage32(int[,] blocks)
    {
        int[,] column = { { 0, 1, 4, 5, 8, 9, 12, 13 }, { 2, 3, 6, 7, 10, 11, 14, 15 } };
        var table = new int[64 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var block = blocks[y >> 3, x >> 3];
                var col = (y >> 1) & 3;
                table[(y * 64) + x] = (block * 256) + (col * 64) + (4 * column[y & 1, x & 7]);
            }
        }

        return table;
    }

    private static int[] BuildPage16(int[,] blocks)
    {
        int[,] column =
        {
            { 0, 2, 8, 10, 16, 18, 24, 26, 1, 3, 9, 11, 17, 19, 25, 27 },
            { 4, 6, 12, 14, 20, 22, 28, 30, 5, 7, 13, 15, 21, 23, 29, 31 },
        };
        var table = new int[64 * 64];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var block = blocks[y >> 3, x >> 4];
                var col = (y >> 1) & 3;
                table[(y * 64) + x] = (block * 256) + (col * 64) + (2 * column[y & 1, x & 15]);
            }
        }

        return table;
    }

    private static int[] BuildPageT8()
    {
        var blocks = BlockCt32;
        var words = ColumnWords;
        var table = new int[128 * 64];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var block = blocks[y >> 4, x >> 4];
                var bx = x & 15;
                var by = y & 15;
                var col = by >> 2;
                var cy = by & 3;
                var sub = ((cy & 2) >> 1) ^ ((y >> 2) & 1);
                var b = ((bx & 8) >> 2) + ((cy & 2) >> 1);
                table[(y * 128) + x] = (block * 256) + (col * 64) + (words[sub, cy & 1, bx & 7] * 4) + b;
            }
        }

        return table;
    }

    private static int[] BuildPageT4()
    {
        var blocks = BlockCt16;
        var words = ColumnWords;
        var table = new int[128 * 128];
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 128; x++)
            {
                var block = blocks[y >> 4, x >> 5];
                var bx = x & 31;
                var by = y & 15;
                var col = by >> 2;
                var cy = by & 3;
                var shift = (bx & 0x18) + ((cy & 2) << 1);
                var sub = ((cy & 2) >> 1) ^ (col & 1);
                table[(y * 128) + x] = (((col * 64) + (block * 256) + (words[sub, cy & 1, bx & 7] * 4)) * 2) + (shift / 4);
            }
        }

        return table;
    }

    /// <summary>The bits per pixel of a storage mode as transfers count them (PSMT8H, PSMT4HL/HH: 8 and 4).</summary>
    public static int BitsPerPixel(int psm) => psm switch
    {
        PsmCt32 or PsmZ32 => 32,
        PsmCt24 or PsmZ24 => 24,
        PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S => 16,
        PsmT8 or PsmT8H => 8,
        PsmT4 or PsmT4HL or PsmT4HH => 4,
        _ => 32,
    };

    /// <summary>
    /// The byte address of a 32-bit-layout pixel (PSMCT32/24, PSMT8H, PSMT4HL/HH, PSMZ32/24). <paramref name="bp"/> is in
    /// blocks (64 words), <paramref name="bw"/> in 64-pixel units. Coordinates wrap at 2048.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address32(int[] table, int bp, int bw, int x, int y)
    {
        x &= 2047;
        y &= 2047;
        var page = (x >> 6) + ((y >> 5) * bw);
        return ((bp << 8) + (page << 13) + table[((y & 31) << 6) | (x & 63)]) & MemoryMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address16(int[] table, int bp, int bw, int x, int y)
    {
        x &= 2047;
        y &= 2047;
        var page = (x >> 6) + ((y >> 6) * bw);
        return ((bp << 8) + (page << 13) + table[((y & 63) << 6) | (x & 63)]) & MemoryMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address8(int bp, int bw, int x, int y)
    {
        x &= 2047;
        y &= 2047;
        var page = (x >> 7) + ((y >> 6) * (bw >> 1));
        return ((bp << 8) + (page << 13) + PageT8[((y & 63) << 7) | (x & 127)]) & MemoryMask;
    }

    /// <summary>A nibble address (byte address × 2, plus 1 for the high nibble).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address4(int bp, int bw, int x, int y)
    {
        x &= 2047;
        y &= 2047;
        var page = (x >> 7) + ((y >> 7) * (bw >> 1));
        return ((bp << 9) + (page << 14) + PageT4[((y & 127) << 7) | (x & 127)]) & ((LocalMemorySize * 2) - 1);
    }

    /// <summary>
    /// The byte address (nibble address for PSMT4) of pixel (<paramref name="x"/>, <paramref name="y"/>) in a buffer
    /// at block <paramref name="bp"/> with width <paramref name="bw"/> × 64.
    /// </summary>
    public static int PixelAddress(int psm, int bp, int bw, int x, int y) => psm switch
    {
        PsmCt32 or PsmCt24 or PsmT8H or PsmT4HL or PsmT4HH => Address32(PageCt32, bp, bw, x, y),
        PsmZ32 or PsmZ24 => Address32(PageZ32, bp, bw, x, y),
        PsmCt16 => Address16(PageCt16, bp, bw, x, y),
        PsmCt16S => Address16(PageCt16S, bp, bw, x, y),
        PsmZ16 => Address16(PageZ16, bp, bw, x, y),
        PsmZ16S => Address16(PageZ16S, bp, bw, x, y),
        PsmT8 => Address8(bp, bw, x, y),
        PsmT4 => Address4(bp, bw, x, y),
        _ => Address32(PageCt32, bp, bw, x, y),
    };

    /// <summary>
    /// Reads a pixel's stored bits: 32 bits for PSMCT32/PSMZ32, the low 24 for the 24-bit formats, 16 for the 16-bit
    /// ones, the index for PSMT8/PSMT4 and the upper byte or nibble for PSMT8H/PSMT4HL/PSMT4HH.
    /// </summary>
    public uint ReadPixel(int psm, int bp, int bw, int x, int y)
    {
        Flush();
        return ReadPixel(_memory, psm, bp, bw, x, y);
    }

    /// <summary>Writes a pixel's bits; the 24-bit and upper-byte/nibble formats leave the word's other bits alone.</summary>
    public void WritePixel(int psm, int bp, int bw, int x, int y, uint value)
    {
        Flush();
        WritePixel(_memory, psm, bp, bw, x, y, value);
    }

    private static uint ReadPixel(byte[] mem, int psm, int bp, int bw, int x, int y)
    {
        switch (psm)
        {
            case PsmCt32:
                return Read32(mem, Address32(PageCt32, bp, bw, x, y));
            case PsmCt24:
                return Read32(mem, Address32(PageCt32, bp, bw, x, y)) & 0xFFFFFF;
            case PsmZ32:
                return Read32(mem, Address32(PageZ32, bp, bw, x, y));
            case PsmZ24:
                return Read32(mem, Address32(PageZ32, bp, bw, x, y)) & 0xFFFFFF;
            case PsmCt16:
                return Read16(mem, Address16(PageCt16, bp, bw, x, y));
            case PsmCt16S:
                return Read16(mem, Address16(PageCt16S, bp, bw, x, y));
            case PsmZ16:
                return Read16(mem, Address16(PageZ16, bp, bw, x, y));
            case PsmZ16S:
                return Read16(mem, Address16(PageZ16S, bp, bw, x, y));
            case PsmT8:
                return mem[Address8(bp, bw, x, y)];
            case PsmT4:
            {
                var nibble = Address4(bp, bw, x, y);
                return (uint)(mem[nibble >> 1] >> ((nibble & 1) << 2)) & 0xF;
            }

            case PsmT8H:
                return mem[Address32(PageCt32, bp, bw, x, y) + 3];
            case PsmT4HL:
                return mem[Address32(PageCt32, bp, bw, x, y) + 3] & 0xFu;
            case PsmT4HH:
                return (uint)mem[Address32(PageCt32, bp, bw, x, y) + 3] >> 4;
            default:
                return Read32(mem, Address32(PageCt32, bp, bw, x, y));
        }
    }

    private static void WritePixel(byte[] mem, int psm, int bp, int bw, int x, int y, uint value)
    {
        switch (psm)
        {
            case PsmCt32:
                Write32(mem, Address32(PageCt32, bp, bw, x, y), value);
                break;
            case PsmZ32:
                Write32(mem, Address32(PageZ32, bp, bw, x, y), value);
                break;
            case PsmCt24:
                Write24(mem, Address32(PageCt32, bp, bw, x, y), value);
                break;
            case PsmZ24:
                Write24(mem, Address32(PageZ32, bp, bw, x, y), value);
                break;
            case PsmCt16:
                Write16(mem, Address16(PageCt16, bp, bw, x, y), (ushort)value);
                break;
            case PsmCt16S:
                Write16(mem, Address16(PageCt16S, bp, bw, x, y), (ushort)value);
                break;
            case PsmZ16:
                Write16(mem, Address16(PageZ16, bp, bw, x, y), (ushort)value);
                break;
            case PsmZ16S:
                Write16(mem, Address16(PageZ16S, bp, bw, x, y), (ushort)value);
                break;
            case PsmT8:
                mem[Address8(bp, bw, x, y)] = (byte)value;
                break;
            case PsmT4:
            {
                var nibble = Address4(bp, bw, x, y);
                var shift = (nibble & 1) << 2;
                ref var b = ref mem[nibble >> 1];
                b = (byte)((b & ~(0xF << shift)) | ((int)(value & 0xF) << shift));
                break;
            }

            case PsmT8H:
                mem[Address32(PageCt32, bp, bw, x, y) + 3] = (byte)value;
                break;
            case PsmT4HL:
            {
                ref var b = ref mem[Address32(PageCt32, bp, bw, x, y) + 3];
                b = (byte)((b & 0xF0) | (int)(value & 0xF));
                break;
            }

            case PsmT4HH:
            {
                ref var b = ref mem[Address32(PageCt32, bp, bw, x, y) + 3];
                b = (byte)((b & 0x0F) | (int)((value & 0xF) << 4));
                break;
            }

            default:
                Write32(mem, Address32(PageCt32, bp, bw, x, y), value);
                break;
        }
    }

    // Pixel addresses are aligned to their size and masked inside local memory, so these never cross its end. Local
    // memory is little-endian, like every host .NET runs this core on.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Read32(byte[] memory, int address) => Unsafe.ReadUnaligned<uint>(ref memory[address]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Read16(byte[] memory, int address) => Unsafe.ReadUnaligned<ushort>(ref memory[address]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write32(byte[] memory, int address, uint value) => Unsafe.WriteUnaligned(ref memory[address], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write16(byte[] memory, int address, ushort value) => Unsafe.WriteUnaligned(ref memory[address], value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write24(byte[] memory, int address, uint value)
    {
        memory[address] = (byte)value;
        memory[address + 1] = (byte)(value >> 8);
        memory[address + 2] = (byte)(value >> 16);
    }
}
