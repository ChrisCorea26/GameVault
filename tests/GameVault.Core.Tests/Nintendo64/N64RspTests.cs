using System.Text.Json;
using FluentAssertions;
using GameVault.Core.Nintendo64;
using GameVault.Core.State;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>The RSP: its scalar unit, SP registers, DMA and the vector unit.</summary>
public sealed class N64RspTests
{
    private const uint StartRsp = 0xAD; // clear halt, broke, interrupt, single step and interrupt-on-break (PeterLemon's StartSP)

    // ---- PeterLemon's RSPTest/CP2 programs, as recorded results ------------------------------------------------

    /// <summary>
    /// The RSP side of PeterLemon's <c>RSPTest/CP2</c> programs (public domain, see tests/TestRoms/SOURCES.md):
    /// each test loads two quads, runs one vector instruction and stores vd, the accumulator and the flags, which the
    /// program compares with the results recorded on hardware. The fixture holds the RSP code taken from each ROM and
    /// those inputs and results; the tests of a program run in order on one RSP, as on the console.
    /// </summary>
    public static TheoryData<string> PeterLemonPrograms()
    {
        var data = new TheoryData<string>();
        foreach (var program in LoadFixture().EnumerateObject())
        {
            data.Add(program.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PeterLemonPrograms))]
    public void PeterLemon_vector_programs_match_the_hardware_results(string program)
    {
        var fixture = LoadFixture().GetProperty(program);
        var codes = fixture.GetProperty("codes").EnumerateArray()
            .Select(c => c.GetString()!.Split(' ').Select(w => Convert.ToUInt32(w, 16)).ToArray()).ToArray();
        var (rsp, _) = Create();

        var index = 0;
        foreach (var test in fixture.GetProperty("tests").EnumerateArray())
        {
            Load(rsp, codes[test.GetProperty("code").GetInt32()]);
            Convert.FromHexString(test.GetProperty("dmem0").GetString()!).CopyTo(rsp.Dmem, 0);
            Convert.FromHexString(test.GetProperty("dmem16").GetString()!).CopyTo(rsp.Dmem, 16);
            rsp.WriteRegister(N64Rsp.SpPc, 0);
            rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
            rsp.Run(1000);
            rsp.Halted.Should().BeTrue();

            var because = $"{program} test {(char)('A' + index)}";
            Convert.ToHexString(rsp.Dmem, 0, 16).Should().BeEquivalentTo(test.GetProperty("vd").GetString(), because + " (vd)");
            if (test.TryGetProperty("acc", out var acc))
            {
                Convert.ToHexString(rsp.Dmem, 16, 48).Should().BeEquivalentTo(acc.GetString(), because + " (accumulator)");
            }

            if (test.TryGetProperty("flags", out var flags))
            {
                Convert.ToHexString(rsp.Dmem, 64, 4).Should().BeEquivalentTo(flags.GetString(), because + " (VCO, VCC)");
            }

            if (test.TryGetProperty("vce", out var vce))
            {
                Convert.ToHexString(rsp.Dmem, 68, 1).Should().BeEquivalentTo(vce.GetString(), because + " (VCE)");
            }

            index++;
        }
    }

    private static JsonElement LoadFixture()
    {
        var path = Path.Combine(TestRomLocator.RepoRoot, "tests", "GameVault.Core.Tests", "Nintendo64", "Fixtures", "rsp-vector-tests.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    // ---- Scalar unit ---------------------------------------------------------------------------------------------

    [Fact]
    public void Scalar_arithmetic_and_logic()
    {
        var (rsp, _) = Run(
            Asm.Ori(1, 0, 0x1234),
            Asm.Lui(2, 0x8000),
            Asm.Addiu(3, 1, -0x235), // 0x0FFF
            Asm.Special(1, 2, 4, 0, 0x20), // ADD r4 = r1 + r2 (no overflow trap on the RSP)
            Asm.Special(1, 3, 5, 0, 0x23), // SUBU r5 = r1 - r3
            Asm.Special(0, 2, 6, 4, 0x03), // SRA r6 = r2 >> 4
            Asm.Special(0, 2, 7, 4, 0x02), // SRL r7 = r2 >> 4
            Asm.Special(2, 1, 8, 0, 0x2A), // SLT r8 = r2 < r1 (signed)
            Asm.Special(2, 1, 9, 0, 0x2B), // SLTU r9 = r2 < r1
            Asm.Special(1, 3, 10, 0, 0x27), // NOR
            Asm.Itype(0x0A, 2, 11, 5), // SLTI r11 = r2 < 5
            Asm.Ori(0, 0, 0x55), // writes to r0 are dropped
            Asm.Break());

        rsp.Gpr(4).Should().Be(0x80001234);
        rsp.Gpr(5).Should().Be(0x0235);
        rsp.Gpr(6).Should().Be(0xF8000000);
        rsp.Gpr(7).Should().Be(0x08000000);
        rsp.Gpr(8).Should().Be(1);
        rsp.Gpr(9).Should().Be(0);
        rsp.Gpr(10).Should().Be(~(0x1234u | 0x0FFF));
        rsp.Gpr(11).Should().Be(1);
        rsp.Gpr(0).Should().Be(0);
    }

    [Fact]
    public void Branches_run_their_delay_slot_and_link_within_imem()
    {
        var (rsp, _) = Run(
            Asm.Itype(0x04, 0, 0, 3), // BEQ r0, r0, +3 → 0x010
            Asm.Ori(1, 0, 1), // delay slot runs
            Asm.Ori(2, 0, 2), // skipped
            Asm.Ori(3, 0, 3), // skipped
            Asm.Jal(0x20), // 0x010: JAL 0x020
            Asm.Ori(4, 0, 4), // delay slot
            Asm.Break(),
            Asm.Nop(),
            Asm.Ori(5, 0, 5), // 0x020
            Asm.Break());

        rsp.Gpr(1).Should().Be(1);
        rsp.Gpr(2).Should().Be(0);
        rsp.Gpr(3).Should().Be(0);
        rsp.Gpr(4).Should().Be(4);
        rsp.Gpr(5).Should().Be(5);
        rsp.Gpr(31).Should().Be(0x18);
    }

    [Fact]
    public void The_program_counter_wraps_within_imem()
    {
        var (rsp, _) = Create();
        Load(rsp, [Asm.Ori(1, 0, 7), Asm.Break()]);
        WriteWord(rsp.Imem, 0xFFC, Asm.Ori(2, 0, 9));
        rsp.WriteRegister(N64Rsp.SpPc, 0xFFC);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        rsp.Gpr(2).Should().Be(9);
        rsp.Gpr(1).Should().Be(7);
        rsp.ReadRegister(N64Rsp.SpPc).Should().Be(8);
    }

    [Fact]
    public void Loads_and_stores_wrap_around_dmem()
    {
        var (rsp, _) = Run(
            Asm.Lui(1, 0x1122),
            Asm.Ori(1, 1, 0x3344),
            Asm.Itype(0x2B, 0, 1, 0xFFE), // SW r1, 0xFFE(r0): bytes FFE, FFF, 000, 001
            Asm.Itype(0x23, 0, 2, 0xFFE), // LW
            Asm.Itype(0x21, 0, 3, 0xFFF), // LH (unaligned)
            Asm.Itype(0x20, 0, 4, 0xFFF), // LB
            Asm.Break());

        rsp.Dmem[0xFFE].Should().Be(0x11);
        rsp.Dmem[0x001].Should().Be(0x44);
        rsp.Gpr(2).Should().Be(0x11223344);
        rsp.Gpr(3).Should().Be(0x2233);
        rsp.Gpr(4).Should().Be(0x22);
    }

    [Fact]
    public void Break_halts_and_raises_the_sp_interrupt_only_with_interrupt_on_break()
    {
        var (rsp, host) = Create();
        Load(rsp, [Asm.Break()]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(10);
        (rsp.Status & (N64Rsp.StatusBroke | N64Rsp.StatusHalt)).Should().Be(N64Rsp.StatusBroke | N64Rsp.StatusHalt);
        host.Interrupts.Should().Be(N64Interrupt.None);

        rsp.WriteRegister(N64Rsp.SpPc, 0);
        rsp.WriteRegister(N64Rsp.SpStatus, (StartRsp & ~(1u << 7)) | 1 << 8); // start, setting interrupt-on-break
        (rsp.Status & N64Rsp.StatusBroke).Should().Be(0);
        rsp.Run(10);
        host.Interrupts.Should().Be(N64Interrupt.Sp);

        rsp.WriteRegister(N64Rsp.SpStatus, 1 << 3); // clear the interrupt
        host.Interrupts.Should().Be(N64Interrupt.None);
    }

    [Fact]
    public void Status_signals_single_step_and_the_semaphore()
    {
        var (rsp, _) = Create();
        rsp.WriteRegister(N64Rsp.SpStatus, 1 << 10 | 1 << 24); // set signals 0 and 7
        (rsp.Status >> 7).Should().Be(0x81);
        rsp.WriteRegister(N64Rsp.SpStatus, 1 << 9 | 1 << 24 | 1 << 23); // clear 0; set and clear 7 at once does nothing
        (rsp.Status >> 7).Should().Be(0x80);

        rsp.ReadRegister(N64Rsp.SpSemaphore).Should().Be(0);
        rsp.ReadRegister(N64Rsp.SpSemaphore).Should().Be(1);
        rsp.WriteRegister(N64Rsp.SpSemaphore, 0);
        rsp.ReadRegister(N64Rsp.SpSemaphore).Should().Be(0);

        Load(rsp, [Asm.Ori(1, 0, 1), Asm.Ori(2, 0, 2), Asm.Break()]);
        rsp.WriteRegister(N64Rsp.SpStatus, 1 << 0 | 1 << 6); // run with single step
        rsp.Run(100);
        rsp.Gpr(1).Should().Be(1);
        rsp.Gpr(2).Should().Be(0);
        rsp.Halted.Should().BeTrue();
    }

    [Fact]
    public void Cop0_reaches_the_sp_and_dp_registers()
    {
        var (rsp, _) = Create(out var dp);
        dp.Values[0x04100004] = 0x1234;
        Load(rsp,
        [
            Asm.Cop0(0x00, 1, 9), // MFC0 r1, DPC_END
            Asm.Ori(2, 0, 0x400),
            Asm.Cop0(0x04, 2, 8), // MTC0 r2, DPC_START
            Asm.Cop0(0x00, 3, 4), // MFC0 r3, SP_STATUS
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        rsp.Gpr(1).Should().Be(0x1234);
        dp.Values[0x04100000].Should().Be(0x400);
        rsp.Gpr(3).Should().Be(0); // running: not halted
    }

    // ---- DMA -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Dma_reads_rows_with_a_skip_and_leaves_the_registers_past_the_transfer()
    {
        var (rsp, host) = Create();
        for (var i = 0; i < 64; i++)
        {
            host.RdramBytes[0x1000 + i] = (byte)i;
        }

        rsp.WriteRegister(N64Rsp.SpDmaSpAddr, 0x0100);
        rsp.WriteRegister(N64Rsp.SpDmaRamAddr, 0x1000);
        rsp.WriteRegister(N64Rsp.SpDmaRdLen, 1 << 20 | 8 << 20 | 1 << 12 | 7); // 8 bytes, 2 rows, skip 8 (the low 3 bits of skip drop)

        rsp.Dmem.AsSpan(0x100, 16).ToArray().Should().Equal(0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23);
        rsp.ReadRegister(N64Rsp.SpDmaSpAddr).Should().Be(0x110);
        rsp.ReadRegister(N64Rsp.SpDmaRamAddr).Should().Be(0x1020);
        (rsp.ReadRegister(N64Rsp.SpDmaRdLen) & 0xFFFFF).Should().Be(0xFF8);
    }

    [Fact]
    public void Dma_writes_from_imem_and_rounds_lengths_up_to_eight_bytes()
    {
        var (rsp, host) = Create();
        for (var i = 0; i < 8; i++)
        {
            rsp.Imem[0xFF8 + i] = (byte)(0xA0 + i);
            rsp.Imem[i] = (byte)(0xB0 + i);
        }

        rsp.WriteRegister(N64Rsp.SpDmaSpAddr, 0x1FF8); // IMEM, last 8 bytes: the SP address wraps to IMEM's start
        rsp.WriteRegister(N64Rsp.SpDmaRamAddr, 0x2003); // the RDRAM address is aligned down
        rsp.WriteRegister(N64Rsp.SpDmaWrLen, 10); // rounds up to 16 bytes

        host.RdramBytes.AsSpan(0x2000, 16).ToArray().Should().Equal(
            0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7);
        rsp.ReadRegister(N64Rsp.SpDmaSpAddr).Should().Be(0x1008);
    }

    // ---- Vector unit ---------------------------------------------------------------------------------------------

    [Fact]
    public void Mtc2_mfc2_and_the_flag_registers()
    {
        var (rsp, _) = Run(
            Asm.Ori(1, 0, 0x8001),
            Asm.Cop2Move(0x04, 1, 2, 3), // MTC2 r1, v2[e3]: bytes 3 and 4
            Asm.Cop2Move(0x00, 3, 2, 3), // MFC2 r3, v2[e3] (sign-extended)
            Asm.Cop2Move(0x04, 1, 4, 15), // MTC2 at byte 15 writes only that byte
            Asm.Ori(5, 0, 0x81FF),
            Asm.Cop2Move(0x06, 5, 0, 0), // CTC2 r5, VCO
            Asm.Cop2Move(0x02, 6, 0, 0), // CFC2 r6, VCO
            Asm.Cop2Move(0x06, 5, 2, 0), // CTC2 r5, VCE (8 bits)
            Asm.Cop2Move(0x02, 7, 2, 0), // CFC2 r7, VCE
            Asm.Break());

        rsp.Vector(2, 1).Should().Be(0x0080);
        rsp.Vector(2, 2).Should().Be(0x0100);
        rsp.Gpr(3).Should().Be(0xFFFF8001);
        rsp.Vector(4, 7).Should().Be(0x0080);
        rsp.Gpr(6).Should().Be(0xFFFF81FF);
        rsp.Gpr(7).Should().Be(0xFF);
    }

    [Fact]
    public void Multiply_accumulate_with_element_selectors()
    {
        var (rsp, _) = Create();
        WriteLanes(rsp.Dmem, 0x00, 0x4000, 0x4000, 0x4000, 0x4000, -0x8000, -0x8000, 0x7FFF, 0x0001);
        WriteLanes(rsp.Dmem, 0x10, 0x4000, 0x2000, 0x1000, 0x0800, -0x8000, 0x7FFF, 0x7FFF, 0x0001);
        Load(rsp,
        [
            Asm.Lqv(1, 0, 0, 0),
            Asm.Lqv(2, 0, 0, 1),
            Asm.Vector(0x00, 3, 1, 2, 0), // VMULF v3 = v1 * v2
            Asm.Vector(0x08, 4, 1, 2, 9), // VMACF v4 = acc + v1 * v2[1] (0x2000)
            Asm.Vector(0x07, 5, 1, 2, 2), // VMUDH v5 = v1 * v2[0q]
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        Lanes(rsp, 3).Should().Equal(0x2000, 0x1000, 0x0800, 0x0400, 0x7FFF, 0x8001, 0x7FFE, 0x0000);
        Lanes(rsp, 4).Should().Equal(0x3000, 0x2000, 0x1800, 0x1400, 0x6000, 0x8000, 0x7FFF, 0x0000);
        Lanes(rsp, 5).Should().Equal(0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF);
        rsp.Accumulator(7).Should().Be(0x7FFFL << 16);
    }

    [Fact]
    public void Vch_and_vcl_clip_a_double_precision_value()
    {
        var (rsp, _) = Create();
        WriteLanes(rsp.Dmem, 0x00, 5, -5, 100, -100, 0, 3, -32768, 32767);
        WriteLanes(rsp.Dmem, 0x10, 10, 10, 10, 10, 10, -3, 1, -32768);
        Load(rsp,
        [
            Asm.Lqv(1, 0, 0, 0),
            Asm.Lqv(2, 0, 0, 1),
            Asm.Vector(0x25, 3, 1, 2, 0), // VCH
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        // Clamps each vs lane into [-|vt|, |vt|].
        Lanes(rsp, 3).Should().Equal(5, unchecked((ushort)-5), 10, unchecked((ushort)-10), 0, 3, 0xFFFF, 0x8000);
        rsp.Vco.Should().Be(0x5FEA);
        rsp.Vcc.Should().Be(0xA4E8);
        rsp.Vce.Should().Be(0x80);

        // VCL then refines with the low halves (here zero, against vt's low half 0): the flags from VCH steer it.
        Load(rsp, [Asm.Vector(0x24, 4, 0, 0, 0), Asm.Break()]);
        rsp.WriteRegister(N64Rsp.SpPc, 0);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);
        rsp.Vco.Should().Be(0);
        rsp.Vce.Should().Be(0);
    }

    [Fact]
    public void Reciprocal_tables_match_the_rom()
    {
        N64Rsp.Reciprocals.Take(8).Should().Equal(0xFFFF, 0xFF00, 0xFE01, 0xFD04, 0xFC07, 0xFB0C, 0xFA11, 0xF918);
        N64Rsp.Reciprocals[511].Should().Be(0x0040);
        // Even entries are for [2, 4) (1/sqrt(2) = 0x16A09 >> 17), odd ones for [1, 2).
        N64Rsp.InverseSquareRoots.Take(4).Should().Equal(0x6A09, 0xFFFF, 0x6955, 0xFF00);
    }

    [Fact]
    public void Vrcp_with_a_32_bit_input_through_vrcph_and_vrcpl()
    {
        var (rsp, _) = Create();
        WriteLanes(rsp.Dmem, 0x00, 0x0001, 0x0000, 4, 0, 0, 0, 0, 0);
        Load(rsp,
        [
            Asm.Lqv(1, 0, 0, 0),
            Asm.Vector(0x30, 2, 0, 1, 8 + 2), // VRCP v2[0], v1[2] = 1/4
            Asm.Vector(0x32, 2, 1, 1, 8 + 0), // VRCPH v2[1], v1[0]: high input 1, reads the high result
            Asm.Vector(0x31, 2, 2, 1, 8 + 1), // VRCPL v2[2], v1[1]: 1/0x00010000
            Asm.Vector(0x32, 2, 3, 1, 8 + 0), // VRCPH v2[3]: the high result
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        // 1/4 = 0x1FFFF000 (2^31/4 through the ROM's 0xFFFF for 1.0); 1/65536 = 0x00007FFF.
        rsp.Vector(2, 0).Should().Be(0xF000);
        rsp.Vector(2, 1).Should().Be(0x1FFF);
        rsp.Vector(2, 2).Should().Be(0x7FFF);
        rsp.Vector(2, 3).Should().Be(0x0000);
    }

    [Fact]
    public void Lwv_does_nothing_and_swv_stores_the_register_rotated()
    {
        var (rsp, _) = Create();
        WriteLanes(rsp.Dmem, 0x00, 1, 2, 3, 4, 5, 6, 7, 8);
        Load(rsp,
        [
            Asm.Lqv(1, 0, 0, 0),
            Asm.LoadStore(0x32, 0x0A, 1, 0, 2, 2), // LWV v1[e2], $20(r0): no effect
            Asm.LoadStore(0x3A, 0x0A, 1, 0, 2, 2), // SWV v1[e2], $20(r0)
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        Lanes(rsp, 1).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        ReadLanes(rsp.Dmem, 0x20).Should().Equal(2, 3, 4, 5, 6, 7, 8, 1);
    }

    /// <summary>LTV and STV, as PeterLemon's RSPTest LTV program records them on hardware.</summary>
    [Fact]
    public void Ltv_and_stv_load_and_store_rotated_diagonals()
    {
        static ushort Source(int row, int column) => (ushort)(row * 8 + column + 1);

        var (rsp, _) = Create();
        for (var row = 0; row < 8; row++)
        {
            WriteLanes(rsp.Dmem, row * 16, Enumerable.Range(0, 8).Select(c => (int)Source(row, c)).ToArray());
        }

        var program = new List<uint>();
        for (var i = 0; i < 8; i++)
        {
            program.Add(Asm.LoadStore(0x32, 0x0B, 0, 0, i * 2, i)); // LTV v0[e2i], $10*i(r0)
        }

        for (var i = 0; i < 8; i++)
        {
            program.Add(Asm.Lqv(8 + i, 0, 0, i)); // LQV v(8+i), $10*i(r0)
        }

        for (var i = 0; i < 8; i++)
        {
            program.Add(Asm.LoadStore(0x3A, 0x0B, 8, 0, i * 2, 8 + i)); // STV v8[e2i], $80+$10*i(r0)
        }

        program.Add(Asm.Break());
        Load(rsp, [.. program]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        for (var k = 0; k < 8; k++)
        {
            Lanes(rsp, k).Should().Equal(Enumerable.Range(0, 8).Select(j => Source((k - j + 8) % 8, k)), $"LTV fills v{k}");
            ReadLanes(rsp.Dmem, 0x80 + k * 16).Should().Equal(Enumerable.Range(0, 8).Select(j => Source((k + j) % 8, j)), $"STV writes row {k}");
        }
    }

    [Fact]
    public void Packed_loads_and_stores()
    {
        var (rsp, _) = Create();
        for (var i = 0; i < 16; i++)
        {
            rsp.Dmem[i] = (byte)(0x10 * (i & 7) + i);
        }

        Load(rsp,
        [
            Asm.LoadStore(0x32, 0x06, 1, 0, 0, 0), // LPV v1[e0], $00
            Asm.LoadStore(0x32, 0x07, 2, 0, 0, 0), // LUV v2[e0], $00
            Asm.LoadStore(0x3A, 0x06, 1, 0, 0, 4), // SPV v1[e0], $20
            Asm.LoadStore(0x3A, 0x07, 2, 0, 0, 5), // SUV v2[e0], $28
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(100);

        Lanes(rsp, 1).Should().Equal(Enumerable.Range(0, 8).Select(i => (ushort)(rsp.Dmem[i] << 8)));
        Lanes(rsp, 2).Should().Equal(Enumerable.Range(0, 8).Select(i => (ushort)(rsp.Dmem[i] << 7)));
        rsp.Dmem.AsSpan(0x20, 8).ToArray().Should().Equal(rsp.Dmem.AsSpan(0, 8).ToArray());
        rsp.Dmem.AsSpan(0x28, 8).ToArray().Should().Equal(rsp.Dmem.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public void Save_state_round_trip_continues_identically()
    {
        var (rsp, host) = Create();
        WriteLanes(rsp.Dmem, 0x00, 0x1234, -2, 300, 0x7FFF, 5, 6, 7, 8);
        Load(rsp,
        [
            Asm.Lqv(1, 0, 0, 0),
            Asm.Ori(2, 0, 50),
            Asm.Vector(0x0F, 3, 1, 1, 0), // loop: VMADH v3 += v1*v1
            Asm.Vector(0x08, 4, 3, 1, 3), // VMACF
            Asm.Addiu(2, 2, -1),
            Asm.Itype(0x05, 2, 0, -4), // BNE r2, r0, loop
            Asm.Nop(),
            Asm.LoadStore(0x3A, 0x04, 4, 0, 0, 2), // SQV v4, $20
            Asm.Break(),
        ]);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(37);

        var writer = new StateWriter();
        rsp.SaveState(writer);
        var snapshot = writer.ToArray();
        rsp.Run(1000);
        var expected = rsp.Dmem.ToArray();

        var (copy, _) = Create();
        copy.LoadState(new StateReader(snapshot));
        copy.Run(1000);
        copy.Dmem.Should().Equal(expected);
        copy.Halted.Should().BeTrue();
        host.Interrupts.Should().Be(N64Interrupt.None);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static (N64Rsp Rsp, FakeHost Host) Create() => Create(out _);

    private static (N64Rsp Rsp, FakeHost Host) Create(out FakeDp dp)
    {
        var host = new FakeHost();
        dp = new FakeDp();
        var rsp = new N64Rsp(host, dp);
        host.Rsp = rsp;
        return (rsp, host);
    }

    private static (N64Rsp Rsp, FakeHost Host) Run(params uint[] program)
    {
        var (rsp, host) = Create();
        Load(rsp, program);
        rsp.WriteRegister(N64Rsp.SpStatus, StartRsp);
        rsp.Run(10_000);
        rsp.Halted.Should().BeTrue();
        return (rsp, host);
    }

    private static void Load(N64Rsp rsp, uint[] program)
    {
        for (var i = 0; i < program.Length; i++)
        {
            WriteWord(rsp.Imem, i * 4, program[i]);
        }
    }

    private static void WriteWord(byte[] memory, int address, uint value)
    {
        memory[address] = (byte)(value >> 24);
        memory[address + 1] = (byte)(value >> 16);
        memory[address + 2] = (byte)(value >> 8);
        memory[address + 3] = (byte)value;
    }

    private static void WriteLanes(byte[] memory, int address, params int[] lanes)
    {
        for (var i = 0; i < lanes.Length; i++)
        {
            memory[address + i * 2] = (byte)(lanes[i] >> 8);
            memory[address + i * 2 + 1] = (byte)lanes[i];
        }
    }

    private static ushort[] ReadLanes(byte[] memory, int address) =>
        Enumerable.Range(0, 8).Select(i => (ushort)(memory[address + i * 2] << 8 | memory[address + i * 2 + 1])).ToArray();

    private static ushort[] Lanes(N64Rsp rsp, int register) =>
        Enumerable.Range(0, 8).Select(i => rsp.Vector(register, i)).ToArray();

    private sealed class FakeHost : IN64RcpHost
    {
        public byte[] RdramBytes { get; } = new byte[0x10000];

        public N64Rsp? Rsp { get; set; }

        public N64Interrupt Interrupts { get; private set; }

        public Span<byte> Rdram => RdramBytes;

        public Span<byte> Dmem => Rsp!.Dmem;

        public void RaiseInterrupt(N64Interrupt source) => Interrupts |= source;

        public void ClearInterrupt(N64Interrupt source) => Interrupts &= ~source;
    }

    private sealed class FakeDp : IN64DpRegisters
    {
        public Dictionary<uint, uint> Values { get; } = [];

        public uint ReadRegister(uint address) => Values.GetValueOrDefault(address);

        public void WriteRegister(uint address, uint value) => Values[address] = value;
    }

    /// <summary>A tiny RSP assembler for hand-written test programs.</summary>
    private static class Asm
    {
        public static uint Nop() => 0;

        public static uint Break() => 0x0000000D;

        public static uint Special(int rs, int rt, int rd, int sa, int funct) =>
            (uint)(rs << 21 | rt << 16 | rd << 11 | sa << 6 | funct);

        public static uint Itype(int op, int rs, int rt, int immediate) =>
            (uint)(op << 26 | rs << 21 | rt << 16 | (immediate & 0xFFFF));

        public static uint Ori(int rt, int rs, int immediate) => Itype(0x0D, rs, rt, immediate);

        public static uint Addiu(int rt, int rs, int immediate) => Itype(0x09, rs, rt, immediate);

        public static uint Lui(int rt, int immediate) => Itype(0x0F, 0, rt, immediate);

        public static uint Jal(int target) => (uint)(0x03 << 26 | (target >> 2));

        public static uint Cop0(int op, int rt, int rd) => (uint)(0x10 << 26 | op << 21 | rt << 16 | rd << 11);

        public static uint Cop2Move(int op, int rt, int rd, int element) =>
            (uint)(0x12 << 26 | op << 21 | rt << 16 | rd << 11 | element << 7);

        public static uint Vector(int funct, int vd, int vs, int vt, int e) =>
            (uint)(0x12 << 26 | 1 << 25 | e << 21 | vt << 16 | vs << 11 | vd << 6 | funct);

        public static uint LoadStore(int op, int kind, int vt, int rs, int element, int offset) =>
            (uint)(op << 26 | rs << 21 | vt << 16 | kind << 11 | element << 7 | (offset & 0x7F));

        public static uint Lqv(int vt, int rs, int element, int offset) => LoadStore(0x32, 0x04, vt, rs, element, offset);
    }
}
