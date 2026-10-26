using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>CSR, IMR and the interrupt line, the video timing hooks, 32-bit access and save states.</summary>
public sealed class Ps2GsPrivilegedTests
{
    [Fact]
    public void Csr_reads_the_revision_id_and_an_empty_fifo()
    {
        var gs = new Ps2Gs();
        var csr = gs.ReadPrivileged(Ps2Gs.PrivCsr);
        (csr >> 16 & 0xFFFF).Should().Be(0x551B);
        (csr & Ps2Gs.CsrFifoEmpty).Should().NotBe(0);
        gs.ReadPrivileged32(Ps2Gs.PrivCsr).Should().Be((uint)csr);
        gs.ReadPrivileged32(Ps2Gs.PrivCsr + 4).Should().Be(0);
    }

    [Fact]
    public void Finish_sets_its_event_and_writing_one_clears_it()
    {
        var gs = new Ps2Gs();
        gs.WriteRegister(Ps2Gs.RegFinish, 0);
        (gs.Csr & Ps2Gs.CsrFinish).Should().NotBe(0);
        gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrSignal);
        (gs.Csr & Ps2Gs.CsrFinish).Should().NotBe(0, "writing 0 to FINISH leaves it");
        gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrFinish);
        (gs.Csr & Ps2Gs.CsrFinish).Should().Be(0);
    }

    [Fact]
    public void Imr_masks_the_interrupt_and_unmasking_a_pending_event_raises_it()
    {
        var gs = new Ps2Gs();
        var raised = 0;
        gs.InterruptRaised = () => raised++;
        gs.Imr.Should().Be(0x7F00, "everything is masked at reset");
        gs.VBlankStart();
        (gs.Csr & Ps2Gs.CsrVsInt).Should().NotBe(0);
        gs.InterruptPending.Should().BeFalse();
        raised.Should().Be(0);

        gs.WritePrivileged32(Ps2Gs.PrivImr, 0x7F00 & ~0x800u); // VSMSK off
        gs.InterruptPending.Should().BeTrue();
        raised.Should().Be(1);
        gs.HBlankStart();
        raised.Should().Be(1, "HSINT is still masked");
        gs.VBlankStart();
        raised.Should().Be(2);
        gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrVsInt);
        gs.InterruptPending.Should().BeFalse();

        gs.WritePrivileged(Ps2Gs.PrivImr, 0x7F00 & ~0x100u); // SIGMSK off
        gs.WriteRegister(Ps2Gs.RegSignal, 0);
        raised.Should().Be(3);
        gs.InterruptPending.Should().BeTrue();
    }

    [Fact]
    public void Vblank_end_toggles_the_field()
    {
        var gs = new Ps2Gs();
        gs.OddField.Should().BeFalse();
        gs.VBlankEnd();
        gs.OddField.Should().BeTrue();
        (gs.Csr & Ps2Gs.CsrField).Should().NotBe(0);
        gs.VBlankEnd();
        (gs.Csr & Ps2Gs.CsrField).Should().Be(0);
    }

    [Fact]
    public void Csr_reset_clears_the_events_and_display_settings()
    {
        var gs = new Ps2Gs();
        gs.WritePrivileged(Ps2Gs.PrivPmode, 3);
        gs.WritePrivileged(Ps2Gs.PrivDispFb1, 0x1234);
        gs.WriteRegister(Ps2Gs.RegFinish, 0);
        gs.WritePrivileged(Ps2Gs.PrivCsr, Ps2Gs.CsrReset);
        gs.ReadPrivileged(Ps2Gs.PrivPmode).Should().Be(0);
        gs.ReadPrivileged(Ps2Gs.PrivDispFb1).Should().Be(0);
        (gs.Csr & Ps2Gs.CsrFinish).Should().Be(0);
    }

    [Fact]
    public void Thirty_two_bit_writes_replace_one_half()
    {
        var gs = new Ps2Gs();
        gs.WritePrivileged32(Ps2Gs.PrivDisplay1, 0x1111_2222);
        gs.WritePrivileged32(Ps2Gs.PrivDisplay1 + 4, 0x3333_4444);
        gs.ReadPrivileged(Ps2Gs.PrivDisplay1).Should().Be(0x3333_4444_1111_2222);
        gs.WritePrivileged32(Ps2Gs.PrivDisplay1, 0x5555_6666);
        gs.ReadPrivileged(Ps2Gs.PrivDisplay1).Should().Be(0x3333_4444_5555_6666);
        gs.ReadPrivileged32(Ps2Gs.PrivDisplay1 + 4).Should().Be(0x3333_4444);
        gs.WritePrivileged32(Ps2Gs.PrivSigLblId + 4, 0xABCD);
        gs.SigLblId.Should().Be(0xABCDUL << 32);
    }

    [Fact]
    public void Save_state_round_trips_memory_registers_and_a_transfer_in_progress()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 8, 8, 0x10, 0x20, 0x30);
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangle, iip: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0xFF, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0, 0xFF, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(100 * 16, 0));
        f.W(Ps2Gs.RegBitBltBuf, GsRegs.BitBltBuf(0, 0, 0, 0x300, 1, Ps2Gs.PsmCt24));
        f.W(Ps2Gs.RegTrxPos, 0);
        f.W(Ps2Gs.RegTrxReg, GsRegs.TrxReg(8, 1));
        f.W(Ps2Gs.RegTrxDir, 0);
        f.Gs.WriteHwReg(0x1122_3344_5566_7788);
        f.Gs.WritePrivileged(Ps2Gs.PrivImr, 0x1234);
        f.Gs.WritePrivileged(Ps2Gs.PrivDisplay2, 0xFEED);
        f.Gs.WriteRegister(Ps2Gs.RegSignal, 5 | (0xFUL << 32));

        var writer = new StateWriter();
        f.Gs.SaveState(writer);
        var saved = writer.ToArray();

        void Finish(Ps2Gs gs)
        {
            gs.WriteHwReg(0x99AA_BBCC_DDEE_FF00);
            gs.WriteHwReg(0x0102_0304_0506_0708);
            gs.WriteHwReg(0x1111_2222_3333_4444);
            gs.WriteRegister(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0, 0, 0xFF));
            gs.WriteRegister(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 100 * 16));
        }

        Finish(f.Gs);
        var restored = new Ps2Gs();
        var reader = new StateReader(saved);
        restored.LoadState(reader);
        reader.IsAtEnd.Should().BeTrue();
        restored.Imr.Should().Be(0x1234 & 0x7F00);
        restored.ReadPrivileged(Ps2Gs.PrivDisplay2).Should().Be(0xFEED);
        restored.SigLblId.Should().Be(5);
        Finish(restored);
        restored.LocalMemory.AsSpan().SequenceEqual(f.Gs.LocalMemory).Should().BeTrue();
        restored.Pixel(f, 20, 30).Should().Be(f.Pixel(20, 30));
    }
}

internal static class GsStateExtensions
{
    public static uint Pixel(this Ps2Gs gs, GsFixture f, int x, int y) => gs.ReadPixel(f.FramePsm, 0, GsFixture.Fbw, x, y);
}
