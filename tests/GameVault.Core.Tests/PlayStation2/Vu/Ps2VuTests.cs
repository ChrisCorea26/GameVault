using System.Buffers.Binary;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using static GameVault.Core.Tests.PlayStation2.Vu.VuOps;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>Micro-mode and macro-mode behaviour of <see cref="Ps2Vu"/>: per instruction class, pipeline and host.</summary>
public class Ps2VuTests
{
    private const uint One = 0x3F800000;
    private const uint Two = 0x40000000;

    private sealed class Program(Ps2Vu vu) : VuBlock(vu.MicroMemory)
    {
        /// <summary>Ends the program and runs it from 0; returns the cycles it took.</summary>
        public long Run()
        {
            SafeExit();
            vu.StartMicro(0);
            long cycles = 0;
            while (vu.Running && cycles < 1_000_000)
            {
                cycles += vu.Run(1000);
            }

            Assert.False(vu.Running);
            return cycles;
        }
    }

    private static (Ps2Vu Vu0, Ps2Vu Vu1, FakeVuHost Host) Create() => FakeVuHost.CreatePair();

    private static void SetVf(Ps2Vu vu, int reg, uint x, uint y, uint z, uint w)
    {
        vu.SetVf(reg, 0, x);
        vu.SetVf(reg, 1, y);
        vu.SetVf(reg, 2, z);
        vu.SetVf(reg, 3, w);
    }

    private static uint[] Vf(Ps2Vu vu, int reg) => [vu.GetVf(reg, 0), vu.GetVf(reg, 1), vu.GetVf(reg, 2), vu.GetVf(reg, 3)];

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void FmacResult_StallsAReaderForThreeCycles()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 2, One, One, One, One);
        var p = new Program(vu);
        p.WrUpper(ADD(DestXYZW, VF(1), VF(2), VF(2)));
        p.WrUpper(ADD(DestXYZW, VF(4), VF(1), VF(1)));
        Assert.Equal(4 + 3, p.Run());
        Assert.Equal([F(4), F(4), F(4), F(4)], Vf(vu, 4));

        p.Reset();
        p.WrUpper(ADD(DestXYZW, VF(1), VF(2), VF(2)));
        p.WrUpper(ADD(DestXYZW, VF(4), VF(2), VF(2)));
        Assert.Equal(4, p.Run());
    }

    [Fact]
    public void FmacHazard_IsCheckedPerField()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrUpper(ADD(DestX, VF(1), VF(2), VF(2)));
        p.WrUpper(ADD(DestY, VF(4), VF(1), VF(1)));
        Assert.Equal(4, p.Run());

        p.Reset();
        p.WrUpper(ADD(DestX, VF(1), VF(2), VF(2)));
        p.WrUpper(ADDbc(DestY, FieldX, VF(4), VF(3), VF(1))); // the broadcast field x is pending
        Assert.Equal(7, p.Run());
    }

    [Fact]
    public void MacFlags_AppearFourCyclesLater()
    {
        var (_, vu, _) = Create();
        vu.SetVi(2, 0xFFFF);
        var p = new Program(vu);
        p.WrUpper(SUB(DestXYZW, VF(1), VF(0), VF(0)));
        for (var reg = 3; reg <= 6; reg++)
        {
            p.WrLower(FMAND(VI(reg), VI(2)));
        }

        p.Run();
        Assert.Equal(0, vu.GetVi(3));
        Assert.Equal(0, vu.GetVi(4));
        Assert.Equal(0, vu.GetVi(5));
        Assert.Equal(0x000F, vu.GetVi(6)); // Zx..Zw
        Assert.Equal(0x0041u, vu.Status); // Z and its sticky ZS
    }

    [Fact]
    public void StatusFlags_StickyBitsOutliveTheResult()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrUpper(SUB(DestXYZW, VF(1), VF(0), VF(0))); // zero: Z
        p.WrUpper(ADD(DestW, VF(2), VF(0), VF(0))); // 2.0 in w only: no flags
        for (var n = 0; n < 4; n++)
        {
            p.WrUpper(NOP());
        }

        p.WrLower(FSAND(VI(1), 0xFFF));
        p.WrLower(FSSET(0));
        p.Run();
        Assert.Equal(0x040, vu.GetVi(1)); // ZS remains, Z is gone
        Assert.Equal(0u, vu.Status);
        Assert.Equal(0u, vu.Mac);
    }

    [Fact]
    public void Overflow_SaturatesAndSetsO()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, 0x7F000000, 0xFF000000, 0x7F000000, 0x3F800000);
        var p = new Program(vu);
        p.WrUpper(MUL(DestXYZ, VF(2), VF(1), VF(1)));
        p.Run();
        Assert.Equal([0x7FFFFFFFu, 0x7FFFFFFFu, 0x7FFFFFFFu, 0u], Vf(vu, 2));
        Assert.Equal(0xE000u, vu.Mac);
        Assert.Equal(0x208u, vu.Status); // O, OS
    }

    [Fact]
    public void Underflow_FlushesToZeroAndSetsUAndZ()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, 0x00800000, 0x80800000, 0x00800000, 0x00800000);
        var p = new Program(vu);
        p.WrUpper(MUL(DestXYZW, VF(2), VF(1), VF(1)));
        p.WrUpper(MUL(DestXY, VF(3), VF(1), VF(0)));
        p.Run();
        Assert.Equal([0u, 0u, 0u, 0u], Vf(vu, 2));
        Assert.Equal([0u, 0x80000000u, 0u, 0u], Vf(vu, 3)); // −x × 0 = −0: S and Z
        Assert.Equal(0x004Cu, vu.Mac);
        Assert.Equal(0x1C3u, vu.Status); // Z, S now; ZS, SS and US (the first MUL's U) sticky
    }

    [Fact]
    public void Div_FlagsInvalidAndDivisionByZeroWhenQIsWritten()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrLower(DIV(FieldX, VF(0), FieldX, VF(0))); // 0/0
        p.WrLower(WAITQ());
        p.WrLower(FSAND(VI(1), 0x30));
        p.WrLower(DIV(FieldW, VF(0), FieldX, VF(0))); // 1/0
        p.WrLower(FSAND(VI(2), 0x30)); // still I: the new result isn't in yet
        p.WrLower(WAITQ());
        p.WrLower(FSAND(VI(3), 0xFFF));
        p.Run();
        Assert.Equal(0x10, vu.GetVi(1));
        Assert.Equal(0x10, vu.GetVi(2));
        Assert.Equal(0x20 | 0x400 | 0x800, vu.GetVi(3)); // D, with IS and DS sticky
        Assert.Equal(0x7FFFFFFFu, vu.Q);
    }

    [Fact]
    public void Q_ArrivesSevenCyclesAfterDiv()
    {
        var (_, vu, _) = Create();
        vu.Q = One;
        SetVf(vu, 1, F(4), 0, 0, 0);
        var p = new Program(vu);
        p.WrLower(DIV(FieldW, VF(0), FieldX, VF(1)));
        for (var reg = 2; reg <= 8; reg++)
        {
            p.WrUpper(ADDq(DestX, VF(reg), VF(0)));
        }

        p.Run();
        for (var reg = 2; reg <= 7; reg++)
        {
            Assert.Equal(One, vu.GetVf(reg, 0));
        }

        Assert.Equal(F(0.25f), vu.GetVf(8, 0));
    }

    [Theory]
    [InlineData(false, 7 + 3)]
    [InlineData(true, 13 + 3)]
    public void WaitQ_StallsUntilTheFdivResult(bool rsqrt, long cycles)
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(4), 0, 0, 0);
        var p = new Program(vu);
        p.WrLower(rsqrt ? RSQRT(FieldW, VF(0), FieldX, VF(1)) : DIV(FieldW, VF(0), FieldX, VF(1)));
        p.WrLower(WAITQ());
        Assert.Equal(cycles, p.Run());
        Assert.Equal(rsqrt ? F(0.5f) : F(0.25f), vu.Q);
    }

    [Fact]
    public void Branch_RunsItsDelaySlotAndLinks()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrLower(BAL(VI(15), 2)); // to pair 3
        p.WrLower(IADDIU(VI(2), VI(0), 7)); // delay slot
        p.WrLower(IADDIU(VI(3), VI(0), 9)); // skipped
        p.WrLower(IADDIU(VI(4), VI(0), 1));
        p.Run();
        Assert.Equal(7, vu.GetVi(2));
        Assert.Equal(0, vu.GetVi(3));
        Assert.Equal(1, vu.GetVi(4));
        Assert.Equal(2, vu.GetVi(15));
    }

    [Fact]
    public void JumpRegister_GoesToTheRegisterTimesEight()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrLower(IADDIU(VI(1), VI(0), 4));
        p.WrUpper(NOP());
        p.WrLower(JR(VI(1)));
        p.WrUpper(NOP());
        p.WrLower(IADDIU(VI(2), VI(0), 1)); // pair 4
        p.Run();
        Assert.Equal(1, vu.GetVi(2));
        Assert.Equal(0, vu.GetVi(3));
    }

    [Fact]
    public void IntegerBranchHazard_BranchSeesTheValueBeforeTheImmediatelyPrecedingWrite()
    {
        // IADDIU vi1 = 1 right before IBNE vi1, vi0: the branch still sees vi1 = 0 and falls through.
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrLower(IADDIU(VI(1), VI(0), 1));
        p.WrLower(IBNE(VI(1), VI(0), 2));
        p.WrUpper(NOP());
        p.WrLower(IADDIU(VI(2), VI(0), 1));
        p.Run();
        Assert.Equal(1, vu.GetVi(2));

        // One instruction between them: the branch sees the new value and is taken.
        p.Reset();
        vu.SetVi(1, 0);
        vu.SetVi(2, 0);
        p.WrLower(IADDIU(VI(1), VI(0), 1));
        p.WrUpper(NOP());
        p.WrLower(IBNE(VI(1), VI(0), 2));
        p.WrUpper(NOP());
        p.WrLower(IADDIU(VI(2), VI(0), 1));
        p.Run();
        Assert.Equal(0, vu.GetVi(2));

        // A branch that stalls (its upper waits on VF1) sees the new value too.
        p.Reset();
        vu.SetVi(1, 0);
        p.Wr(ADD(DestXYZW, VF(1), VF(0), VF(0)), IADDIU(VI(1), VI(0), 1));
        p.Wr(ADD(DestXYZW, VF(2), VF(1), VF(1)), IBNE(VI(1), VI(0), 2));
        p.WrUpper(NOP());
        p.WrLower(IADDIU(VI(2), VI(0), 1));
        p.Run();
        Assert.Equal(0, vu.GetVi(2));
    }

    [Fact]
    public void EBit_EndsAfterTheNextPair()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.Wr(NOP(UpperE), IADDIU(VI(1), VI(0), 1));
        p.WrLower(IADDIU(VI(2), VI(0), 2));
        p.WrLower(IADDIU(VI(3), VI(0), 3));
        Assert.Equal(2, p.Run());
        Assert.Equal([1, 2, 0], new int[] { vu.GetVi(1), vu.GetVi(2), vu.GetVi(3) });
        Assert.Equal(16u, vu.Pc);

        vu.ContinueMicro(); // MSCNT resumes at the pair after the delay slot
        while (vu.Running)
        {
            vu.Run(100);
        }

        Assert.Equal(3, vu.GetVi(3));
    }

    [Fact]
    public void IBit_LoadsIAfterTheUpperInstructionReadsIt()
    {
        var (_, vu, _) = Create();
        vu.I = One;
        var p = new Program(vu);
        p.WrImm(ADDi(DestX, VF(1), VF(0)), 3.0f);
        p.WrUpper(ADDi(DestX, VF(2), VF(0)));
        p.Run();
        Assert.Equal(One, vu.GetVf(1, 0));
        Assert.Equal(F(3), vu.GetVf(2, 0));
        Assert.Equal(F(3), vu.I);
    }

    [Fact]
    public void SamePair_LowerReadsTheOldValueAndTheUpperWinsAWriteToTheSameRegister()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(7), F(7), F(7), F(7));
        SetVf(vu, 2, One, One, One, One);
        SetVf(vu, 5, F(9), F(9), F(9), F(9));
        var p = new Program(vu);
        p.Wr(ADD(DestXYZW, VF(1), VF(2), VF(2)), SQ(DestXYZW, VF(1), VI(0), 0));
        p.Wr(ADD(DestXY, VF(3), VF(2), VF(2)), MOVE(DestXYZW, VF(3), VF(5)));
        p.Run();
        Assert.Equal(F(7), BinaryPrimitives.ReadUInt32LittleEndian(vu.DataMemory));
        Assert.Equal([Two, Two, F(9), F(9)], Vf(vu, 3));
    }

    [Fact]
    public void LoadStoreIncrementAndDecrement_WrapAtTheEndOfDataMemory()
    {
        var (vu0, vu1, _) = Create();
        VuTestRunner.SetConstant(vu1.DataMemory, 0x3FF * 16, 1, 2, 3, 4);
        VuTestRunner.SetConstant(vu1.DataMemory, 0, 5, 6, 7, 8);
        vu1.SetVi(1, 0x3FF);
        var p = new Program(vu1);
        p.WrLower(LQI(DestXYZW, VF(1), VI(1)));
        p.WrUpper(NOP());
        p.WrLower(LQ(DestX, VF(2), VI(1), 0)); // $400 wraps to 0
        p.WrLower(SQD(DestXYZW, VF(1), VI(2))); // --vi2 = $FFFF → $3FF
        p.Run();
        Assert.Equal([1u, 2u, 3u, 4u], Vf(vu1, 1));
        Assert.Equal(0x400, vu1.GetVi(1));
        Assert.Equal(5u, vu1.GetVf(2, 0));
        Assert.Equal(0xFFFF, vu1.GetVi(2));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(vu1.DataMemory.AsSpan(0x3FF * 16 + 12)));

        VuTestRunner.SetConstant(vu0.DataMemory, 0, 11, 12, 13, 14);
        vu0.SetVi(1, 0x100);
        var q = new Program(vu0);
        q.WrLower(LQ(DestXYZW, VF(1), VI(1), 0)); // VU0's 4 KB wrap at $100 quadwords
        q.Run();
        Assert.Equal([11u, 12u, 13u, 14u], Vf(vu0, 1));
    }

    [Fact]
    public void Vu0_ReachesVu1RegistersAt4000()
    {
        var (vu0, vu1, _) = Create();
        SetVf(vu1, 5, 0x11, 0x22, 0x33, 0x44);
        vu1.SetVi(3, 0x1234);
        vu1.Q = 0xCAFEF00D;
        var p = new Program(vu0);
        p.WrLower(IADDIU(VI(1), VI(0), 0x400));
        p.WrUpper(NOP());
        p.WrLower(LQ(DestXYZW, VF(1), VI(1), 5)); // VU1 VF05
        p.WrLower(ILW(DestX, VI(2), VI(1), 0x23)); // VU1 VI03
        p.WrLower(ILW(DestX, VI(3), VI(1), 0x36)); // VU1 Q
        p.WrUpper(NOP());
        p.WrUpper(NOP());
        p.WrLower(SQ(DestXYZW, VF(1), VI(1), 6)); // VU1 VF06
        p.WrLower(ISW(DestX, VI(2), VI(1), 0x24)); // VU1 VI04
        p.Run();
        Assert.Equal([0x11u, 0x22u, 0x33u, 0x44u], Vf(vu0, 1));
        Assert.Equal(0x1234, vu0.GetVi(2));
        Assert.Equal(0xF00D, vu0.GetVi(3));
        Assert.Equal([0x11u, 0x22u, 0x33u, 0x44u], Vf(vu1, 6));
        Assert.Equal(0x1234, vu1.GetVi(4));
    }

    [Fact]
    public void XgKickAndTop_GoThroughTheHost()
    {
        var (vu0, vu1, host) = Create();
        host.Top = 0x123;
        host.Itop = 0x345;
        var p = new Program(vu1);
        p.WrLower(XTOP(VI(1)));
        p.WrLower(XITOP(VI(2)));
        p.WrLower(IADDIU(VI(3), VI(0), 0x50));
        p.WrUpper(NOP());
        p.WrLower(XGKICK(VI(3)));
        p.Run();
        Assert.Equal(0x123, vu1.GetVi(1));
        Assert.Equal(0x345, vu1.GetVi(2));
        Assert.Equal([0x50u], host.Kicks);

        var q = new Program(vu0); // VU0 has no XGKICK, and its ITOP is 8 bits
        q.WrLower(XITOP(VI(2)));
        q.WrLower(XGKICK(VI(3)));
        q.Run();
        Assert.Equal(0x45, vu0.GetVi(2));
        Assert.Single(host.Kicks);
    }

    [Fact]
    public void DBit_StopsAndInterruptsOnlyWhenFbrstEnablesIt()
    {
        var (vu0, vu1, host) = Create();
        var p = new Program(vu1);
        p.Wr(NOP(UpperD), IADDIU(VI(1), VI(0), 1));
        p.WrLower(IADDIU(VI(2), VI(0), 2));
        p.Run();
        Assert.Equal(2, vu1.GetVi(2));
        Assert.Empty(host.Interrupts);

        vu0.WriteControl(28, 0x400); // DE1
        vu1.SetVi(2, 0);
        vu1.StartMicro(0);
        vu1.Run(100);
        Assert.False(vu1.Running);
        Assert.Equal(0, vu1.GetVi(2));
        Assert.Equal([1], host.Interrupts);
        Assert.Equal(0x200u, vu0.ReadControl(29)); // VDS1
        Assert.Equal(0x400u, vu0.ReadControl(28));
    }

    [Fact]
    public void TBit_StopsVu0WhenTeIsSet()
    {
        var (vu0, _, host) = Create();
        vu0.WriteControl(28, 0x8); // TE0
        var p = new Program(vu0);
        p.Wr(NOP(UpperT), IADDIU(VI(1), VI(0), 1));
        p.WrLower(IADDIU(VI(2), VI(0), 2));
        p.Run();
        Assert.Equal(0, vu0.GetVi(2));
        Assert.Equal([0], host.Interrupts);
        Assert.Equal(0x4u, vu0.ReadControl(29)); // VTS0
        Assert.Equal(1u, vu0.ReadControl(26)); // TPC: the pair after the T bit
    }

    [Fact]
    public void Moves_MoveRotateAndConvertIntegers()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 2, 0x11111111, 0x22222222, 0x33334444, 0x55555555);
        vu.SetVi(1, 0xFFFE);
        var p = new Program(vu);
        p.WrLower(MR32(DestXYZW, VF(3), VF(2)));
        p.WrLower(MFIR(DestXY, VF(4), VI(1)));
        p.WrLower(MTIR(VI(2), FieldZ, VF(2)));
        p.WrLower(MOVE(DestZ | DestW, VF(5), VF(2)));
        p.Run();
        Assert.Equal([0x22222222u, 0x33334444u, 0x55555555u, 0x11111111u], Vf(vu, 3));
        Assert.Equal([0xFFFFFFFEu, 0xFFFFFFFEu, 0u, 0u], Vf(vu, 4));
        Assert.Equal(0x4444, vu.GetVi(2));
        Assert.Equal([0u, 0u, 0x33334444u, 0x55555555u], Vf(vu, 5));
    }

    [Fact]
    public void OpmulaOpmsub_GiveTheCrossProduct()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(1), F(2), F(3), 0);
        SetVf(vu, 2, F(4), F(5), F(6), 0);
        var p = new Program(vu);
        p.WrUpper(OPMULA(VF(1), VF(2)));
        p.WrUpper(OPMSUB(VF(3), VF(2), VF(1)));
        p.Run();
        Assert.Equal([F(-3), F(6), F(-3), 0u], Vf(vu, 3));
        Assert.Equal(0x00A0u, vu.Mac); // Sx, Sz
    }

    [Fact]
    public void AccumulatorForms_ChainWithoutStalls()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(1), F(2), F(3), F(4));
        SetVf(vu, 2, F(10), 0, 0, 0);
        var p = new Program(vu);
        p.WrUpper(MULAbc(DestXYZW, FieldX, VF(1), VF(2))); // ACC = v1 × 10
        p.WrUpper(MADDA(DestXYZW, VF(1), VF(1))); // ACC += v1²
        p.WrUpper(MSUB(DestXYZW, VF(3), VF(1), VF(0))); // v3 = ACC − v1 × (0,0,0,1)
        Assert.Equal(5, p.Run());
        Assert.Equal([F(11), F(24), F(39), F(52)], Vf(vu, 3));
        Assert.Equal(F(56), vu.GetAcc(3));
    }

    [Fact]
    public void MaxMini_CompareAsSignMagnitude()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, 0x80000000, F(-2), 0x7FFFFFFF, F(1));
        SetVf(vu, 2, 0x00000000, F(-3), F(5), F(1));
        var p = new Program(vu);
        p.WrUpper(MAX(DestXYZW, VF(3), VF(1), VF(2)));
        p.WrUpper(MINI(DestXYZW, VF(4), VF(1), VF(2)));
        p.WrImm(MAXi(DestX, VF(5), VF(1)), 0xFFFFFFFF);
        p.WrUpper(MINIi(DestX, VF(6), VF(1)));
        p.Run();
        Assert.Equal([0u, F(-2), 0x7FFFFFFFu, F(1)], Vf(vu, 3));
        Assert.Equal([0x80000000u, F(-3), F(5), F(1)], Vf(vu, 4));
        Assert.Equal(0u, vu.GetVf(5, 0)); // max(−0, I) with the old I = +0
        Assert.Equal(0xFFFFFFFFu, vu.GetVf(6, 0)); // min(−0, −max)
    }

    [Fact]
    public void FixedPointConversions_UseTheirFractionBits()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(1.5f), F(-2.75f), F(1e10f), F(0.5f));
        var p = new Program(vu);
        p.WrUpper(FTOI4(DestXYZW, VF(2), VF(1)));
        p.WrUpper(NOP());
        p.WrUpper(NOP());
        p.WrUpper(NOP());
        p.WrUpper(ITOF12(DestXYZW, VF(3), VF(2)));
        p.WrUpper(FTOI15(DestW, VF(4), VF(1)));
        p.WrUpper(ABS(DestXY, VF(5), VF(1)));
        p.Run();
        Assert.Equal([24u, unchecked((uint)-44), 0x7FFFFFFFu, 8u], Vf(vu, 2));
        Assert.Equal(F(24f / 4096), vu.GetVf(3, 0));
        Assert.Equal(16384u, vu.GetVf(4, 3));
        Assert.Equal([F(1.5f), F(2.75f), 0u, 0u], Vf(vu, 5));
    }

    [Fact]
    public void ClipFlagInstructions_TestTheJudgements()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        p.WrLower(FCSET(0x000015));
        p.WrLower(FCAND(0x000001));
        p.WrLower(IADD(VI(2), VI(1), VI(0)));
        p.WrLower(FCEQ(0x000016));
        p.WrLower(IADD(VI(3), VI(1), VI(0)));
        p.WrLower(FCOR(0xFFFFEA));
        p.WrLower(IADD(VI(4), VI(1), VI(0)));
        p.WrLower(FCGET(VI(5)));
        p.Run();
        Assert.Equal([1, 0, 1, 0x15], new int[] { vu.GetVi(2), vu.GetVi(3), vu.GetVi(4), vu.GetVi(5) });
    }

    [Fact]
    public void Efu_WritesPAfterItsLatencyAndWaitPWaits()
    {
        var (_, vu, _) = Create();
        SetVf(vu, 1, F(4), 0, 0, 0);
        var p = new Program(vu);
        p.WrLower(ESQRT(FieldX, VF(1)));
        for (var n = 0; n < 9; n++)
        {
            p.WrUpper(NOP());
        }

        p.WrLower(MFP(DestX, VF(2))); // cycle 10: not yet
        p.WrLower(MFP(DestX, VF(3))); // cycle 11
        p.WrLower(ERCPR(FieldX, VF(1)));
        p.WrLower(WAITP());
        p.WrLower(MFP(DestX, VF(4)));
        p.Run();
        Assert.Equal(0u, vu.GetVf(2, 0));
        Assert.Equal(Two, vu.GetVf(3, 0));
        Assert.Equal(F(0.25f), vu.GetVf(4, 0));
    }

    [Fact]
    public void IntegerLoad_StallsAReaderLikeAnFmacResult()
    {
        var (_, vu, _) = Create();
        VuTestRunner.SetConstant(vu.DataMemory, 0, 21, 0, 0, 0);
        var p = new Program(vu);
        p.WrLower(ILW(DestX, VI(1), VI(0), 0));
        p.WrLower(IADD(VI(2), VI(1), VI(1)));
        Assert.Equal(4 + 3, p.Run());
        Assert.Equal(42, vu.GetVi(2));
    }

    [Fact]
    public void Run_StopsAtItsBudgetAndContinues()
    {
        var (_, vu, _) = Create();
        var p = new Program(vu);
        for (var n = 0; n < 20; n++)
        {
            p.WrLower(IADDI(VI(1), VI(1), 1));
        }

        p.SafeExit();
        vu.StartMicro(0);
        Assert.Equal(5, vu.Run(5));
        Assert.True(vu.Running);
        Assert.Equal(5, vu.GetVi(1));
        Assert.Equal(17, vu.Run(1000));
        Assert.False(vu.Running);
        Assert.Equal(20, vu.GetVi(1));
        Assert.Equal(0, vu.Run(10));
    }

    [Fact]
    public void Macro_ExecutesAtOnceWithFlagsAndQ()
    {
        var (vu0, _, _) = Create();
        vu0.WriteVf(1, new UInt128(((ulong)Two << 32) | Two, ((ulong)One << 32) | One));
        vu0.ExecuteMacro(Macro(SUB(DestXYZW, VF(2), VF(1), VF(1))));
        Assert.Equal(0x000Fu, vu0.ReadControl(17));
        Assert.Equal(0x041u, vu0.ReadControl(16));
        vu0.ExecuteMacro(Macro(ADD(DestXYZW, VF(3), VF(1), VF(1))));
        Assert.Equal(new UInt128(((ulong)F(4) << 32) | F(4), ((ulong)Two << 32) | Two), vu0.ReadVf(3));
        vu0.ExecuteMacro(Macro(DIV(FieldW, VF(1), FieldZ, VF(3)))); // 2 / 4
        Assert.Equal(F(0.5f), vu0.ReadControl(22));
        vu0.ExecuteMacro(Macro(MTIR(VI(5), FieldX, VF(3))));
        Assert.Equal(F(2) & 0xFFFF, vu0.ReadControl(5));
    }

    [Fact]
    public void Macro_StartsMicroProgramsAndReportsVpuStat()
    {
        var (vu0, vu1, _) = Create();
        var p = new Program(vu0);
        p.WrLower(IADDIU(VI(1), VI(0), 0x11));
        p.SafeExit();
        var q = new Program(vu1);
        q.Reset();
        for (var n = 0; n < 4; n++)
        {
            q.WrUpper(NOP());
        }

        q.WrLower(IADDIU(VI(1), VI(0), 0x22)); // at pair 4 = $20
        q.SafeExit();

        vu0.WriteControl(27, 0); // CMSAR0
        vu0.ExecuteMacro(Macro(0x39)); // VCALLMSR
        Assert.True(vu0.MicroRunning);
        vu0.WriteControl(31, 4); // CMSAR1: VU1 at pair 4
        Assert.Equal(0x101u, vu0.ReadControl(29));
        vu0.WaitForMicro();
        Assert.False(vu0.MicroRunning);
        Assert.Equal(0x11, vu0.GetVi(1));
        vu1.Run(100);
        Assert.Equal(0x22, vu1.GetVi(1));
        Assert.Equal(0u, vu0.ReadControl(29));

        // VCALLMS with an immediate; a macro instruction while it runs waits for it.
        vu0.SetVi(1, 0);
        vu0.ExecuteMacro(Macro(0x38));
        vu0.ExecuteMacro(Macro(IADDI(VI(2), VI(1), 1)));
        Assert.Equal(0x12, vu0.GetVi(2));
    }

    [Fact]
    public void Fbrst_ForceBreaksAndResets()
    {
        var (vu0, vu1, _) = Create();
        var q = new Program(vu1);
        var loop = q.L();
        q.B(loop); // spins forever
        vu1.StartMicro(0);
        vu1.Run(10);
        vu0.WriteControl(28, 0x100); // FB1
        Assert.False(vu1.Running);
        Assert.Equal(0x800u, vu0.ReadControl(29)); // VFS1
        vu0.WriteControl(28, 0x200); // RS1
        Assert.Equal(0u, vu0.ReadControl(29));
    }

    [Fact]
    public void SaveState_RoundTripsAProgramInFlight()
    {
        static (Ps2Vu Vu, FakeVuHost Host) Build()
        {
            var (_, vu1, host) = FakeVuHost.CreatePair();
            SetVf(vu1, 1, F(1), F(2), F(3), F(4));
            var p = new Program(vu1);
            p.WrLower(DIV(FieldW, VF(1), FieldX, VF(1)));
            p.WrUpper(MUL(DestXYZW, VF(2), VF(1), VF(1)));
            p.Wr(ADD(DestXYZW, VF(3), VF(2), VF(1)), ESUM(VF(1)));
            p.WrUpper(CLIP(VF(3), VF(0)));
            p.WrLower(IADDIU(VI(1), VI(0), 3));
            var loop = p.L();
            p.Wr(MULq(DestX, VF(4), VF(4)), IADDI(VI(1), VI(1), -1));
            p.WrUpper(NOP());
            p.IBNE(loop, VI(1), VI(0));
            p.WrLower(WAITP());
            p.WrLower(MFP(DestXYZW, VF(5)));
            p.WrLower(FCGET(VI(6)));
            p.WrLower(FMOR(VI(7), VI(0)));
            p.SafeExit();
            return (vu1, host);
        }

        var (original, _) = Build();
        original.StartMicro(0);
        original.Run(4); // DIV, the EFU, flags and a VF write all in flight
        var writer = new StateWriter();
        original.SaveState(writer);
        var saved = writer.ToArray();
        while (original.Running)
        {
            original.Run(7);
        }

        var finished = new StateWriter();
        original.SaveState(finished);

        var (restored, _) = Build();
        Array.Clear(restored.MicroMemory);
        restored.LoadState(new StateReader(saved));
        var again = new StateWriter();
        restored.SaveState(again);
        Assert.Equal(saved, again.ToArray());
        while (restored.Running)
        {
            restored.Run(3);
        }

        var restoredFinished = new StateWriter();
        restored.SaveState(restoredFinished);
        Assert.Equal(finished.ToArray(), restoredFinished.ToArray());
        Assert.Equal(F(4), restored.Q);
        Assert.Equal(F(10), restored.GetVf(5, 0));
        Assert.NotEqual(0, restored.GetVi(7));
    }
}
