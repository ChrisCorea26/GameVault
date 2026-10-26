using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using GameVault.Core.Tests.PlayStation2.Dma;

namespace GameVault.Core.Tests.PlayStation2.Ipu;

/// <summary>The IPU (ps2tek: "Image Processing Unit (IPU)"): commands, FIFOs, stalls, interrupts and save states.</summary>
public sealed class Ps2IpuTests
{
    // IPU_CTRL mode bits.
    private const uint Idp1 = 1u << 16;
    private const uint As = 1u << 20;
    private const uint Ivf = 1u << 21;
    private const uint Qst = 1u << 22;
    private const uint Mp1 = 1u << 23;
    private const uint Ecd = 1u << 14;
    private const uint Scd = 1u << 15;
    private const uint IpuIrq = 1u << 8;

    private static uint Pct(int type) => (uint)type << 24;

    private static uint Command(int code, uint options = 0) => ((uint)code << 28) | options;

    private static uint Bdec(int qsc, bool intra, bool dcr = true, bool field = false, int fb = 0) =>
        Command(Ps2Ipu.CmdBdec, (uint)fb | ((uint)qsc << 16) | (field ? 1u << 25 : 0) | (dcr ? 1u << 26 : 0)
            | (intra ? 1u << 27 : 0));

    private static uint Idec(int qsc, bool rgb16 = false, bool dither = false, bool sgn = false, bool dtd = false) =>
        Command(Ps2Ipu.CmdIdec, ((uint)qsc << 16) | (dtd ? 1u << 24 : 0) | (sgn ? 1u << 25 : 0)
            | (dither ? 1u << 26 : 0) | (rgb16 ? 1u << 27 : 0));

    private static (Ps2Ipu Ipu, Ps2Intc Intc) Create(uint ctrl = 0)
    {
        var intc = new Ps2Intc();
        var ipu = new Ps2Ipu(intc);
        ipu.WriteRegister(Ps2Ipu.RegCtrl, ctrl);
        return (ipu, intc);
    }

    private static void Feed(Ps2Ipu ipu, ReadOnlySpan<UInt128> data)
    {
        ipu.WriteIn(data).Should().Be(data.Length, "the FIFO has room");
    }

    /// <summary>Feeds the input and drains the output until the command ends, as the two DMA channels would.</summary>
    private static List<UInt128> Run(Ps2Ipu ipu, UInt128[] input, int chunk = 8)
    {
        var output = new List<UInt128>();
        var buffer = new UInt128[8];
        int fed = 0;
        for (int guard = 0; guard < 100000; guard++)
        {
            if (fed < input.Length)
            {
                fed += ipu.WriteIn(input.AsSpan(fed, Math.Min(chunk, input.Length - fed)));
            }

            int n = ipu.ReadOut(buffer);
            output.AddRange(buffer.AsSpan(0, n).ToArray());
            if (!ipu.Busy && ipu.OutputCount == 0)
            {
                return output;
            }
        }

        throw new InvalidOperationException("The IPU never finished.");
    }

    private static short[] Levels(params (int Index, int Level)[] entries)
    {
        var levels = new short[64];
        foreach (var (index, level) in entries)
        {
            levels[index] = (short)level;
        }

        return levels;
    }

    private static short[] ReadRaw16(List<UInt128> output, int offset = 0)
    {
        byte[] bytes = IpuBitWriter.Bytes(output);
        var samples = new short[384];
        for (int i = 0; i < 384; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + i * 2));
        }

        return samples;
    }

    // ---- VDEC / FDEC / VLC tables ----

    private static (uint Result, uint Bp, Ps2Ipu Ipu) Vdec(int table, string code, uint ctrl = 0)
    {
        var (ipu, _) = Create(ctrl);
        var w = new IpuBitWriter().Put(code).Put(0xA5A5A5A5, 32);
        Feed(ipu, w.ToQuadwords(1));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdVdec, (uint)table << 26));
        ipu.Busy.Should().BeFalse();
        return ((uint)ipu.ReadRegister(Ps2Ipu.RegCmd), (uint)ipu.ReadRegister(Ps2Ipu.RegBp) & 0x7F, ipu);
    }

    [Theory]
    [InlineData(0, "1", 0u, 0x10001u)]
    [InlineData(0, "011", 0u, 0x30002u)]
    [InlineData(0, "0001 0", 0u, 0x50007u)]
    [InlineData(0, "0000 0011 000", 0u, 0xB0021u)]
    [InlineData(0, "0000 0001 000", 0u, 0xB0023u)]
    [InlineData(1, "1", 1u, 0x10001u)]
    [InlineData(1, "01", 1u, 0x20011u)]
    [InlineData(1, "1", 2u, 0x1000Au)]
    [InlineData(1, "0001 1", 2u, 0x50001u)]
    [InlineData(1, "0000 01", 2u, 0x60011u)]
    [InlineData(1, "10", 3u, 0x2000Cu)]
    [InlineData(1, "0001 0", 3u, 0x5001Eu)]
    [InlineData(1, "0000 01", 3u, 0x60011u)]
    [InlineData(2, "1", 0u, 0x10000u)]
    [InlineData(2, "010", 0u, 0x30001u)]
    [InlineData(2, "011", 0u, 0x3FFFFu)]
    [InlineData(2, "0000 0011 001", 0u, 0xBFFF0u)]
    [InlineData(3, "0", 0u, 0x10000u)]
    [InlineData(3, "10", 0u, 0x20001u)]
    [InlineData(3, "11", 0u, 0x2FFFFu)]
    public void VdecDecodesOneSymbolWithItsLength(int table, string code, uint pictureType, uint expected)
    {
        var (result, bp, ipu) = Vdec(table, code, Pct((int)pictureType));
        result.Should().Be(expected);
        bp.Should().Be(expected >> 16, "VDEC consumes the code");
        ((uint)ipu.ReadRegister(Ps2Ipu.RegTop)).Should().Be(0xA5A5A5A5u, "IPU_TOP shows the bits after it");
        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) & Ecd).Should().Be(0);
    }

    [Fact]
    public void VdecOfAnInvalidCodeSetsEcdAndReturnsZero()
    {
        var (result, bp, ipu) = Vdec(1, "00", Pct(1));
        result.Should().Be(0);
        bp.Should().Be(0);
        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) & Ecd).Should().Be(Ecd);
    }

    [Fact]
    public void FdecSkipsFbBitsAndPeeksThe32BitsAfterThem()
    {
        var (ipu, intc) = Create();
        Feed(ipu, new IpuBitWriter().Put(0x12345678, 32).Put(0x9ABCDEF0, 32).ToQuadwords());
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdFdec, 4));

        ipu.Busy.Should().BeFalse();
        ((uint)ipu.ReadRegister(Ps2Ipu.RegCmd)).Should().Be(0x23456789u);
        ((uint)ipu.ReadRegister(Ps2Ipu.RegTop)).Should().Be(0x23456789u);
        (ipu.ReadRegister(Ps2Ipu.RegBp) & 0x7F).Should().Be(4, "FDEC does not consume what it reads");
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void FdecWaitsForDataAndFinishesWhenTheFifoGetsIt()
    {
        var (ipu, intc) = Create();
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdFdec));
        ipu.Busy.Should().BeTrue();
        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) >> 31).Should().Be(1);
        (ipu.ReadRegister(Ps2Ipu.RegCmd) >> 63).Should().Be(1);
        (intc.Stat & IpuIrq).Should().Be(0);

        ipu.WriteFifo(new IpuBitWriter().Put(0xCAFEF00D, 32).ToQuadwords()[0]);

        ipu.Busy.Should().BeFalse();
        ((uint)ipu.ReadRegister(Ps2Ipu.RegCmd)).Should().Be(0xCAFEF00Du);
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    // ---- IDCT ----

    [Fact]
    public void IdctMatchesADoublePrecisionReferenceWithinOne()
    {
        var random = new Random(1234);
        int worst = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            var f = new int[64];
            int nonZero = trial % 4 == 0 ? 64 : random.Next(1, 12);
            int range = trial % 3 == 0 ? 2048 : 300;
            for (int n = 0; n < nonZero; n++)
            {
                f[random.Next(64)] = random.Next(-range, range);
            }

            int[] block = (int[])f.Clone();
            Ps2Ipu.Idct(block);
            double[] reference = IpuReference.Idct(f);
            for (int i = 0; i < 64; i++)
            {
                int expected = Math.Clamp((int)Math.Round(reference[i], MidpointRounding.AwayFromZero), -256, 255);
                worst = Math.Max(worst, Math.Abs(block[i] - expected));
            }
        }

        worst.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void IdctOfADcOnlyBlockIsFlat()
    {
        var block = new int[64];
        block[0] = 800;
        Ps2Ipu.Idct(block);
        block.Should().AllBeEquivalentTo(100);
    }

    // ---- BDEC ----

    private static short[][] SampleIntraBlocks() =>
    [
        Levels((0, 120), (1, 3), (2, -2), (5, 1)),
        Levels((0, 118), (1, -1), (3, 2), (9, 20)),
        Levels((0, 130), (4, 4)),
        Levels((0, 125), (1, 5), (2, 1), (3, -1), (40, 1)),
        Levels((0, 100), (1, -3)),
        Levels((0, 150), (2, 2), (63, -1)),
    ];

    private static IpuBitWriter EncodeMacroblock(IpuBitWriter w, short[][] blocks, bool intra, int[] predictors,
        int cbp = 0x3F, bool tableB15 = false, bool mpeg1 = false)
    {
        for (int b = 0; b < 6; b++)
        {
            if ((cbp & (0x20 >> b)) == 0)
            {
                continue;
            }

            int component = b < 4 ? 0 : b - 3;
            IpuMpegEncoder.Block(w, blocks[b], intra, component, ref predictors[component], tableB15, mpeg1);
        }

        return w;
    }

    private static IpuBitWriter StartCode(IpuBitWriter w) => w.AlignWithZeros().PutBytes(0, 0, 1, 0xB3);

    [Fact]
    public void BdecDecodesAnMpeg1IntraMacroblockToRaw16()
    {
        var (ipu, intc) = Create(Mp1);
        short[][] blocks = SampleIntraBlocks();
        var w = EncodeMacroblock(new IpuBitWriter(), blocks, intra: true, predictors: [128, 128, 128], mpeg1: true);
        StartCode(w);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Bdec(qsc: 3, intra: true));
        var output = Run(ipu, w.ToQuadwords());

        output.Should().HaveCount(48, "RAW16 is 384 halfwords");
        int[] expected = IpuReference.Assemble(
            [.. blocks.Select(b => IpuReference.DecodeBlock(b, true, 3, IpuReference.DefaultIntraScanOrder, mpeg1: true))]);
        short[] actual = ReadRaw16(output);
        for (int i = 0; i < 384; i++)
        {
            actual[i].Should().BeInRange((short)(expected[i] - 1), (short)(expected[i] + 1), $"sample {i}");
        }

        uint ctrl = ipu.ReadRegister32(Ps2Ipu.RegCtrl);
        ((ctrl >> 8) & 0x3F).Should().Be(0x3F, "CBP of an intra macroblock");
        (ctrl & Scd).Should().Be(Scd, "the start code after the macroblock");
        (ctrl & Ecd).Should().Be(0);
        ((uint)ipu.ReadRegister(Ps2Ipu.RegTop)).Should().Be(0x000001B3u);
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void BdecDecodesAnMpeg2NonIntraFieldMacroblockWithItsCodedBlockPattern()
    {
        var (ipu, _) = Create(As);
        short[][] blocks =
        [
            Levels((0, 1), (1, -2), (6, 3)),
            Levels((0, -1), (2, 1)),
            Levels((3, 5), (4, -1)),
            Levels((0, 2), (20, -30)),
            Levels(), Levels(),
        ];

        // coded_block_pattern 60 ("111"): the four luma blocks.
        var w = new IpuBitWriter().Put("111");
        EncodeMacroblock(w, blocks, intra: false, predictors: [0, 0, 0], cbp: 60);
        StartCode(w);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Bdec(qsc: 5, intra: false, field: true));
        var output = Run(ipu, w.ToQuadwords());

        int[] flat = Enumerable.Repeat(16, 64).ToArray();
        int[] expected = IpuReference.Assemble(
        [
            .. blocks.Select((b, i) => i < 4
                ? IpuReference.DecodeBlock(b, false, 5, flat, alternate: true)
                : new int[64]),
        ], field: true);
        short[] actual = ReadRaw16(output);
        for (int i = 0; i < 384; i++)
        {
            actual[i].Should().BeInRange((short)(expected[i] - 1), (short)(expected[i] + 1), $"sample {i}");
        }

        ((ipu.ReadRegister32(Ps2Ipu.RegCtrl) >> 8) & 0x3F).Should().Be(60);
    }

    [Fact]
    public void BdecUsesTableB15TheNonLinearScaleAndAMatrixFromSetiq()
    {
        var (ipu, _) = Create(Ivf | Qst | Idp1);

        // SETIQ (intra): a ramp matrix, in bitstream order.
        int[] matrix = [.. Enumerable.Range(0, 64).Select(i => 8 + i)];
        var setiq = new IpuBitWriter();
        foreach (int m in matrix)
        {
            setiq.Put((uint)m, 8);
        }

        Feed(ipu, setiq.ToQuadwords());
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetiq));
        ipu.Busy.Should().BeFalse();

        // With IDP = 1 (9-bit DC) the predictors reset to 256 and DC multiplies by 4.
        short[][] blocks =
        [
            Levels((0, 240), (1, 3), (2, -2)),
            Levels((0, 250), (1, 1), (4, 2)),
            Levels((0, 260), (3, -1)),
            Levels((0, 255), (1, 8)),
            Levels((0, 256)),
            Levels((0, 270), (2, 1), (12, -4)),
        ];
        var w = EncodeMacroblock(new IpuBitWriter(), blocks, intra: true, predictors: [256, 256, 256], tableB15: true);
        StartCode(w);
        ipu.WriteRegister(Ps2Ipu.RegCmd, Bdec(qsc: 12, intra: true));
        var output = Run(ipu, w.ToQuadwords());

        int[] expected = IpuReference.Assemble(
            [.. blocks.Select(b => IpuReference.DecodeBlock(b, true, 12, matrix, dcPrecision: 1, nonLinear: true))]);
        short[] actual = ReadRaw16(output);
        for (int i = 0; i < 384; i++)
        {
            actual[i].Should().BeInRange((short)(expected[i] - 1), (short)(expected[i] + 1), $"sample {i}");
        }
    }

    [Fact]
    public void BdecKeepsDcPredictionAcrossMacroblocksUnlessDcrIsSet()
    {
        var (ipu, _) = Create();
        short[][] first = [.. Enumerable.Range(0, 6).Select(_ => Levels((0, 140)))];
        short[][] second = [.. Enumerable.Range(0, 6).Select(_ => Levels((0, 90)))];
        int[] predictors = [128, 128, 128];
        var w = EncodeMacroblock(new IpuBitWriter(), first, true, predictors);
        EncodeMacroblock(w, second, true, predictors);
        StartCode(w);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Bdec(qsc: 1, intra: true));
        var output = Run(ipu, w.ToQuadwords());
        ReadRaw16(output).Should().AllBeEquivalentTo((short)140);
        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) & Scd).Should().Be(0, "macroblock data follows, not a start code");

        // The next BDEC starts where the last one stopped.
        ipu.WriteRegister(Ps2Ipu.RegCmd, Bdec(qsc: 1, intra: true, dcr: false));
        output = Run(ipu, []);
        ReadRaw16(output).Should().AllBeEquivalentTo((short)90, "the second macroblock's DC differentials continue from the first");
    }

    // ---- IDEC ----

    private static IpuBitWriter IdecSlice(short[][] mb1, short[][] mb2, int qsc2)
    {
        int[] predictors = [128, 128, 128];
        var w = new IpuBitWriter().Put("1");               // macroblock_type: intra
        EncodeMacroblock(w, mb1, true, predictors);
        w.Put("1");                                        // macroblock_address_increment 1
        w.Put("01").Put((uint)qsc2, 5);                    // macroblock_type: intra + quant
        EncodeMacroblock(w, mb2, true, predictors);
        return StartCode(w);
    }

    [Fact]
    public void IdecDecodesASliceToRgb32UntilTheStartCode()
    {
        var (ipu, intc) = Create();
        short[][] mb1 = SampleIntraBlocks();
        short[][] mb2 =
        [
            Levels((0, 60), (1, 2)), Levels((0, 70), (2, -1)), Levels((0, 80)), Levels((0, 90), (5, 3)),
            Levels((0, 110), (1, 2)), Levels((0, 160), (1, -2)),
        ];
        var w = IdecSlice(mb1, mb2, qsc2: 7);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        var output = Run(ipu, w.ToQuadwords());

        output.Should().HaveCount(128, "two RGB32 macroblocks");
        byte[] rgba = IpuBitWriter.Bytes(output);
        int[][] ycc =
        [
            IpuReference.Assemble([.. mb1.Select(b => IpuReference.DecodeBlock(b, true, 3, IpuReference.DefaultIntraScanOrder))]),
            IpuReference.Assemble([.. mb2.Select(b => IpuReference.DecodeBlock(b, true, 7, IpuReference.DefaultIntraScanOrder))]),
        ];
        for (int m = 0; m < 2; m++)
        {
            for (int p = 0; p < 256; p++)
            {
                int y = ycc[m][p];
                int c = 256 + (p / 32) * 8 + (p % 16) / 2;
                var (r, g, b) = IpuReference.Rgb(y, ycc[m][c], ycc[m][c + 64]);
                int o = m * 1024 + p * 4;
                ((int)rgba[o]).Should().BeCloseTo((int)Math.Clamp(r, 0, 255), 2, $"R of macroblock {m} pixel {p}");
                ((int)rgba[o + 1]).Should().BeCloseTo((int)Math.Clamp(g, 0, 255), 2);
                ((int)rgba[o + 2]).Should().BeCloseTo((int)Math.Clamp(b, 0, 255), 2);
                rgba[o + 3].Should().Be(0x80);
            }
        }

        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) & Scd).Should().Be(Scd);
        ((uint)ipu.ReadRegister(Ps2Ipu.RegTop)).Should().Be(0x000001B3u);
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    private static short[][] Flat(int y, int cb, int cr) =>
    [
        Levels((0, y)), Levels((0, y)), Levels((0, y)), Levels((0, y)), Levels((0, cb)), Levels((0, cr)),
    ];

    [Fact]
    public void IdecRgb16DithersAndSetsTheAlphaBitOfTranslucentPixels()
    {
        var (ipu, _) = Create();
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetth, 0u | (99u << 16)));

        // Y = 100, Cb = Cr = 128 decodes exactly; the IPU's CSC gives R = G = B = 98, below TH1.
        var w = new IpuBitWriter().Put("1");
        EncodeMacroblock(w, Flat(100, 128, 128), true, [128, 128, 128]);
        StartCode(w);
        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 1, rgb16: true, dither: true));
        byte[] bytes = IpuBitWriter.Bytes(Run(ipu, w.ToQuadwords()));

        bytes.Should().HaveCount(512);
        int[,] dither = { { -4, 0, -3, 1 }, { 2, -2, 3, -1 }, { -3, 1, -4, 0 }, { 3, -1, 2, -2 } };
        for (int p = 0; p < 256; p++)
        {
            int c = (98 + dither[(p / 16) & 3, p & 3]) >> 3;
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p * 2))
                .Should().Be((ushort)(c | (c << 5) | (c << 10) | 0x8000), $"pixel {p}");
        }
    }

    [Fact]
    public void IdecSgnSubtracts128AndTh0ClearsDarkPixels()
    {
        var (ipu, _) = Create();
        var w = new IpuBitWriter().Put("1");
        EncodeMacroblock(w, Flat(100, 128, 128), true, [128, 128, 128]);
        StartCode(w);
        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 1, sgn: true));
        byte[] bytes = IpuBitWriter.Bytes(Run(ipu, w.ToQuadwords()));
        bytes.Take(4).Should().Equal(98 ^ 0x80, 98 ^ 0x80, 98 ^ 0x80, 0x80);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetth, 99u | (120u << 16)));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 1));
        bytes = IpuBitWriter.Bytes(Run(ipu, w.ToQuadwords()));
        bytes.Should().AllBeEquivalentTo((byte)0, "every pixel is below TH0");
    }

    // ---- CSC / PACK / SETVQ ----

    [Fact]
    public void CscConvertsMacroblocksOfYcbcr()
    {
        var (ipu, intc) = Create();
        var ycc = new byte[768];
        for (int m = 0; m < 2; m++)
        {
            for (int i = 0; i < 256; i++)
            {
                ycc[m * 384 + i] = (byte)(16 + (i * 219 / 255));
            }

            for (int i = 0; i < 64; i++)
            {
                ycc[m * 384 + 256 + i] = (byte)(m == 0 ? 128 : 16 + i * 3);
                ycc[m * 384 + 320 + i] = (byte)(m == 0 ? 128 : 240 - i * 3);
            }
        }

        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdCsc, 2));
        byte[] rgba = IpuBitWriter.Bytes(Run(ipu, IpuBitWriter.Quadwords(ycc)));

        rgba.Should().HaveCount(2048);
        rgba.Take(4).Should().Equal(0, 0, 0, 0x80);
        rgba.Skip(1020).Take(4).Should().Equal(255, 255, 255, 0x80);
        for (int m = 0; m < 2; m++)
        {
            for (int p = 0; p < 256; p++)
            {
                int c = m * 384 + 256 + (p / 32) * 8 + (p % 16) / 2;
                var (r, g, b) = IpuReference.Rgb(ycc[m * 384 + p], ycc[c], ycc[c + 64]);
                int o = m * 1024 + p * 4;
                ((int)rgba[o]).Should().BeCloseTo((int)Math.Clamp(r, 0, 255), 2);
                ((int)rgba[o + 1]).Should().BeCloseTo((int)Math.Clamp(g, 0, 255), 2);
                ((int)rgba[o + 2]).Should().BeCloseTo((int)Math.Clamp(b, 0, 255), 2);
            }
        }

        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void CscToRgb16TruncatesWithoutDither()
    {
        var (ipu, _) = Create();
        var ycc = new byte[384];
        ycc.AsSpan(0, 256).Fill(235);
        ycc.AsSpan(256).Fill(128);
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdCsc, 1 | (1u << 27)));
        byte[] rgb16 = IpuBitWriter.Bytes(Run(ipu, IpuBitWriter.Quadwords(ycc)));
        rgb16.Should().HaveCount(512);
        Enumerable.Range(0, 256).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(rgb16.AsSpan(i * 2)))
            .Should().AllBeEquivalentTo((ushort)0x7FFF, "white, opaque (alpha 0x80 gives A = 0)");
    }

    [Fact]
    public void PackConvertsRgb32ToRgb16AndThroughTheVqClutToIndx4()
    {
        var (ipu, _) = Create();

        // SETVQ: 16 RGB16 entries, little-endian halfwords in the stream.
        var clut = new ushort[16];
        for (int i = 0; i < 16; i++)
        {
            clut[i] = (ushort)((i * 2) | ((31 - i * 2) << 5) | ((i % 4) * 8 << 10));
        }

        var clutBytes = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(clutBytes.AsSpan(i * 2), clut[i]);
        }

        Feed(ipu, IpuBitWriter.Quadwords(clutBytes));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetvq));
        ipu.Busy.Should().BeFalse();

        // Each pixel is CLUT colour (p * 7) % 16 in 8-bit form, with a little noise below the 5-bit step.
        var rgba = new byte[1024];
        for (int p = 0; p < 256; p++)
        {
            int k = p * 7 % 16;
            rgba[p * 4] = (byte)(((clut[k] & 31) << 3) | (p & 7));
            rgba[p * 4 + 1] = (byte)((((clut[k] >> 5) & 31) << 3) | 3);
            rgba[p * 4 + 2] = (byte)(((clut[k] >> 10) & 31) << 3);
            rgba[p * 4 + 3] = (byte)(p % 3 == 0 ? 0x40 : 0x80);
        }

        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdPack, 1 | (1u << 27)));
        byte[] rgb16 = IpuBitWriter.Bytes(Run(ipu, IpuBitWriter.Quadwords(rgba)));
        rgb16.Should().HaveCount(512);
        for (int p = 0; p < 256; p++)
        {
            ushort expected = (ushort)(clut[p * 7 % 16] | (p % 3 == 0 ? 0x8000 : 0));
            BinaryPrimitives.ReadUInt16LittleEndian(rgb16.AsSpan(p * 2)).Should().Be(expected, $"pixel {p}");
        }

        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdPack, 1));
        byte[] indx4 = IpuBitWriter.Bytes(Run(ipu, IpuBitWriter.Quadwords(rgba)));
        indx4.Should().HaveCount(128);
        for (int p = 0; p < 256; p++)
        {
            int index = (indx4[p / 2] >> ((p & 1) * 4)) & 15;
            index.Should().Be(p * 7 % 16, $"pixel {p}");
        }
    }

    // ---- Stalls, FIFOs, registers ----

    [Fact]
    public void IdecStallsOnInputStarvationAndOutputBackPressureThenResumes()
    {
        short[][] mb1 = SampleIntraBlocks();
        short[][] mb2 = Flat(77, 100, 150);
        UInt128[] input = IdecSlice(mb1, mb2, 4).ToQuadwords();

        var (reference, _) = Create();
        reference.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        List<UInt128> expected = Run(reference, input);

        // One quadword at a time, draining the output between.
        var (ipu, intc) = Create();
        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        List<UInt128> trickled = [];
        var buffer = new UInt128[8];
        for (int i = 0; i < input.Length; i++)
        {
            ipu.Busy.Should().BeTrue($"the slice is not complete after {i} quadwords");
            (intc.Stat & IpuIrq).Should().Be(0);
            ipu.WriteIn(input.AsSpan(i, 1)).Should().Be(1);
            int n;
            while ((n = ipu.ReadOut(buffer)) > 0)
            {
                trickled.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        }

        ipu.Busy.Should().BeFalse();
        trickled.Should().Equal(expected);
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);

        // All the input at once with nothing draining the output: the FIFO fills and the IPU waits.
        var (blocked, _) = Create();
        blocked.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        int taken = blocked.WriteIn(input);
        blocked.Busy.Should().BeTrue("the output FIFO is full");
        blocked.OutputCount.Should().Be(Ps2Ipu.FifoDepth);
        uint ctrl = blocked.ReadRegister32(Ps2Ipu.RegCtrl);
        ((ctrl >> 4) & 15).Should().Be(8u, "OFC");
        (ctrl & 15).Should().Be((uint)blocked.InputCount, "IFC");

        List<UInt128> drained = [];
        for (int guard = 0; guard < 1000 && (blocked.Busy || blocked.OutputCount > 0); guard++)
        {
            if (blocked.OutputCount > 0)
            {
                drained.Add(blocked.ReadFifo());
            }

            if (taken < input.Length)
            {
                taken += blocked.WriteIn(input.AsSpan(taken));
            }
        }

        drained.Should().Equal(expected);
    }

    [Fact]
    public void SaveStateMidCommandResumesIdentically()
    {
        UInt128[] input = IdecSlice(SampleIntraBlocks(), Flat(60, 90, 200), 9).ToQuadwords();
        var (ipu, _) = Create();
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetth, 20u | (40u << 16)));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3, rgb16: true, dither: true));

        // Part of the input (not the start code at its end), with part of the output read.
        int half = input.Length / 2;
        ipu.WriteIn(input.AsSpan(0, half)).Should().Be(half);
        ipu.ReadOut(new UInt128[3]);
        ipu.Busy.Should().BeTrue();

        var writer = new StateWriter();
        ipu.SaveState(writer);
        var (copy, copyIntc) = Create();
        copy.LoadState(new StateReader(writer.ToArray()));

        copy.Busy.Should().BeTrue();
        copy.ReadRegister(Ps2Ipu.RegBp).Should().Be(ipu.ReadRegister(Ps2Ipu.RegBp));
        copy.ReadRegister32(Ps2Ipu.RegCtrl).Should().Be(ipu.ReadRegister32(Ps2Ipu.RegCtrl));

        UInt128[] rest = input[half..];
        Run(copy, rest).Should().Equal(Run(ipu, rest));
        copy.Busy.Should().BeFalse();
        (copyIntc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void BclrEmptiesTheFifoAndSetsTheBitPointer()
    {
        var (ipu, intc) = Create();
        Feed(ipu, new IpuBitWriter().Put(0x01234567, 32).ToQuadwords(3));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdFdec, 8));
        ipu.ReadRegister(Ps2Ipu.RegBp).Should().Be(8u | (3u << 8) | (1u << 16), "BP 8, three quadwords in the FIFO, one in the buffer");

        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdBclr, 16));
        ipu.ReadRegister(Ps2Ipu.RegBp).Should().Be(16u);
        ipu.InputCount.Should().Be(0);
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);

        Feed(ipu, new IpuBitWriter().Put(0xAABBCCDD, 32).Put(0x11223344, 32).ToQuadwords());
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdFdec));
        ((uint)ipu.ReadRegister(Ps2Ipu.RegCmd)).Should().Be(0xCCDD1122u, "the stream restarts at bit 16");
    }

    [Fact]
    public void IpuCmdShowsTheNextBitsBeforeAnyDecodeAndTopIsBusyUntil32BitsArrive()
    {
        var (ipu, _) = Create();
        (ipu.ReadRegister(Ps2Ipu.RegTop) >> 63).Should().Be(1, "fewer than 32 bits are in the FIFO");

        ipu.WriteFifo(new IpuBitWriter().Put(0xDEADBEEF, 32).ToQuadwords()[0]);
        ipu.ReadRegister(Ps2Ipu.RegCmd).Should().Be(0xDEADBEEFul);
        ipu.ReadRegister32(Ps2Ipu.RegCmd + 4).Should().Be(0u);
        ipu.ReadRegister(Ps2Ipu.RegTop).Should().Be(0xDEADBEEFul);
        ipu.ReadRegister(Ps2Ipu.RegBp).Should().Be(1u << 8, "peeking does not consume");
    }

    [Fact]
    public void CtrlKeepsTheModeBitsAndResetClearsTheIpu()
    {
        var (ipu, intc) = Create();
        ipu.WriteRegister32(Ps2Ipu.RegCtrl, 0xFFFF_FFFF & ~(1u << 30));
        (ipu.ReadRegister32(Ps2Ipu.RegCtrl) & 0x7FFF_0000).Should().Be(0x07F30000u);

        // RST while idle: no interrupt.
        Feed(ipu, new UInt128[2]);
        ipu.WriteRegister32(Ps2Ipu.RegCtrl, 1u << 30);
        ipu.InputCount.Should().Be(0);
        (intc.Stat & IpuIrq).Should().Be(0);

        // RST during a command ends it and raises the interrupt.
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdFdec));
        ipu.Busy.Should().BeTrue();
        ipu.WriteRegister32(Ps2Ipu.RegCtrl, 1u << 30);
        ipu.Busy.Should().BeFalse();
        (intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void TheDmacFeedsAndDrainsTheIpuThroughChannels4And3()
    {
        var rig = new Ps2DmaRig();
        var ipu = new Ps2Ipu(rig.Intc);
        rig.Dmac.Ipu = ipu;

        short[][] mb1 = SampleIntraBlocks();
        UInt128[] input = IdecSlice(mb1, Flat(90, 120, 130), 5).ToQuadwords();
        var (reference, _) = Create();
        reference.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        byte[] expected = IpuBitWriter.Bytes(Run(reference, input));

        const uint source = 0x10000;
        const uint destination = 0x20000;
        byte[] inputBytes = IpuBitWriter.Bytes(input);
        inputBytes.CopyTo(rig.Ram, (int)source);

        ipu.WriteRegister(Ps2Ipu.RegCmd, Idec(qsc: 3));
        uint to = Ps2DmaRig.ChannelBase(Ps2Dmac.IpuToChannel);
        rig.SetReg(to + 0x10, source);
        rig.SetReg(to + 0x20, (uint)input.Length);
        rig.SetChcr(Ps2Dmac.IpuToChannel, 0x101);
        uint from = Ps2DmaRig.ChannelBase(Ps2Dmac.IpuFromChannel);
        rig.SetReg(from + 0x10, destination);
        rig.SetReg(from + 0x20, (uint)(expected.Length / 16));
        rig.SetChcr(Ps2Dmac.IpuFromChannel, 0x100);

        rig.RunUntilDone(Ps2Dmac.IpuFromChannel).Should().BeTrue();
        rig.RunUntilDone(Ps2Dmac.IpuToChannel).Should().BeTrue();
        ipu.Busy.Should().BeFalse();
        rig.Ram.AsSpan((int)destination, expected.Length).ToArray().Should().Equal(expected);
        (rig.Intc.Stat & IpuIrq).Should().Be(IpuIrq);
    }

    [Fact]
    public void IpuRoundTripsThroughASaveStateWhenIdle()
    {
        var (ipu, _) = Create(Mp1 | Pct(2));
        var setiq = new IpuBitWriter();
        for (int i = 0; i < 64; i++)
        {
            setiq.Put((uint)(i + 1), 8);
        }

        Feed(ipu, setiq.ToQuadwords());
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetiq, 1u << 27));
        ipu.WriteRegister(Ps2Ipu.RegCmd, Command(Ps2Ipu.CmdSetth, 5u | (9u << 16)));
        Feed(ipu, new IpuBitWriter().Put(0x13572468, 32).ToQuadwords());

        var writer = new StateWriter();
        ipu.SaveState(writer);
        var (copy, _) = Create();
        copy.LoadState(new StateReader(writer.ToArray()));
        var again = new StateWriter();
        copy.SaveState(again);
        again.ToArray().Should().Equal(writer.ToArray());
        copy.ReadRegister(Ps2Ipu.RegTop).Should().Be(0x13572468ul);
    }
}
