using GameVault.Core.Cpu.W65816;
using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>The SA-1 (outstanding-work plan §5), with programs written here for both CPUs.</summary>
public sealed class Sa1Tests
{
    /// <summary>LDA #value; STA absolute.</summary>
    private static byte[] Store(int address, byte value) => [0xA9, value, 0x8D, (byte)address, (byte)(address >> 8)];

    // The SNES CPU: point the SA-1 at $9000, release it, wait for $5A in I-RAM, then copy what it left into WRAM.
    private static readonly byte[] SnesProgram =
    [
        0x18, 0xFB,
        .. Store(0x2203, 0x00), .. Store(0x2204, 0x90), .. Store(0x2200, 0x00),
        0xAD, 0x00, 0x30, 0xC9, 0x5A, 0xD0, 0xF9, // wait: LDA $3000; CMP #$5A; BNE wait
        0xAD, 0x01, 0x30, 0x85, 0x10,             // LDA $3001; STA $10
        0xAD, 0x00, 0x23, 0x85, 0x11,             // LDA $2300; STA $11 (SFR)
        0x80, 0xFE,
    ];

    // The SA-1: -3 × 7 on the multiplier, a byte into BW-RAM, a message and interrupt to the SNES, then the flag.
    private static readonly byte[] Sa1Program =
    [
        0x18, 0xFB,
        .. Store(0x2250, 0x00), .. Store(0x2251, 0xFD), .. Store(0x2252, 0xFF), .. Store(0x2253, 0x07), .. Store(0x2254, 0x00),
        0xAD, 0x06, 0x23, 0x8D, 0x01, 0x30,       // LDA $2306; STA $3001
        0xA9, 0x77, 0x8F, 0x10, 0x00, 0x40,       // LDA #$77; STA $400010
        .. Store(0x2209, 0x85),                    // IRQ to the SNES, message 5
        .. Store(0x3000, 0x5A),
        0x80, 0xFE,
    ];

    private static byte[] Rom()
    {
        var rom = SnesTests.Rom(SnesProgram, chip: 0x35, ramSize: 3);
        Sa1Program.CopyTo(rom, 0x1000);
        return rom;
    }

    [Fact]
    public void Both_cpus_run_and_talk_through_i_ram_bw_ram_and_the_message_registers()
    {
        var cartridge = new SnesCartridge(Rom());
        cartridge.Coprocessor.Should().BeOfType<Sa1>();
        cartridge.HasBattery.Should().BeTrue();
        var console = new SnesConsole(cartridge);
        console.Power();

        console.RunFrame();

        console.Wram[0x10].Should().Be(0xEB, "-21 from the multiplier");
        console.Wram[0x11].Should().Be(0x85, "SFR: the SA-1's interrupt flag and message 5");
        cartridge.BatteryRam[0x10].Should().Be(0x77, "BW-RAM is the save file");
        cartridge.Coprocessor!.Irq.Should().BeFalse("the SNES hasn't enabled the SA-1's interrupt");
    }

    [Fact]
    public void A_save_state_with_the_sa1_resumes_exactly()
    {
        var console = new SnesConsole(new SnesCartridge(Rom()));
        console.Power();
        console.RunFrame();
        var state = console.SaveSnapshot();
        console.RunFrame();
        var after = console.SaveSnapshot();

        var copy = new SnesConsole(new SnesCartridge(Rom()));
        copy.Power();
        copy.LoadSnapshot(state);
        copy.RunFrame();

        copy.SaveSnapshot().Should().Equal(after);
    }

    private static Sa1 Chip(int romSize = 0x10000)
    {
        var sa1 = new Sa1(new byte[romSize], 0x2000, pal: false);
        sa1.Power();
        return sa1;
    }

    [Fact]
    public void The_super_mmc_maps_each_quarter_to_its_chosen_megabyte()
    {
        var rom = new byte[0x400000];
        for (var block = 0; block < 4; block++)
        {
            rom.AsSpan(block * 0x100000, 0x100000).Fill((byte)block);
        }

        var sa1 = new Sa1(rom, 0x2000, pal: false);
        sa1.Power();
        sa1.Read(0xA08000, 0).Should().Be(3, "FXB starts on the fourth megabyte");
        sa1.Read(0xE00000, 0).Should().Be(2);

        sa1.Write(0x2222, 0x81); // EXB: megabyte 1, also in $80–$9F
        sa1.Read(0x808000, 0).Should().Be(1);
        sa1.Read(0xE00000, 0).Should().Be(1);
        sa1.Write(0x2222, 0x01); // bit 7 clear: $80–$9F keep their own megabyte
        sa1.Read(0x808000, 0).Should().Be(2);
    }

    [Theory]
    [InlineData(-7, 2, -4, 1)]
    [InlineData(7, 2, 3, 1)]
    [InlineData(-8, 4, -2, 0)]
    public void Division_is_signed_by_unsigned_with_a_positive_remainder(int dividend, int divisor, int quotient, int remainder)
    {
        var sa1 = Chip();
        sa1.Write(0x2250, 0x01);
        sa1.Write(0x2251, (byte)dividend);
        sa1.Write(0x2252, (byte)(dividend >> 8));
        sa1.Write(0x2253, (byte)divisor);
        sa1.Write(0x2254, (byte)(divisor >> 8));

        var q = (short)(sa1.Read(0x2306, 0) | sa1.Read(0x2307, 0) << 8);
        var r = sa1.Read(0x2308, 0) | sa1.Read(0x2309, 0) << 8;
        q.Should().Be((short)quotient);
        r.Should().Be(remainder);
    }

    [Fact]
    public void The_sum_of_products_accumulates_forty_bits()
    {
        var sa1 = Chip();
        sa1.Write(0x2250, 0x02);
        for (var i = 0; i < 3; i++)
        {
            sa1.Write(0x2251, 0x00);
            sa1.Write(0x2252, 0x40); // 16384
            sa1.Write(0x2253, 0x00);
            sa1.Write(0x2254, 0x40);
        }

        long result = 0;
        for (var i = 0; i < 5; i++)
        {
            result |= (long)sa1.Read((uint)(0x2306 + i), 0) << (i * 8);
        }

        result.Should().Be(3L * 16384 * 16384);
        sa1.Read(0x230B, 0).Should().Be(0);
    }

    [Fact]
    public void Variable_length_reads_step_through_rom_bits()
    {
        var rom = new byte[0x10000];
        rom[0] = 0b1010_0101;
        rom[1] = 0b0011_1100;
        var sa1 = new Sa1(rom, 0x2000, pal: false);
        sa1.Power();
        sa1.Write(0x2259, 0x00);
        sa1.Write(0x225A, 0x80);
        sa1.Write(0x225B, 0x00); // $008000
        sa1.Write(0x2258, 0x84); // 4 bits a read, advancing on $230D

        sa1.Read(0x230C, 0).Should().Be(0xA5);
        sa1.Read(0x230D, 0).Should().Be(0x3C);
        sa1.Read(0x230C, 0).Should().Be(0xCA, "four bits later");
    }

    [Fact]
    public void The_bitmap_view_packs_pixels_into_bw_ram()
    {
        var sa1 = Chip();
        var bus = (IW65816Bus)sa1;
        bus.Write(0x223F, 0x80); // 2 bits a pixel
        bus.Write(0x600000, 1);
        bus.Write(0x600001, 2);
        bus.Write(0x600003, 3);
        sa1.BwRam[0].Should().Be(0b11_00_10_01);
        bus.Read(0x600001).Should().Be(2);

        bus.Write(0x223F, 0x00); // 4 bits a pixel
        bus.Write(0x600005, 0xC);
        sa1.BwRam[2].Should().Be(0xC0);
    }

    [Fact]
    public void Character_conversion_2_turns_a_row_of_pixels_into_bitplanes()
    {
        var sa1 = Chip();
        sa1.Write(0x2230, 0xA0); // DMA on, character conversion, type 2
        sa1.Write(0x2231, 0x02); // 2 bits a pixel
        sa1.Write(0x2235, 0x00);
        sa1.Write(0x2236, 0x01); // I-RAM $100
        for (var x = 0; x < 8; x++)
        {
            ((IW65816Bus)sa1).Write((uint)(0x2240 + x), (byte)(x & 3));
        }

        sa1.IRam[0x100].Should().Be(0x55, "plane 0: the pixels with bit 0 set");
        sa1.IRam[0x101].Should().Be(0x33);
    }

    [Fact]
    public void Character_conversion_1_converts_tiles_as_the_snes_dma_reads_them()
    {
        var sa1 = Chip();
        sa1.BwRam[0] = 0b11_10_01_00; // pixels 0–3 of row 0: 0, 1, 2, 3
        sa1.BwRam[1] = 0b11_10_01_00;
        sa1.Write(0x2201, 0x20);      // the SNES takes the conversion's interrupt
        sa1.Write(0x2230, 0xB0);      // DMA on, character conversion, type 1
        sa1.Write(0x2231, 0x02);      // 2 bits a pixel, one tile across
        sa1.Write(0x2232, 0x00);
        sa1.Write(0x2233, 0x00);
        sa1.Write(0x2234, 0x00);
        sa1.Write(0x2235, 0x00);
        sa1.Write(0x2236, 0x30);      // I-RAM $3000: starts it

        sa1.Irq.Should().BeTrue();
        sa1.Read(0x400000, 0).Should().Be(0x55);
        sa1.Read(0x400001, 0).Should().Be(0x33);

        sa1.Write(0x2231, 0x82);      // the end
        sa1.Read(0x400000, 0).Should().Be(0b11_10_01_00, "plain BW-RAM again");
    }
}
