using FluentAssertions;
using GameVault.Core.Cpu.R3000;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.PlayStation;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>The IOP's CPU, interrupt controller and timers, with programs assembled in the tests (no BIOS).</summary>
public sealed class IopCpuTests
{
    private const uint IStat = 0x1F801070;
    private const uint IMask = 0x1F801074;
    private const uint ICtrl = 0x1F801078;

    private readonly IopHarness _h = new();
    private readonly List<R3000Exception> _exceptions = [];

    public IopCpuTests()
    {
        _h.Iop.Cpu.ExceptionHook = _exceptions.Add;

        // The exception vector: an endless loop, so a test sees the exception and nothing after it.
        _h.Iop.Write32(0x80, 0x08000020); // j $80
        _h.Iop.Write32(0x84, Mips.Nop);
    }

    private uint Word(uint address) => _h.Iop.Read32(address);

    [Fact]
    public void Without_a_bios_the_cpu_is_halted_but_time_passes()
    {
        var iop = new Ps2Iop(null, new Ps2Sif());
        iop.Halted.Should().BeTrue();
        iop.Run(10_000);
        iop.Cycles.Should().Be(10_000);
        iop.Cpu.Pc.Should().Be(R3000Cpu.ResetVector);
    }

    [Fact]
    public void With_a_bios_the_cpu_starts_at_the_reset_vector()
    {
        var bios = new byte[Ps2Iop.BiosSize];
        // $BFC00000: li $2, $1234 ; sw $2, $100($0) ; j . ; nop
        uint[] code = [Mips.Ori(2, 0, 0x1234), Mips.Sw(2, 0, 0x100), 0x0BF00002, Mips.Nop];
        for (var i = 0; i < code.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bios.AsSpan(i * 4), code[i]);
        }

        var iop = new Ps2Iop(bios, new Ps2Sif());
        iop.Halted.Should().BeFalse();
        iop.Run(100);
        iop.Read32(0x100).Should().Be(0x1234u);
    }

    [Fact]
    public void Prid_reads_1F_on_the_iop()
    {
        _h.Load(Mips.Mfc0(2, 15), Mips.Nop, Mips.Sw(2, 0, 0x100));
        _h.Run(20);
        Word(0x100).Should().Be(0x1Fu);
        new R3000Cpu(new FlatR3000Bus()).ProcessorIdValue.Should().Be(R3000Cpu.ProcessorId, "the PlayStation keeps its own");
    }

    [Fact]
    public void Cop2_is_unusable_even_with_its_usable_bit_set()
    {
        _h.Load([.. IopHarness.Li(1, R3000Cpu.SrCu2), Mips.Mtc0(1, 12), Mips.Nop, Mips.Mfc2(2, 0), Mips.Nop]);
        _h.Run(20);
        _exceptions.Should().Equal(R3000Exception.CoprocessorUnusable);
        ((_h.Iop.Cpu.Cause >> 28) & 3).Should().Be(2u);
    }

    [Fact]
    public void A_timer_interrupt_reaches_the_cpu_through_i_mask_and_i_ctrl()
    {
        // Timer 0: compare interrupt at 100, repeating, back to 0 at the target.
        _h.Load(
        [
            .. IopHarness.Li(8, 0x1F801100),
            Mips.Ori(9, 0, 100), Mips.Sw(9, 8, 8), // target
            Mips.Ori(9, 0, 0x58), Mips.Sw(9, 8, 4), // mode: compare IRQ, zero return, repeat
            .. IopHarness.Li(8, IMask), Mips.Ori(9, 0, 1 << 4), Mips.Sw(9, 8, 0),
            Mips.Ori(9, 0, 1), Mips.Sw(9, 8, 4), // I_CTRL
            Mips.Ori(9, 0, 0x401), Mips.Mtc0(9, 12), // IEc and IM2
        ]);
        _h.Run(400);
        _exceptions.Should().Equal(R3000Exception.Interrupt);
        (_h.Iop.InterruptStatus & (1u << 4)).Should().NotBe(0);
    }

    [Fact]
    public void Reading_i_ctrl_returns_it_and_disables_interrupts()
    {
        _h.Iop.Write32(IMask, 1u << 2);
        _h.Iop.Write32(ICtrl, 1);
        _h.Iop.RaiseInterrupt(Ps2IopInterrupt.Cdvd);
        _h.Iop.Cpu.InterruptLine.Should().BeTrue();
        _h.Iop.Read32(ICtrl).Should().Be(1u);
        _h.Iop.Cpu.InterruptLine.Should().BeFalse();
        _h.Iop.Read32(ICtrl).Should().Be(0u);

        // Writing 0 to an I_STAT bit acknowledges it.
        _h.Iop.Write32(IStat, ~(1u << 2));
        _h.Iop.InterruptStatus.Should().Be(0u);
    }

    [Fact]
    public void The_iop_prints_through_its_console_byte()
    {
        var text = new System.Text.StringBuilder();
        _h.Iop.DebugOutput = c => text.Append(c);
        _h.Load([.. IopHarness.Li(8, 0x1F80380C), Mips.Ori(9, 0, 'O'), IopHarness.Sb(9, 8, 0), Mips.Ori(9, 0, 'K'), IopHarness.Sb(9, 8, 0)]);
        _h.Run(50);
        text.ToString().Should().Be("OK");
    }

    // ---- Timers ----

    private uint Timer(int timer, int register) =>
        _h.Iop.Read32((timer < 3 ? 0x1F801100u + (uint)timer * 0x10 : 0x1F801480u + (uint)(timer - 3) * 0x10) + (uint)register * 4);

    private void SetTimer(int timer, int register, uint value) =>
        _h.Iop.Write32((timer < 3 ? 0x1F801100u + (uint)timer * 0x10 : 0x1F801480u + (uint)(timer - 3) * 0x10) + (uint)register * 4, value);

    [Theory]
    [InlineData(0, 0x000u, 500, 500u)] // the IOP clock
    [InlineData(0, 0x100u, 1024, 375u)] // the 13.5 MHz pixel clock
    [InlineData(2, 0x200u, 800, 100u)] // divided by 8
    [InlineData(4, 0x2000u, 800, 100u)] // divided by 8
    [InlineData(5, 0x4000u, 1600, 100u)] // divided by 16
    [InlineData(4, 0x6000u, 2560, 10u)] // divided by 256
    public void Timers_count_their_sources(int timer, uint mode, int cycles, uint expected)
    {
        SetTimer(timer, 1, mode);
        _h.Run(cycles);
        Timer(timer, 0).Should().Be(expected);
    }

    [Fact]
    public void A_32_bit_timer_interrupts_at_its_target_and_repeats()
    {
        _h.Iop.Write32(IMask, 1u << 14);
        SetTimer(3, 2, 1000);
        SetTimer(3, 1, 0x58);
        _h.Run(999);
        (_h.Iop.InterruptStatus & (1u << 14)).Should().Be(0);
        _h.Run(1);
        (_h.Iop.InterruptStatus & (1u << 14)).Should().NotBe(0);
        Timer(3, 0).Should().Be(0u, "zero return");

        // Reading the mode shows and clears the compare flag; the interrupt repeats.
        (Timer(3, 1) & 0x800).Should().NotBe(0);
        (Timer(3, 1) & 0x800).Should().Be(0);
        _h.Iop.Write32(IStat, 0);
        _h.Run(1000);
        (_h.Iop.InterruptStatus & (1u << 14)).Should().NotBe(0);
    }

    [Fact]
    public void A_16_bit_timer_overflows_into_its_interrupt()
    {
        SetTimer(1, 1, 0x60); // overflow IRQ, repeat
        SetTimer(1, 0, 0xFFF0);
        _h.Run(0x20);
        (_h.Iop.InterruptStatus & (1u << 5)).Should().NotBe(0);
        Timer(1, 0).Should().Be(0x10u);
        (Timer(1, 1) & 0x1000).Should().NotBe(0);
    }

    [Fact]
    public void Without_repeat_a_timer_interrupts_once()
    {
        SetTimer(0, 2, 10);
        SetTimer(0, 1, 0x10);
        _h.Run(50);
        (_h.Iop.InterruptStatus & (1u << 4)).Should().NotBe(0);
        _h.Iop.Write32(IStat, 0);
        _h.Run(0x20000);
        (_h.Iop.InterruptStatus & (1u << 4)).Should().Be(0);
        (Timer(0, 1) & 0x400).Should().Be(0, "bit 10 clears after the one interrupt");
    }

    [Fact]
    public void Timer_1_can_count_h_blanks_and_timer_0_pauses_in_them()
    {
        SetTimer(1, 1, 0x100);
        SetTimer(0, 1, 0x001); // gate on H-blank, mode 0: count outside it
        for (var i = 0; i < 5; i++)
        {
            _h.Iop.SetHBlank(true);
            _h.Run(100);
            _h.Iop.SetHBlank(false);
            _h.Run(300);
        }

        Timer(1, 0).Should().Be(5u);
        Timer(0, 0).Should().Be(1500u);
    }

    [Fact]
    public void Gate_mode_1_clears_at_the_end_of_the_blank()
    {
        SetTimer(1, 1, 0x003); // gate on V-blank, mode 1
        _h.Run(500);
        _h.Iop.SetVBlank(true);
        _h.Run(100);
        Timer(1, 0).Should().Be(600u);
        _h.Iop.SetVBlank(false);
        _h.Run(10);
        Timer(1, 0).Should().Be(10u);
        (_h.Iop.InterruptStatus & 1).Should().NotBe(0, "V-blank start is IRQ 0");
        (_h.Iop.InterruptStatus & (1u << 11)).Should().NotBe(0, "V-blank end is IRQ 11");
    }
}
