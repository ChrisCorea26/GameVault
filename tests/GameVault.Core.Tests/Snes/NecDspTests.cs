using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>
/// The µPD7725 core (outstanding-work plan §4) on hand-assembled programs: the real DSP-n program ROMs are the player's
/// own, so they can't be in the repository. Each test builds a tiny program with the encoders below.
/// </summary>
public sealed class NecDspTests
{
    // ---- An assembler for the four instruction types ----

    // OP: pselect(2) alu(4) asl(1) dpl(2) dphm(4) rpdcr(1) src(4) dst(4), type 00.
    private static uint Op(int alu = 0, int p = 0, int asl = 0, int src = 0, int dst = 0, int dpl = 0, int dphm = 0, bool rpdcr = false) =>
        (uint)((p << 20) | (alu << 16) | (asl << 15) | (dpl << 13) | (dphm << 9) | ((rpdcr ? 1 : 0) << 8) | (src << 4) | dst);

    private static uint Rt(int alu = 0, int p = 0, int asl = 0, int src = 0, int dst = 0) => (1u << 22) | Op(alu, p, asl, src, dst);

    private static uint Jp(int brch, int target) => (2u << 22) | (uint)(brch << 13) | (uint)(target << 2);

    private static uint Ld(int value, int dst) => (3u << 22) | (uint)((value & 0xFFFF) << 6) | (uint)dst;

    // Registers (LD/OP destinations; sources are listed in the core).
    private const int DstA = 1, DstB = 2, DstTr = 3, DstDp = 4, DstRp = 5, DstDr = 6, DstSr = 7, DstK = 10, DstKl = 11, DstL = 13, DstRam = 15;
    private const int SrcA = 1, SrcB = 2, SrcDr = 8, SrcRam = 15, SrcRom = 6;
    private const int PRam = 0, PIdb = 1, PM = 2, PN = 3;
    private const int AluOr = 1, AluAdd = 5, AluSub = 4, AluInc = 9, AluShl1 = 12;
    private const int Jmp = 0x100, Call = 0x140, Jrqm = 0x0BE, Jnrqm = 0x0BC, Jza = 0x08A, Jnza = 0x088, Jca = 0x082;

    private static readonly NecDspMap TestMap = NecDspMap.LoRom1Mb;
    private const uint DataPort = 0x308000;
    private const uint StatusPort = 0x30C000;

    private static NecDsp Dsp(uint[] code, ushort[]? data = null)
    {
        var program = new uint[NecDsp.ProgramWords];
        code.CopyTo(program, 0);
        for (var i = code.Length; i < program.Length; i++)
        {
            program[i] = Jp(Jmp, i); // park anywhere past the program
        }

        var dsp = new NecDsp("DSP-1", program, data ?? new ushort[NecDsp.DataWords], TestMap);
        dsp.Power();
        return dsp;
    }

    private static void Run(NecDsp dsp, int instructions)
    {
        for (var i = 0; i < instructions; i++)
        {
            dsp.Step();
        }
    }

    [Fact]
    public void Loads_and_the_alu_set_the_accumulators_and_flags()
    {
        var dsp = Dsp(
        [
            Ld(0x7FFF, DstA),
            Ld(0x0001, DstB),
            Op(alu: AluAdd, p: PIdb, asl: 0, src: SrcB), // A = A + B: overflows into the sign
            Op(alu: AluSub, p: PIdb, asl: 1, src: SrcB), // B = B - B = 0
        ]);

        Run(dsp, 4);

        dsp.A.Should().Be(0x8000);
        dsp.B.Should().Be(0);
    }

    [Fact]
    public void Conditional_jumps_test_the_flags()
    {
        var dsp = Dsp(
        [
            Ld(0, DstA),
            Op(alu: AluOr, p: PIdb, src: SrcA), // A |= A: zero
            Jp(Jza, 5),
            Ld(0x1111, DstB), // skipped
            Jp(Jmp, 6),
            Ld(0x2222, DstB), // 5: taken
            Jp(Jmp, 6),
        ]);

        Run(dsp, 6);

        dsp.B.Should().Be(0x2222);
    }

    [Fact]
    public void The_multiplier_gives_m_and_n_after_every_instruction()
    {
        // K = 0x4000 (0.5), L = 0x2000 (0.25): 0x08000000 → M = sign and top 15 bits, N = low 15 bits and a zero.
        var dsp = Dsp(
        [
            Ld(0x4000, DstK),
            Ld(0x2000, DstL),
            Op(alu: AluOr, p: PM, asl: 0), // A = 0 | M
            Op(alu: AluOr, p: PN, asl: 1), // B = 0 | N
        ]);

        Run(dsp, 4);

        var product = 0x4000 * 0x2000;
        dsp.A.Should().Be((ushort)(product >> 15));
        dsp.B.Should().Be((ushort)(product << 1));
    }

    [Fact]
    public void The_snes_reads_a_16_bit_result_low_byte_first_and_rqm_clears()
    {
        var dsp = Dsp(
        [
            Ld(0xBEEF, DstDr), // DR = result, RQM set
            Jp(Jrqm, 1),       // wait while the SNES hasn't read it
            Ld(0x1234, DstA),
            Jp(Jmp, 3),
        ]);

        Run(dsp, 5);
        (dsp.Read(StatusPort, 0) & 0x80).Should().Be(0x80, "RQM: the DSP wants a transfer");
        dsp.Pc.Should().Be(1);

        dsp.Read(DataPort, 0).Should().Be(0xEF);
        (dsp.Read(StatusPort, 0) & 0x80).Should().Be(0x80, "still half a word to go");
        dsp.Read(DataPort, 0).Should().Be(0xBE);
        (dsp.Read(StatusPort, 0) & 0x80).Should().Be(0);

        Run(dsp, 3);
        dsp.A.Should().Be(0x1234, "the DSP went on once the word was read");
    }

    [Fact]
    public void A_command_from_the_snes_is_echoed_plus_one()
    {
        // Wait for a word (RQM set by reading DR), add one, send it back.
        var dsp = Dsp(
        [
            Op(src: SrcDr, dst: DstA),       // 0: A = DR, RQM = 1 (ready for the next transfer)
            Jp(Jrqm, 1),                     // 1: wait for the SNES's write to clear RQM
            Op(src: SrcDr, dst: DstA),       // 2: A = the word written
            Op(alu: AluInc, asl: 0),         // 3: A++
            Op(src: SrcA, dst: DstDr),       // 4: DR = A, RQM
            Jp(Jmp, 5),
        ]);

        Run(dsp, 4);
        dsp.Write(DataPort, 0x41);
        dsp.Write(DataPort, 0x10);
        Run(dsp, 6);

        dsp.Read(DataPort, 0).Should().Be(0x42);
        dsp.Read(DataPort, 0).Should().Be(0x10);
    }

    [Fact]
    public void Calls_return_and_data_rom_and_ram_are_addressed_by_rp_and_dp()
    {
        var data = new ushort[NecDsp.DataWords];
        data[0x3FF] = 0x0ABC;
        data[0x3FE] = 0x0001;
        var dsp = Dsp(
        [
            Ld(0x3FF, DstRp),
            Jp(Call, 5),
            Ld(0x0021, DstDp),                 // 2: DP = $21
            Op(src: SrcA, dst: DstRam),        // 3: RAM[$21] = A
            Jp(Jmp, 4),
            Op(src: SrcRom, dst: DstA, rpdcr: true), // 5: A = ROM[RP], RP--
            Rt(alu: AluAdd, p: PIdb, asl: 0, src: SrcRom), // 6: A += ROM[RP] ($3FE), return
        ], data);

        Run(dsp, 6);

        dsp.A.Should().Be(0x0ABD);
        dsp.Ram(0x21).Should().Be(0x0ABD);
        dsp.Pc.Should().Be(4);
    }

    [Fact]
    public void A_dump_in_either_byte_order_splits_the_same()
    {
        var program = new uint[NecDsp.ProgramWords];
        for (var i = 0; i < program.Length; i++)
        {
            program[i] = (i % 4) switch
            {
                0 => Jp(Jrqm, i),
                1 => Jp(Call, (i * 7) & 0x7FF),
                2 => Ld(i, DstA),
                _ => Op(alu: AluAdd, p: PIdb, src: SrcB),
            };
        }

        var data = Enumerable.Range(0, NecDsp.DataWords).Select(i => (ushort)(i * 31)).ToArray();
        byte[] Dump(bool big)
        {
            var dump = new byte[NecDsp.DumpSize];
            for (var i = 0; i < program.Length; i++)
            {
                var w = program[i];
                (dump[i * 3], dump[i * 3 + 1], dump[i * 3 + 2]) = big
                    ? ((byte)(w >> 16), (byte)(w >> 8), (byte)w)
                    : ((byte)w, (byte)(w >> 8), (byte)(w >> 16));
            }

            for (var i = 0; i < data.Length; i++)
            {
                var at = NecDsp.ProgramWords * 3 + i * 2;
                (dump[at], dump[at + 1]) = big ? ((byte)(data[i] >> 8), (byte)data[i]) : ((byte)data[i], (byte)(data[i] >> 8));
            }

            return dump;
        }

        foreach (var big in new[] { false, true })
        {
            var (p, d) = NecDsp.SplitDump(Dump(big));
            p.Should().Equal(program, big ? "big-endian dump" : "little-endian dump");
            d.Should().Equal(data);
        }
    }

    [Fact]
    public void Its_clock_runs_at_7_6_mhz_against_the_master_clock()
    {
        var dsp = Dsp([Jp(Jmp, 0)]);
        dsp.CatchUp(21_477_272 / 10);
        dsp.Instructions.Should().BeCloseTo(760_000, 1);

        dsp.EndFrame(21_477_272 / 10);
        dsp.CatchUp(1000);
        dsp.Instructions.Should().BeCloseTo(760_000 + 354, 1);
    }

    [Fact]
    public void A_save_state_resumes_exactly()
    {
        uint[] code =
        [
            Ld(0x0123, DstA),
            Ld(0x0300, DstK),
            Ld(0x0021, DstL),
            Op(alu: AluAdd, p: PM, asl: 0, dpl: 1),
            Op(alu: AluShl1, asl: 1, src: SrcA, dst: DstRam),
            Jp(Jnza, 3),
        ];
        var first = Dsp(code);
        Run(first, 50);
        var writer = new StateWriter();
        first.SaveState(writer);
        Run(first, 77);

        var second = Dsp(code);
        second.LoadState(new StateReader(writer.ToArray()));
        Run(second, 77);

        var a = new StateWriter();
        first.SaveState(a);
        var b = new StateWriter();
        second.SaveState(b);
        b.ToArray().Should().Equal(a.ToArray());
    }
}
