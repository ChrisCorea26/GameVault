using FluentAssertions;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>The PlayStation's memory map, interrupt controller and GPU transfers (SPEC §7.11, Phase 15).</summary>
public sealed class PlayStationConsoleTests
{
    private const uint Origin = 0x80010000;

    private static PlayStationConsole Console()
    {
        var console = new PlayStationConsole();
        console.Power();
        return console;
    }

    /// <summary>A PS-EXE with <paramref name="program"/> loaded and started at $80010000.</summary>
    internal static byte[] Exe(params uint[] program)
    {
        var data = new byte[PsExe.HeaderSize + program.Length * 4];
        "PS-X EXE"u8.CopyTo(data);
        BitConverter.GetBytes(Origin).CopyTo(data, 0x10);
        BitConverter.GetBytes(Origin).CopyTo(data, 0x18);
        BitConverter.GetBytes((uint)(program.Length * 4)).CopyTo(data, 0x1C);
        for (var i = 0; i < program.Length; i++)
        {
            BitConverter.GetBytes(program[i]).CopyTo(data, PsExe.HeaderSize + i * 4);
        }

        return data;
    }

    private static PlayStationConsole Run(params uint[] program)
    {
        var console = Console();
        console.LoadExe(PsExe.Parse(Exe(program)));
        for (var i = 0; i < program.Length; i++)
        {
            console.Cpu.Step();
        }

        return console;
    }

    [Fact]
    public void Ps_exe_headers_give_the_entry_point_and_a_default_stack()
    {
        var exe = PsExe.Parse(Exe(Mips.Nop));

        exe.Pc.Should().Be(Origin);
        exe.LoadAddress.Should().Be(Origin);
        exe.Stack.Should().Be(PsExe.DefaultStack);
        PsExe.IsPsExe(new byte[0x800]).Should().BeFalse();
    }

    [Fact]
    public void Ram_is_mirrored_through_kuseg_kseg0_and_kseg1()
    {
        var console = Run(
            Mips.Lui(1, 0xA060), // KSEG1, fourth 2 MB mirror
            Mips.Ori(2, 0, 0x1234),
            Mips.Sw(2, 1, 0x100),
            Mips.Lui(3, 0x8000),
            Mips.Lw(4, 3, 0x100),
            Mips.Nop);

        console.Cpu.GetRegister(4).Should().Be(0x1234);
    }

    [Fact]
    public void The_scratchpad_holds_data()
    {
        var console = Run(
            Mips.Lui(1, 0x1F80),
            Mips.Ori(2, 0, 0x55),
            Mips.Sw(2, 1, 0x3FC),
            Mips.Lw(3, 1, 0x3FC),
            Mips.Nop);

        console.Cpu.GetRegister(3).Should().Be(0x55);
    }

    [Fact]
    public void Interrupts_reach_the_cpu_through_i_mask_and_are_acknowledged_in_i_stat()
    {
        var console = Run(
            Mips.Lui(1, 0x1F80),
            Mips.Ori(2, 0, (int)PsInterrupt.VBlank),
            Mips.Sw(2, 1, 0x1074)); // I_MASK = VBlank

        console.RaiseInterrupt(PsInterrupt.VBlank | PsInterrupt.Dma);
        console.Cpu.InterruptLine.Should().BeTrue();
        console.Read32(0x1F801070).Should().Be((uint)(PsInterrupt.VBlank | PsInterrupt.Dma));

        console.Write32(0x1F801070, ~(uint)PsInterrupt.VBlank);
        console.Cpu.InterruptLine.Should().BeFalse();
        console.Read32(0x1F801070).Should().Be((uint)PsInterrupt.Dma);
    }

    [Fact]
    public void Byte_stores_to_32_bit_registers_deliver_the_whole_cpu_register()
    {
        // ps1-tests cpu/io-access-bitwidth: a byte store of $12345678 writes the whole value.
        var console = Run(
            Mips.Lui(1, 0x1F80),
            Mips.Lui(2, 0x1234),
            Mips.Ori(2, 2, 0x0678),
            Mips.I(0x28, 1, 2, 0x1074)); // SB to I_MASK

        console.Read32(0x1F801074).Should().Be(0x678, "I_MASK keeps its 11 bits of the whole value");
    }

    [Fact]
    public void Expansion_2_reads_as_open_bus()
    {
        Console().Read32(0x1F802000).Should().Be(0xFFFFFFFF);
    }

    [Fact]
    public void The_gpu_fills_copies_and_reads_back_vram()
    {
        var gpu = Console().Gpu;
        gpu.WriteGp0(0x020000F8); // fill with red (R = $F8)
        gpu.WriteGp0(0x00100010); // at (16, 16)
        gpu.WriteGp0(0x00020010); // 16×2

        gpu.Pixel(16, 16).Should().Be(0x001F);
        gpu.Pixel(31, 17).Should().Be(0x001F);
        gpu.Pixel(32, 16).Should().Be(0);

        gpu.WriteGp0(0xA0000000); // CPU→VRAM at (100, 50), 2×1
        gpu.WriteGp0(0x00320064);
        gpu.WriteGp0(0x00010002);
        gpu.WriteGp0(0x7C0003E0);
        gpu.Pixel(100, 50).Should().Be(0x03E0);
        gpu.Pixel(101, 50).Should().Be(0x7C00);

        gpu.WriteGp0(0x80000000); // VRAM→VRAM: (100, 50) → (200, 60), 2×1
        gpu.WriteGp0(0x00320064);
        gpu.WriteGp0(0x003C00C8);
        gpu.WriteGp0(0x00010002);
        gpu.Pixel(201, 60).Should().Be(0x7C00);

        gpu.WriteGp0(0xC0000000); // VRAM→CPU
        gpu.WriteGp0(0x003C00C8);
        gpu.WriteGp0(0x00010002);
        gpu.ReadData().Should().Be(0x7C0003E0);
    }

    [Fact]
    public void Draw_commands_are_consumed_whole_so_the_stream_stays_in_step()
    {
        var gpu = Console().Gpu;
        gpu.WriteGp0(0x3C000000); // Gouraud textured quad: 12 words
        for (var i = 0; i < 11; i++)
        {
            gpu.WriteGp0(0x02000000); // parameters that would be fills if misread
        }

        gpu.WriteGp0(0x48FFFFFF); // flat polyline with three vertices, then the terminator
        gpu.WriteGp0(0);
        gpu.WriteGp0(0x00100010);
        gpu.WriteGp0(0x00200020);
        gpu.WriteGp0(0x55555555);

        gpu.WriteGp0(0xE6000001); // set mask
        (gpu.Status & (1u << 11)).Should().NotBe(0, "the mask command after them was read as a command");
    }

    [Fact]
    public void Gp1_sets_the_display_mode_reported_in_gpustat()
    {
        var gpu = Console().Gpu;
        gpu.WriteGp1(0x08000009); // 320 wide, PAL
        gpu.WriteGp1(0x03000000); // display on
        gpu.WriteGp1(0x04000002); // DMA CPU→GP0

        var status = gpu.Status;
        ((status >> 17) & 0x3F).Should().Be(0x09);
        (status & (1u << 23)).Should().Be(0);
        ((status >> 29) & 3).Should().Be(2);
        gpu.PalMode.Should().BeTrue();
    }

    [Fact]
    public void Save_states_round_trip_the_machine()
    {
        var console = Run(Mips.Ori(1, 0, 7), Mips.Lui(2, 0x8000), Mips.Sw(1, 2, 0x200));
        console.Gpu.WriteGp0(0x0200FF00);
        console.Gpu.WriteGp0(0);
        console.Gpu.WriteGp0(0x00010010);

        var writer = new StateWriter();
        console.SaveState(writer);
        var copy = Console();
        copy.LoadState(new StateReader(writer.ToArray()));

        copy.Cpu.Pc.Should().Be(console.Cpu.Pc);
        copy.Cpu.Cycles.Should().Be(console.Cpu.Cycles);
        copy.Ram[0x200].Should().Be(7);
        copy.Gpu.Pixel(0, 0).Should().Be(0x03E0);
    }
}
