namespace GameVault.Core.PlayStation2;

/// <summary>
/// The IPU's fixed tables: the MPEG-1/2 variable-length codes (ISO/IEC 13818-2 Annex B) as direct lookup tables, the
/// scan orders, the default quantiser matrices, the non-linear quantiser scale and the IDCT's cosine matrix.
/// </summary>
/// <remarks>
/// The VLC code lists are ported from Play! (BSD-2-Clause, Jean-Philip Desjardins): Source/ee/IPU_*Table.cpp and
/// Play--Framework src/mpeg2/*Table.cpp. Each lookup table is indexed by the next <c>MaxBits</c> bits of the stream
/// (MSB first, zero-padded past the data); an entry packs the code length in bits 0–4 and the decoded value (signed)
/// from bit 5 up, or is 0 for a bit pattern that starts no code.
/// </remarks>
internal static class Ps2IpuTables
{
    public const int DcLumBits = 9;
    public const int DcChromBits = 10;
    public const int CbpBits = 9;
    public const int MbaBits = 11;
    public const int MbTypeBits = 6;
    public const int MotionBits = 11;
    public const int DmvBits = 2;
    public const int DctBits = 16;

    /// <summary>The run of a DCT coefficient entry that marks the escape code (run and level follow as fixed-length fields).</summary>
    public const int DctEscape = 63;

    /// <summary>dct_dc_size_luminance (Table B.12): value = dc size.</summary>
    public static readonly int[] DcLum = Build(DcLumBits,
    [
        0x0, 2, 1, 0x1, 2, 2, 0x4, 3, 0, 0x5, 3, 3, 0x6, 3, 4, 0xE, 4, 5, 0x1E, 5, 6, 0x3E, 6, 7, 0x7E, 7, 8,
        0xFE, 8, 9, 0x1FE, 9, 10, 0x1FF, 9, 11,
    ]);

    /// <summary>dct_dc_size_chrominance (Table B.13): value = dc size.</summary>
    public static readonly int[] DcChrom = Build(DcChromBits,
    [
        0x0, 2, 0, 0x1, 2, 1, 0x2, 2, 2, 0x6, 3, 3, 0xE, 4, 4, 0x1E, 5, 5, 0x3E, 6, 6, 0x7E, 7, 7, 0xFE, 8, 8,
        0x1FE, 9, 9, 0x3FE, 10, 10, 0x3FF, 10, 11,
    ]);

    /// <summary>coded_block_pattern (Table B.9): value = the six-bit pattern (bit 5 = block 0).</summary>
    public static readonly int[] Cbp = Build(CbpBits,
    [
        0x7, 3, 60, 0xD, 4, 4, 0xC, 4, 8, 0xB, 4, 16, 0xA, 4, 32, 0x13, 5, 12, 0x12, 5, 48, 0x11, 5, 20, 0x10, 5, 40,
        0xF, 5, 28, 0xE, 5, 44, 0xD, 5, 52, 0xC, 5, 56, 0xB, 5, 1, 0xA, 5, 61, 0x9, 5, 2, 0x8, 5, 62, 0xF, 6, 24,
        0xE, 6, 36, 0xD, 6, 3, 0xC, 6, 63, 0x17, 7, 5, 0x16, 7, 9, 0x15, 7, 17, 0x14, 7, 33, 0x13, 7, 6, 0x12, 7, 10,
        0x11, 7, 18, 0x10, 7, 34, 0x1F, 8, 7, 0x1E, 8, 11, 0x1D, 8, 19, 0x1C, 8, 35, 0x1B, 8, 13, 0x1A, 8, 49,
        0x19, 8, 21, 0x18, 8, 41, 0x17, 8, 14, 0x16, 8, 50, 0x15, 8, 22, 0x14, 8, 42, 0x13, 8, 15, 0x12, 8, 51,
        0x11, 8, 23, 0x10, 8, 43, 0xF, 8, 25, 0xE, 8, 37, 0xD, 8, 26, 0xC, 8, 38, 0xB, 8, 29, 0xA, 8, 45, 0x9, 8, 53,
        0x8, 8, 57, 0x7, 8, 30, 0x6, 8, 46, 0x5, 8, 54, 0x4, 8, 58, 0x7, 9, 31, 0x6, 9, 47, 0x5, 9, 55, 0x4, 9, 59,
        0x3, 9, 27, 0x2, 9, 39, 0x1, 9, 0,
    ]);

    /// <summary>
    /// macroblock_address_increment (Table B.1) without macroblock_escape ("0000 0001 000") and macroblock_stuffing
    /// ("0000 0001 111"), which the decoder handles itself: value = the increment.
    /// </summary>
    public static readonly int[] Mba = Build(MbaBits,
    [
        0x1, 1, 1, 0x3, 3, 2, 0x2, 3, 3, 0x3, 4, 4, 0x2, 4, 5, 0x3, 5, 6, 0x2, 5, 7, 0x7, 7, 8, 0x6, 7, 9,
        0xB, 8, 10, 0xA, 8, 11, 0x9, 8, 12, 0x8, 8, 13, 0x7, 8, 14, 0x6, 8, 15, 0x17, 10, 16, 0x16, 10, 17,
        0x15, 10, 18, 0x14, 10, 19, 0x13, 10, 20, 0x12, 10, 21, 0x23, 11, 22, 0x22, 11, 23, 0x21, 11, 24,
        0x20, 11, 25, 0x1F, 11, 26, 0x1E, 11, 27, 0x1D, 11, 28, 0x1C, 11, 29, 0x1B, 11, 30, 0x1A, 11, 31,
        0x19, 11, 32, 0x18, 11, 33,
    ]);

    /// <summary>The macroblock_escape code (11 bits): adds 33 to the increment.</summary>
    public const int MbaEscape = 0x008;

    /// <summary>The macroblock_stuffing code (11 bits, MPEG-1 only): skipped.</summary>
    public const int MbaStuffing = 0x00F;

    // macroblock_type values: bit 0 intra, bit 1 pattern, bit 2 motion backward, bit 3 motion forward, bit 4 quant.

    /// <summary>macroblock_type in I-pictures (Table B.2).</summary>
    public static readonly int[] MbTypeI = Build(MbTypeBits, [0x1, 1, 0x01, 0x1, 2, 0x11]);

    /// <summary>macroblock_type in P-pictures (Table B.3).</summary>
    public static readonly int[] MbTypeP = Build(MbTypeBits,
    [
        0x1, 1, 0x0A, 0x1, 2, 0x02, 0x1, 3, 0x08, 0x3, 5, 0x01, 0x2, 5, 0x1A, 0x1, 5, 0x12, 0x1, 6, 0x11,
    ]);

    /// <summary>macroblock_type in B-pictures (Table B.4).</summary>
    public static readonly int[] MbTypeB = Build(MbTypeBits,
    [
        0x2, 2, 0x0C, 0x3, 2, 0x0E, 0x2, 3, 0x04, 0x3, 3, 0x06, 0x2, 4, 0x08, 0x3, 4, 0x0A, 0x3, 5, 0x01,
        0x2, 5, 0x1E, 0x3, 6, 0x1A, 0x2, 6, 0x16, 0x1, 6, 0x11,
    ]);

    /// <summary>macroblock_type in MPEG-1 D-pictures: a single "1" (intra).</summary>
    public static readonly int[] MbTypeD = Build(MbTypeBits, [0x1, 1, 0x01]);

    /// <summary>motion_code (Table B.10), the sign included: value = the signed motion code.</summary>
    public static readonly int[] Motion = Build(MotionBits,
    [
        0x1, 1, 0, 0x2, 3, 1, 0x3, 3, -1, 0x2, 4, 2, 0x3, 4, -2, 0x2, 5, 3, 0x3, 5, -3, 0x6, 7, 4, 0x7, 7, -4,
        0xA, 8, 5, 0xB, 8, -5, 0x8, 8, 6, 0x9, 8, -6, 0x6, 8, 7, 0x7, 8, -7, 0x16, 10, 8, 0x17, 10, -8,
        0x14, 10, 9, 0x15, 10, -9, 0x12, 10, 10, 0x13, 10, -10, 0x22, 11, 11, 0x23, 11, -11, 0x20, 11, 12,
        0x21, 11, -12, 0x1E, 11, 13, 0x1F, 11, -13, 0x1C, 11, 14, 0x1D, 11, -14, 0x1A, 11, 15, 0x1B, 11, -15,
        0x18, 11, 16, 0x19, 11, -16,
    ]);

    /// <summary>dmvector (Table B.11).</summary>
    public static readonly int[] Dmv = Build(DmvBits, [0x0, 1, 0, 0x2, 2, 1, 0x3, 2, -1]);

    /// <summary>
    /// DCT coefficients, Table B.14 (all non-intra blocks; intra blocks unless IVF in MPEG-2), without its end of block
    /// ("10") and the first-coefficient "1s" form. Entry: length in bits 0–4, run in bits 5–10 (<see cref="DctEscape"/>
    /// for the escape), level from bit 11; the sign bit follows the code.
    /// </summary>
    public static readonly int[] DctB14 = BuildDct(
    [
        0x3, 2, 0, 1, 0x3, 3, 1, 1, 0x4, 4, 0, 2, 0x5, 4, 2, 1, 0x5, 5, 0, 3, 0x7, 5, 3, 1, 0x6, 5, 4, 1,
        0x6, 6, 1, 2, 0x7, 6, 5, 1, 0x5, 6, 6, 1, 0x4, 6, 7, 1, 0x1, 6, -1, 0, 0x6, 7, 0, 4, 0x4, 7, 2, 2,
        0x7, 7, 8, 1, 0x5, 7, 9, 1, 0x26, 8, 0, 5, 0x21, 8, 0, 6, 0x25, 8, 1, 3, 0x24, 8, 3, 2, 0x27, 8, 10, 1,
        0x23, 8, 11, 1, 0x22, 8, 12, 1, 0x20, 8, 13, 1, 0xA, 10, 0, 7, 0xC, 10, 1, 4, 0xB, 10, 2, 3, 0xF, 10, 4, 2,
        0x9, 10, 5, 2, 0xE, 10, 14, 1, 0xD, 10, 15, 1, 0x8, 10, 16, 1, 0x1D, 12, 0, 8, 0x18, 12, 0, 9,
        0x13, 12, 0, 10, 0x10, 12, 0, 11, 0x1B, 12, 1, 5, 0x14, 12, 2, 4, 0x1C, 12, 3, 3, 0x12, 12, 4, 3,
        0x1E, 12, 6, 2, 0x15, 12, 7, 2, 0x11, 12, 8, 2, 0x1F, 12, 17, 1, 0x1A, 12, 18, 1, 0x19, 12, 19, 1,
        0x17, 12, 20, 1, 0x16, 12, 21, 1, 0x1A, 13, 0, 12, 0x19, 13, 0, 13, 0x18, 13, 0, 14, 0x17, 13, 0, 15,
        0x16, 13, 1, 6, 0x15, 13, 1, 7, 0x14, 13, 2, 5, 0x13, 13, 3, 4, 0x12, 13, 5, 3, 0x11, 13, 9, 2,
        0x10, 13, 10, 2, 0x1F, 13, 22, 1, 0x1E, 13, 23, 1, 0x1D, 13, 24, 1, 0x1C, 13, 25, 1, 0x1B, 13, 26, 1,
        .. CommonLongDctCodes,
    ]);

    /// <summary>DCT coefficients, Table B.15 (intra blocks with IPU_CTRL.IVF in MPEG-2), without its end of block ("0110").</summary>
    public static readonly int[] DctB15 = BuildDct(
    [
        0x2, 2, 0, 1, 0x2, 3, 1, 1, 0x6, 3, 0, 2, 0x7, 4, 0, 3, 0x5, 5, 2, 1, 0x7, 5, 3, 1, 0x6, 5, 1, 2,
        0x1C, 5, 0, 4, 0x1D, 5, 0, 5, 0x6, 6, 4, 1, 0x7, 6, 5, 1, 0x5, 6, 0, 6, 0x4, 6, 0, 7, 0x1, 6, -1, 0,
        0x79, 7, 1, 3, 0x7A, 7, 10, 1, 0x7, 7, 2, 2, 0x5, 7, 8, 1, 0x78, 7, 9, 1, 0x6, 7, 6, 1, 0x4, 7, 7, 1,
        0x7B, 7, 0, 8, 0x7C, 7, 0, 9, 0x26, 8, 3, 2, 0x21, 8, 11, 1, 0x25, 8, 12, 1, 0x24, 8, 13, 1, 0x27, 8, 1, 4,
        0xFC, 8, 2, 3, 0xFD, 8, 4, 2, 0xFA, 8, 0, 12, 0xFB, 8, 0, 13, 0xFE, 8, 0, 14, 0xFF, 8, 0, 15,
        0x23, 8, 0, 10, 0x22, 8, 0, 11, 0x20, 8, 1, 5, 0x4, 9, 5, 2, 0x5, 9, 14, 1, 0x7, 9, 15, 1,
        0xD, 10, 16, 1, 0xC, 10, 2, 4, 0x1C, 12, 3, 3, 0x12, 12, 4, 3, 0x1E, 12, 6, 2, 0x15, 12, 7, 2,
        0x11, 12, 8, 2, 0x1F, 12, 17, 1, 0x1A, 12, 18, 1, 0x19, 12, 19, 1, 0x17, 12, 20, 1, 0x16, 12, 21, 1,
        0x16, 13, 1, 6, 0x15, 13, 1, 7, 0x14, 13, 2, 5, 0x13, 13, 3, 4, 0x12, 13, 5, 3, 0x11, 13, 9, 2,
        0x10, 13, 10, 2, 0x1F, 13, 22, 1, 0x1E, 13, 23, 1, 0x1D, 13, 24, 1, 0x1C, 13, 25, 1, 0x1B, 13, 26, 1,
        .. CommonLongDctCodes,
    ]);

    /// <summary>The zigzag scan (Figure 7-2): scan position → raster position.</summary>
    public static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14,
        21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53,
        60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>The alternate scan (Figure 7-3, IPU_CTRL.AS): scan position → raster position.</summary>
    public static readonly byte[] Alternate =
    [
        0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49, 41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42, 50,
        58, 35, 43, 51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45, 53, 61, 22, 30, 7, 15, 23, 31, 38,
        46, 54, 62, 39, 47, 55, 63,
    ];

    /// <summary>
    /// The default intra quantiser matrix (ISO/IEC 13818-2 §6.3.11) in scan (bitstream) order, the order SETIQ loads
    /// and the decoder indexes the IPU's matrices in.
    /// </summary>
    public static readonly byte[] DefaultIntraMatrix =
    [
        8, 16, 16, 19, 16, 19, 22, 22, 22, 22, 22, 22, 26, 24, 26, 27, 27, 27, 26, 26, 26, 26, 27, 27, 27, 29, 29, 29,
        34, 34, 34, 29, 29, 29, 27, 27, 29, 29, 32, 32, 34, 34, 37, 38, 37, 35, 35, 34, 35, 38, 38, 40, 40, 40, 48, 48,
        46, 46, 56, 56, 58, 69, 69, 83,
    ];

    /// <summary>quantiser_scale for q_scale_type 1 (IPU_CTRL.QST, Table 7-6).</summary>
    public static readonly byte[] NonLinearScale =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 44, 48, 52, 56, 64, 72, 80, 88, 96,
        104, 112,
    ];

    /// <summary>
    /// The 8-point IDCT matrix: [x * 8 + u] = round(2^20 · C(u)/2 · cos((2x + 1)uπ/16)), C(0) = 1/√2, else 1.
    /// Integer constants keep the transform bit-exact on every machine.
    /// </summary>
    public static readonly long[] IdctMatrix =
    [
        370728, 514214, 484379, 435930, 370728, 291279, 200636, 102284,
        370728, 435930, 200636, -102284, -370728, -514214, -484379, -291279,
        370728, 291279, -200636, -514214, -370728, 102284, 484379, 435930,
        370728, 102284, -484379, -291279, 370728, 435930, -200636, -514214,
        370728, -102284, -484379, 291279, 370728, -435930, -200636, 514214,
        370728, -291279, -200636, 514214, -370728, -102284, 484379, -435930,
        370728, -435930, 200636, 102284, -370728, 514214, -484379, 291279,
        370728, -514214, 484379, -435930, 370728, -291279, 200636, -102284,
    ];

    /// <summary>The 4×4 ordered dither the IPU adds before truncating to RGB16 (IPU_CMD.DTE), by [y &amp; 3][x &amp; 3].</summary>
    public static readonly sbyte[] Dither =
    [
        -4, 0, -3, 1,
        2, -2, 3, -1,
        -3, 1, -4, 0,
        3, -1, 2, -2,
    ];

    // The 12- to 16-bit codes are the same in B.14 and B.15.
    private static ReadOnlySpan<int> CommonLongDctCodes =>
    [
        0x1F, 14, 0, 16, 0x1E, 14, 0, 17, 0x1D, 14, 0, 18, 0x1C, 14, 0, 19, 0x1B, 14, 0, 20, 0x1A, 14, 0, 21,
        0x19, 14, 0, 22, 0x18, 14, 0, 23, 0x17, 14, 0, 24, 0x16, 14, 0, 25, 0x15, 14, 0, 26, 0x14, 14, 0, 27,
        0x13, 14, 0, 28, 0x12, 14, 0, 29, 0x11, 14, 0, 30, 0x10, 14, 0, 31, 0x18, 15, 0, 32, 0x17, 15, 0, 33,
        0x16, 15, 0, 34, 0x15, 15, 0, 35, 0x14, 15, 0, 36, 0x13, 15, 0, 37, 0x12, 15, 0, 38, 0x11, 15, 0, 39,
        0x10, 15, 0, 40, 0x1F, 15, 1, 8, 0x1E, 15, 1, 9, 0x1D, 15, 1, 10, 0x1C, 15, 1, 11, 0x1B, 15, 1, 12,
        0x1A, 15, 1, 13, 0x19, 15, 1, 14, 0x13, 16, 1, 15, 0x12, 16, 1, 16, 0x11, 16, 1, 17, 0x10, 16, 1, 18,
        0x14, 16, 6, 3, 0x1A, 16, 11, 2, 0x19, 16, 12, 2, 0x18, 16, 13, 2, 0x17, 16, 14, 2, 0x16, 16, 15, 2,
        0x15, 16, 16, 2, 0x1F, 16, 27, 1, 0x1E, 16, 28, 1, 0x1D, 16, 29, 1, 0x1C, 16, 30, 1, 0x1B, 16, 31, 1,
    ];

    /// <summary>Builds a lookup table from (code, length, value) triples.</summary>
    private static int[] Build(int maxBits, ReadOnlySpan<int> codes)
    {
        var table = new int[1 << maxBits];
        for (int i = 0; i < codes.Length; i += 3)
        {
            Fill(table, maxBits, codes[i], codes[i + 1], (codes[i + 2] << 5) | codes[i + 1]);
        }

        return table;
    }

    /// <summary>Builds a DCT coefficient table from (code, length, run, level) quadruples; run -1 is the escape.</summary>
    private static int[] BuildDct(ReadOnlySpan<int> codes)
    {
        var table = new int[1 << DctBits];
        for (int i = 0; i < codes.Length; i += 4)
        {
            int run = codes[i + 2] < 0 ? DctEscape : codes[i + 2];
            Fill(table, DctBits, codes[i], codes[i + 1], codes[i + 1] | (run << 5) | (codes[i + 3] << 11));
        }

        return table;
    }

    private static void Fill(int[] table, int maxBits, int code, int length, int entry)
    {
        int shift = maxBits - length;
        int first = code << shift;
        for (int j = 0; j < 1 << shift; j++)
        {
            table[first + j] = entry;
        }
    }
}
