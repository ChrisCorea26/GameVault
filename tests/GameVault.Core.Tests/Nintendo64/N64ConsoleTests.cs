using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.Cpu.R4300;
using GameVault.Core.Input;
using GameVault.Core.Nintendo64;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>MIPS instruction encodings for hand-assembled test programs.</summary>
internal static class Mips
{
    public static uint R(int function, int rd, int rs, int rt, int sa = 0) =>
        (uint)((rs << 21) | (rt << 16) | (rd << 11) | (sa << 6) | function);

    public static uint I(int opcode, int rt, int rs, int immediate) =>
        (uint)((opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFF));

    public static uint Addiu(int rt, int rs, int immediate) => I(0x09, rt, rs, immediate);

    public static uint Addi(int rt, int rs, int immediate) => I(0x08, rt, rs, immediate);

    public static uint Ori(int rt, int rs, int immediate) => I(0x0D, rt, rs, immediate);

    public static uint Lui(int rt, int immediate) => I(0x0F, rt, 0, immediate);

    public static uint Lw(int rt, int rs, int offset) => I(0x23, rt, rs, offset);

    public static uint Sw(int rt, int rs, int offset) => I(0x2B, rt, rs, offset);

    public static uint Ld(int rt, int rs, int offset) => I(0x37, rt, rs, offset);

    public static uint Sd(int rt, int rs, int offset) => I(0x3F, rt, rs, offset);

    /// <summary>A branch to <paramref name="offset"/> instructions after its delay slot.</summary>
    public static uint Beq(int rs, int rt, int offset) => I(0x04, rt, rs, offset);

    public static uint Bne(int rs, int rt, int offset) => I(0x05, rt, rs, offset);

    public static uint Beql(int rs, int rt, int offset) => I(0x14, rt, rs, offset);

    public static uint Dsll32(int rd, int rt, int sa) => R(0x3C, rd, 0, rt, sa);

    public static uint Mtc0(int rt, int rd) => (uint)((0x10 << 26) | (0x04 << 21) | (rt << 16) | (rd << 11));

    public static uint Mfc0(int rt, int rd) => (uint)((0x10 << 26) | (rt << 16) | (rd << 11));

    public static uint Tlbwi => 0x4200_0002;

    public static uint Eret => 0x4200_0018;

    public const uint Nop = 0;

    /// <summary><c>j .</c>, relative to the program's base in KSEG1 DMEM.</summary>
    public static uint BranchSelf => 0x1000_FFFF;
}

/// <summary>The Nintendo 64: cartridges, the simulated boot, the VR4300 in place, the PIF and saves.</summary>
public sealed class N64ConsoleTests
{
    /// <summary>
    /// A cartridge whose boot code is <paramref name="program"/>: the simulated PIF copies it to DMEM and runs it from
    /// $A4000040.
    /// </summary>
    internal static byte[] Rom(uint[] program, string name = "GAMEVAULT TEST", int size = 0x10_0000)
    {
        var rom = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(rom, 0x8037_1240);
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(8), 0x8000_0400);
        Encoding.ASCII.GetBytes(name.PadRight(20)).CopyTo(rom, 0x20);
        Encoding.ASCII.GetBytes("NGVE").CopyTo(rom, 0x3B);
        for (var i = 0; i < program.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(0x40 + i * 4), program[i]);
        }

        return rom;
    }

    private static N64Console Run(uint[] program, long cycles = 2000)
    {
        var console = new N64Console(new N64Cartridge(Rom(program)));
        console.Power();
        console.RunCycles(cycles);
        return console;
    }

    [Fact]
    public void Every_byte_order_becomes_big_endian()
    {
        var z64 = Rom([Mips.Addiu(8, 0, 1)]);
        var v64 = new byte[z64.Length];
        var n64 = new byte[z64.Length];
        for (var i = 0; i < z64.Length; i += 4)
        {
            (v64[i], v64[i + 1], v64[i + 2], v64[i + 3]) = (z64[i + 1], z64[i], z64[i + 3], z64[i + 2]);
            (n64[i], n64[i + 1], n64[i + 2], n64[i + 3]) = (z64[i + 3], z64[i + 2], z64[i + 1], z64[i]);
        }

        N64Cartridge.IsN64Rom(v64).Should().BeTrue();
        N64Cartridge.IsN64Rom(n64).Should().BeTrue();
        new N64Cartridge(v64).Rom.Should().Equal(z64);
        new N64Cartridge(n64).Rom.Should().Equal(z64);

        var cartridge = new N64Cartridge(z64);
        cartridge.Name.Should().Be("GAMEVAULT TEST");
        cartridge.GameCode.Should().Be("NGVE");
        cartridge.Region.Should().Be(N64Region.Ntsc);
        cartridge.Cic.Should().Be(N64Cic.Cic6102, "unknown boot code is taken as a 6102");
        cartridge.EntryPoint.Should().Be(0x8000_0400);
    }

    [Fact]
    public void The_homebrew_header_picks_the_save_type()
    {
        var rom = Rom([]);
        rom[0x3C] = (byte)'E';
        rom[0x3D] = (byte)'D';
        rom[0x3F] = 0x50;
        new N64Cartridge(rom).SaveType.Should().Be(N64SaveType.FlashRam);
        rom[0x3F] = 0x30;
        new N64Cartridge(rom).SaveType.Should().Be(N64SaveType.Sram);
        new N64Cartridge(Rom([])).SaveType.Should().Be(N64SaveType.Eeprom4K, "a 4 Kbit EEPROM is the default");
        new N64Cartridge(Rom([]), N64SaveType.Eeprom16K).BatteryRam.Length.Should().Be(2048);
    }

    [Fact]
    public void The_simulated_pif_boot_starts_the_boot_code_in_dmem()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.Addiu(8, 0, 1)])));
        console.Power();

        console.Cpu.Pc.Should().Be(0xFFFF_FFFF_A400_0040);
        console.Cpu.GetRegister(22).Should().Be(0x3F, "s6 holds the 6102's seed");
        console.Cpu.GetRegister(20).Should().Be(1, "s4 is the TV type, NTSC");
        console.Cpu.GetRegister(29).Should().Be(0xFFFF_FFFF_A400_1FF0);
        BinaryPrimitives.ReadUInt32BigEndian(console.Rsp.Dmem.AsSpan(0x40)).Should().Be(Mips.Addiu(8, 0, 1));
        BinaryPrimitives.ReadUInt32BigEndian(console.Rdram.AsSpan(0x318)).Should().Be(8u * 1024 * 1024, "osMemSize: the Expansion Pak");
        console.Read32(0x0470_000C).Should().NotBe(0u, "RI_SELECT says RDRAM is set up, so IPL3 skips that");
        console.Rsp.Halted.Should().BeTrue();
    }

    [Fact]
    public void Branch_delay_slots_run_and_branch_likely_skips_them_when_not_taken()
    {
        var console = Run(
        [
            Mips.Beq(0, 0, 3), // taken
            Mips.Addiu(8, 0, 1), // delay slot: runs
            Mips.Addiu(9, 0, 1), // skipped
            Mips.Addiu(10, 0, 1), // skipped
            Mips.Beql(0, 8, 1), // not taken (r0 != r8): its delay slot is skipped
            Mips.Addiu(13, 0, 1), // nullified
            Mips.Addiu(12, 0, 7),
            Mips.BranchSelf,
            Mips.Nop,
        ]);

        console.Cpu.GetRegister(8).Should().Be(1);
        console.Cpu.GetRegister(9).Should().Be(0);
        console.Cpu.GetRegister(10).Should().Be(0);
        console.Cpu.GetRegister(13).Should().Be(0);
        console.Cpu.GetRegister(12).Should().Be(7);
    }

    [Fact]
    public void Doubleword_registers_hold_64_bits_and_32_bit_results_sign_extend()
    {
        var console = Run(
        [
            Mips.Lui(8, 0x8000), // sign-extended
            Mips.Ori(9, 0, 0x1234),
            Mips.Dsll32(9, 9, 0), // 0x00001234_00000000
            Mips.Addiu(10, 0, -1),
            Mips.BranchSelf,
            Mips.Nop,
        ]);

        console.Cpu.GetRegister(8).Should().Be(0xFFFF_FFFF_8000_0000);
        console.Cpu.GetRegister(9).Should().Be(0x0000_1234_0000_0000);
        console.Cpu.GetRegister(10).Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void Overflow_takes_the_general_exception_with_epc_at_the_instruction()
    {
        var console = new N64Console(new N64Cartridge(Rom(
        [
            Mips.Lui(8, 0x7FFF),
            Mips.Ori(8, 8, 0xFFFF),
            Mips.Addi(9, 8, 1), // overflows: r9 stays 0
            Mips.BranchSelf,
            Mips.Nop,
        ])));
        console.Power();
        console.Cpu.WriteCop0Register(12, 0x3400_0000); // BEV clear: the vector is at $80000180
        BinaryPrimitives.WriteUInt32BigEndian(console.Rdram.AsSpan(0x180), Mips.BranchSelf);
        console.RunCycles(100);

        console.Cpu.GetRegister(9).Should().Be(0);
        ((console.Cpu.Cause >> 2) & 0x1F).Should().Be((uint)R4300Exception.Overflow);
        console.Cpu.Epc.Should().Be(0xFFFF_FFFF_A400_0048);
        console.Cpu.Pc.Should().BeOneOf(0xFFFF_FFFF_8000_0180, 0xFFFF_FFFF_8000_0184);
    }

    [Fact]
    public void The_tlb_maps_a_virtual_page_onto_rdram()
    {
        // Map the even 4 KB page at virtual $00000000 to physical $00100000 (PFN $100), valid and dirty.
        var console = Run(
        [
            Mips.Mtc0(0, 0), // Index 0
            Mips.Mtc0(0, 5), // PageMask: 4 KB
            Mips.Mtc0(0, 10), // EntryHi: VPN2 0, ASID 0
            Mips.Ori(8, 0, (0x100 << 6) | 0b111), // PFN $100, D, V, G
            Mips.Mtc0(8, 2), // EntryLo0
            Mips.Ori(8, 0, 1), // G
            Mips.Mtc0(8, 3), // EntryLo1
            Mips.Tlbwi,
            Mips.Lui(9, 0x1234),
            Mips.Sw(9, 0, 0x10), // virtual $00000010
            Mips.Lw(10, 0, 0x10),
            Mips.BranchSelf,
            Mips.Nop,
        ]);

        BinaryPrimitives.ReadUInt32BigEndian(console.Rdram.AsSpan(0x10_0010)).Should().Be(0x1234_0000);
        console.Cpu.GetRegister(10).Should().Be(0x1234_0000);
    }

    [Fact]
    public void A_tlb_miss_takes_the_refill_vector()
    {
        // Every TLB entry starts zeroed, which matches page 0 as invalid; $70000000 matches none.
        var console = new N64Console(new N64Cartridge(Rom([Mips.Lui(9, 0x7000), Mips.Lw(8, 9, 0x100), Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.Cpu.WriteCop0Register(12, 0x3400_0000);
        BinaryPrimitives.WriteUInt32BigEndian(console.Rdram.AsSpan(0x000), Mips.BranchSelf);
        console.RunCycles(50);

        ((console.Cpu.Cause >> 2) & 0x1F).Should().Be((uint)R4300Exception.TlbLoad);
        console.Cpu.BadVAddr.Should().Be(0x7000_0100);
        console.Cpu.Pc.Should().BeOneOf(0xFFFF_FFFF_8000_0000, 0xFFFF_FFFF_8000_0004);
    }

    [Fact]
    public void Count_reaching_compare_raises_the_timer_interrupt()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.Cpu.WriteCop0Register(12, 0x3400_8001); // IE and IM7, BEV clear
        console.Cpu.WriteCop0Register(9, 0);
        console.Cpu.WriteCop0Register(11, 500);
        BinaryPrimitives.WriteUInt32BigEndian(console.Rdram.AsSpan(0x180), Mips.BranchSelf);
        console.RunCycles(900); // instructions fetched from DMEM take about 23 cycles each
        (console.Cpu.Cause & 0x8000).Should().Be(0u);
        console.RunCycles(300);

        (console.Cpu.Cause & 0x8000).Should().NotBe(0u, "Count ticks at half the CPU clock, reaching 500 after 1,000 cycles");
        ((console.Cpu.Cause >> 2) & 0x1F).Should().Be((uint)R4300Exception.Interrupt);
    }

    [Fact]
    public void Mi_masks_decide_which_rcp_interrupts_reach_the_cpu()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.RaiseInterrupt(N64Interrupt.Vi);
        console.Cpu.InterruptLine.Should().BeFalse();
        console.Write32(0x0430_000C, 0x80); // set the VI mask
        console.Cpu.InterruptLine.Should().BeTrue();
        console.Read32(0x0430_0008).Should().Be((uint)N64Interrupt.Vi);
        console.Write32(0x0440_0010, 0); // writing VI_V_CURRENT clears it
        console.Cpu.InterruptLine.Should().BeFalse();
    }

    [Fact]
    public void A_frame_comes_from_the_vi_framebuffer()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        // 320×240 at 16 bits from $00200000, as libultra sets NTSC low resolution.
        console.Write32(0x0440_0000, 0x0000_3002); // 16-bit
        console.Write32(0x0440_0004, 0x0020_0000);
        console.Write32(0x0440_0008, 320);
        console.Write32(0x0440_0018, 0x20D);
        console.Write32(0x0440_001C, 0xC15);
        console.Write32(0x0440_0024, 0x006C_02EC);
        console.Write32(0x0440_0028, 0x0025_01FF);
        console.Write32(0x0440_0030, 0x200);
        console.Write32(0x0440_0034, 0x400);
        BinaryPrimitives.WriteUInt16BigEndian(console.Rdram.AsSpan(0x20_0000 + (10 * 320 + 5) * 2), 0xF801); // red
        console.RunFrame();

        console.ScreenWidth.Should().Be(320);
        console.ScreenHeight.Should().Be(237);
        console.FrameBuffer[10 * 320 + 5].Should().Be(0x001F, "red in BGR555");
        console.PixelAspect.Should().BeApproximately(4.0 / 3.0 / (320.0 / 237), 1e-9);
    }

    [Fact]
    public void A_32_bit_frame_is_kept_at_full_depth_and_a_16_bit_one_is_not()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.Write32(0x0440_0000, 0x0000_3003); // 32-bit
        console.Write32(0x0440_0004, 0x0020_0000);
        console.Write32(0x0440_0008, 320);
        console.Write32(0x0440_0018, 0x20D);
        console.Write32(0x0440_001C, 0xC15);
        console.Write32(0x0440_0024, 0x006C_02EC);
        console.Write32(0x0440_0028, 0x0025_01FF);
        console.Write32(0x0440_0030, 0x200);
        console.Write32(0x0440_0034, 0x400);
        BinaryPrimitives.WriteUInt32BigEndian(console.Rdram.AsSpan(0x20_0000 + (10 * 320 + 5) * 4), 0x123456FF);
        console.RunFrame();

        console.TrueColorFrame.Length.Should().Be(320 * 237);
        console.TrueColorFrame[10 * 320 + 5].Should().Be(0xFF123456, "every bit of the 8-bit channels");
        console.FrameBuffer[10 * 320 + 5].Should().Be((ushort)((0x12 >> 3) | (0x34 >> 3) << 5 | (0x56 >> 3) << 10));

        console.Write32(0x0440_0000, 0x0000_3002); // back to 16-bit
        console.RunFrame();
        console.TrueColorFrame.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void The_joybus_reads_the_controller_and_its_stick()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.SetControllerState(0, (ControllerState.B | ControllerState.Start | ControllerState.L2 | ControllerState.A).WithStick(40, -80));
        var ram = console.Pif.Ram;
        Array.Clear(ram);
        ram[0] = 1; // tx 1
        ram[1] = 4; // rx 4
        ram[2] = 0x01; // read buttons
        ram[7] = 0xFE;
        ram[63] = 1;
        console.Pif.Written();

        ((N64Buttons)((ram[3] << 8) | ram[4])).Should().Be(N64Buttons.A | N64Buttons.Start | N64Buttons.Z | N64Buttons.CDown);
        ((sbyte)ram[5]).Should().Be(40);
        ((sbyte)ram[6]).Should().Be(-80);
        (ram[63] & 1).Should().Be(0);
    }

    [Fact]
    public void Si_dma_carries_joybus_commands_to_the_pif_and_the_replies_back()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        console.SetControllerState(1, ControllerState.Start.WithStick(-5, 7));

        // In RDRAM: skip port 0, then "read buttons" for port 1, and run the joybus.
        var block = console.Rdram.AsSpan(0x1000, 64);
        block.Clear();
        block[0] = 0;
        block[1] = 1;
        block[2] = 4;
        block[3] = 0x01;
        block[8] = 0xFE;
        block[63] = 1;
        console.Write32(0x0480_0000, 0x1000);
        console.Write32(0x0480_0010, 0x1FC0_07C0); // SI_PIF_AD_WR64B
        (console.Read32(0x0480_0018) & 1).Should().Be(1u, "the SI is busy");
        console.RunCycles(10_000);
        (console.Read32(0x0480_0018) & 0x1000).Should().NotBe(0u, "the SI interrupt");
        console.Write32(0x0480_0018, 0);

        console.Write32(0x0480_0000, 0x2000);
        console.Write32(0x0480_0004, 0x1FC0_07C0); // SI_PIF_AD_RD64B
        console.RunCycles(10_000);
        var reply = console.Rdram.AsSpan(0x2000, 64);
        ((N64Buttons)((reply[4] << 8) | reply[5])).Should().Be(N64Buttons.Start);
        ((sbyte)reply[6]).Should().Be(-5);
        ((sbyte)reply[7]).Should().Be(7);
    }

    [Fact]
    public void An_empty_port_reports_no_device()
    {
        var pif = new N64Pif(null, null) { ConnectedPads = 1 };
        Array.Clear(pif.Ram);
        pif.Ram[0] = 0; // skip port 0
        pif.Ram[1] = 1;
        pif.Ram[2] = 3;
        pif.Ram[3] = 0x00; // info for port 1
        pif.Ram[7] = 0xFE;
        pif.Ram[63] = 1;
        pif.Written();
        (pif.Ram[2] & 0x80).Should().NotBe(0);
    }

    [Fact]
    public void The_eeprom_keeps_blocks_written_over_the_joybus()
    {
        var cartridge = new N64Cartridge(Rom([]), N64SaveType.Eeprom16K);
        var pif = new N64Pif(cartridge, null);
        var ram = pif.Ram;

        Array.Clear(ram);
        ram[0] = 1;
        ram[1] = 3;
        ram[2] = 0x00; // info on channel 4 is reached by skipping four ports
        Array.Clear(ram);
        ram[0] = 0; ram[1] = 0; ram[2] = 0; ram[3] = 0;
        ram[4] = 10; ram[5] = 1; ram[6] = 0x05; ram[7] = 3; // write block 3
        for (var i = 0; i < 8; i++)
        {
            ram[8 + i] = (byte)(0xA0 + i);
        }

        ram[17] = 0xFE;
        ram[63] = 1;
        pif.Written();
        cartridge.BatteryRam.Slice(24, 8).ToArray().Should().Equal(0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7);

        Array.Clear(ram);
        ram[4] = 2; ram[5] = 8; ram[6] = 0x04; ram[7] = 3; // read block 3
        ram[16] = 0xFE;
        ram[63] = 1;
        pif.Written();
        ram.AsSpan(8, 8).ToArray().Should().Equal(0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7);
    }

    [Fact]
    public void The_controller_pak_round_trips_32_bytes_with_their_crc()
    {
        var pak = new N64ControllerPak();
        var pif = new N64Pif(null, pak);
        var ram = pif.Ram;
        Array.Clear(ram);
        ram[0] = 35;
        ram[1] = 1;
        ram[2] = 0x03;
        ram[3] = 0x04; // address $0400 (page 4)
        ram[4] = 0x00;
        for (var i = 0; i < 32; i++)
        {
            ram[5 + i] = (byte)i;
        }

        ram[38] = 0xFE;
        ram[63] = 1;
        pif.Written();
        var writeCrc = ram[37];
        pak.Memory.AsSpan(0x400, 32).ToArray().Should().Equal(Enumerable.Range(0, 32).Select(i => (byte)i));

        Array.Clear(ram);
        ram[0] = 3;
        ram[1] = 33;
        ram[2] = 0x02;
        ram[3] = 0x04;
        ram[4] = 0x00;
        ram[38] = 0xFE;
        ram[63] = 1;
        pif.Written();
        ram.AsSpan(5, 32).ToArray().Should().Equal(Enumerable.Range(0, 32).Select(i => (byte)i));
        ram[37].Should().Be(writeCrc);
        ram[37].Should().Be(N64Pif.DataCrc(ram.AsSpan(5, 32)));
    }

    [Fact]
    public void A_new_controller_pak_is_formatted()
    {
        var pak = new N64ControllerPak();
        var id = pak.Memory.AsSpan(0x20, 32);
        var sum = 0;
        for (var i = 0; i < 28; i += 2)
        {
            sum += (id[i] << 8) | id[i + 1];
        }

        ((id[28] << 8) | id[29]).Should().Be(sum & 0xFFFF);
        ((id[30] << 8) | id[31]).Should().Be((0xFFF2 - (sum & 0xFFFF)) & 0xFFFF);
        pak.Memory.AsSpan(0x60, 32).ToArray().Should().Equal(id.ToArray(), "the ID block has backups");
        pak.Memory[0x100 + 5 * 2 + 1].Should().Be(0x03, "pages from 5 on are free");
    }

    [Fact]
    public void Pi_dma_copies_the_rom_into_rdram_and_raises_the_pi_interrupt()
    {
        var rom = Rom([Mips.BranchSelf, Mips.Nop]);
        for (var i = 0; i < 256; i++)
        {
            rom[0x2000 + i] = (byte)i;
        }

        var console = new N64Console(new N64Cartridge(rom));
        console.Power();
        console.Write32(0x0460_0000, 0x0030_0000);
        console.Write32(0x0460_0004, 0x1000_2000);
        console.Write32(0x0460_000C, 255);
        (console.Read32(0x0460_0010) & 1).Should().Be(1u, "the DMA is busy");
        console.Rdram.AsSpan(0x30_0000, 256).ToArray().Should().Equal(rom.AsSpan(0x2000, 256).ToArray());
        console.RunCycles(20_000);
        (console.Read32(0x0460_0010) & 1).Should().Be(0u);
        (console.Read32(0x0430_0008) & (uint)N64Interrupt.Pi).Should().NotBe(0u);
    }

    [Fact]
    public void Sram_is_reached_through_pi_dma_and_flashram_through_its_commands()
    {
        var sram = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop]), N64SaveType.Sram));
        sram.Power();
        sram.Rdram[0x1000] = 0x5A;
        sram.Write32(0x0460_0000, 0x1000);
        sram.Write32(0x0460_0004, 0x0800_0010);
        sram.Write32(0x0460_0008, 1); // RDRAM to SRAM
        sram.Cartridge.BatteryRam[0x10].Should().Be(0x5A);

        var flash = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop]), N64SaveType.FlashRam));
        flash.Power();
        flash.Write32(0x0801_0000, 0xE100_0000); // status mode
        flash.Read32(0x0800_0000).Should().Be(0x1111_8001u);
        for (var i = 0; i < 128; i++)
        {
            flash.Rdram[0x2000 + i] = (byte)(i + 1);
        }

        flash.Write32(0x0801_0000, 0x4B00_0002); // erase page 2
        flash.Write32(0x0801_0000, 0xD200_0000);
        flash.Write32(0x0801_0000, 0xB400_0000); // write mode
        flash.Write32(0x0460_0000, 0x2000);
        flash.Write32(0x0460_0004, 0x0800_0000);
        flash.Write32(0x0460_0008, 127); // fill the page buffer
        flash.RunCycles(10_000);
        flash.Write32(0x0801_0000, 0xA500_0002); // program page 2
        flash.Write32(0x0801_0000, 0xD200_0000);
        flash.Cartridge.BatteryRam.Slice(256, 128).ToArray().Should().Equal(flash.Rdram.AsSpan(0x2000, 128).ToArray());
    }

    [Fact]
    public void The_ai_plays_queued_buffers_and_asks_for_the_next_one()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        var audio = console.EnableAudio(48000);

        // 4,000 stereo samples of a square wave at $00300000, at about 32 kHz (VI clock / (1520 + 1)).
        for (var i = 0; i < 4000; i++)
        {
            var level = (short)(i / 50 % 2 == 0 ? 8000 : -8000);
            BinaryPrimitives.WriteInt16BigEndian(console.Rdram.AsSpan(0x30_0000 + i * 4), level);
            BinaryPrimitives.WriteInt16BigEndian(console.Rdram.AsSpan(0x30_0000 + i * 4 + 2), level);
        }

        console.Write32(0x0450_0010, 1520); // AI_DACRATE
        console.Write32(0x0450_0008, 1); // DMA on
        console.Write32(0x0450_0000, 0x30_0000);
        console.Write32(0x0450_0004, 8000); // the first half: starts at once
        (console.Read32(0x0430_0008) & (uint)N64Interrupt.Ai).Should().NotBe(0u, "the AI asks for the next buffer as one starts");
        console.Write32(0x0450_000C, 0); // acknowledge
        console.Write32(0x0450_0000, 0x30_0000 + 8000);
        console.Write32(0x0450_0004, 8000); // queued behind it
        (console.Read32(0x0450_000C) & 0xC000_0000).Should().Be(0xC000_0000u, "busy and full");

        console.RunFrame();
        console.RunFrame();
        console.RunFrame();
        console.RunFrame();
        (console.Read32(0x0450_000C) & 0x8000_0000).Should().Be(0u, "the first buffer has finished and the second is playing");
        (console.Read32(0x0430_0008) & (uint)N64Interrupt.Ai).Should().NotBe(0u);

        var samples = new float[48000];
        var count = audio.Read(samples);
        count.Should().BeGreaterThan(2000);
        samples.Take(count).Max().Should().BeGreaterThan(0.15f, "the square wave reached the output");
    }

    [Fact]
    public void The_ai_plays_left_and_right_on_their_own_channels()
    {
        var console = new N64Console(new N64Cartridge(Rom([Mips.BranchSelf, Mips.Nop])));
        console.Power();
        var audio = console.EnableAudio(48000);

        // A square wave on the left only; the right holds a steady negative level.
        for (var i = 0; i < 4000; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(console.Rdram.AsSpan(0x30_0000 + i * 4), (short)(i / 50 % 2 == 0 ? 8000 : -8000));
            BinaryPrimitives.WriteInt16BigEndian(console.Rdram.AsSpan(0x30_0000 + i * 4 + 2), -4096);
        }

        console.Write32(0x0450_0010, 1520);
        console.Write32(0x0450_0008, 1);
        console.Write32(0x0450_0000, 0x30_0000);
        console.Write32(0x0450_0004, 16000);
        console.RunFrame();
        console.RunFrame();

        var frames = new float[96000];
        var count = audio.ReadStereo(frames);
        count.Should().BeGreaterThan(1000);
        var left = Enumerable.Range(0, count).Select(i => frames[2 * i]).ToArray();
        var right = Enumerable.Range(0, count).Select(i => frames[2 * i + 1]).ToArray();
        left[^200..].Max().Should().BeGreaterThan(0.2f, "the square wave plays on the left");
        right[^200..].Should().OnlyContain(r => Math.Abs(r + 0.125f) < 0.01f, "the right holds its own level");
    }

    [Fact]
    public void A_save_state_resumes_exactly()
    {
        // A loop that counts and stores, with the VI running.
        var program = new uint[]
        {
            Mips.Lui(8, 0xA010),
            Mips.Addiu(9, 9, 1),
            Mips.Sw(9, 8, 0),
            Mips.Beq(0, 0, -3),
            Mips.Nop,
        };
        var first = new N64Console(new N64Cartridge(Rom(program)));
        first.Power();
        first.RunFrame();
        var snapshot = first.SaveSnapshot();
        first.RunFrame();
        first.RunFrame();

        var second = new N64Console(new N64Cartridge(Rom(program)));
        second.Power();
        second.LoadSnapshot(snapshot);
        second.RunFrame();
        second.RunFrame();

        second.Cpu.GetRegister(9).Should().Be(first.Cpu.GetRegister(9));
        second.Cpu.Cycles.Should().Be(first.Cpu.Cycles);
        second.Rdram.AsSpan(0x10_0000, 4).ToArray().Should().Equal(first.Rdram.AsSpan(0x10_0000, 4).ToArray());
    }
}
