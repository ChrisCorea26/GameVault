using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;

namespace GameVault.Core.Tests.Cartridge;

/// <summary>Tier 4 boards (SPEC §4.5, Phase 7): banking, IRQs and state.</summary>
public class Tier4MapperTests
{
    private static INesRom Rom(int mapper, int prg8K, int chr1K, int submapper = 0, bool battery = false) =>
        INesRom.Parse(BankedRom.Build(mapper, prg8K, chr1K, submapper, battery));

    [Fact]
    public void CpRom_switches_the_upper_4k_of_16k_chr_ram()
    {
        var mapper = new CpRom(Rom(13, 4, 0));
        mapper.PpuWrite(0x0000, 0x11);
        mapper.PpuRead(0x1000).Should().Be(0x11, "at power-on $1000 also shows bank 0");

        // The ROM byte at $A000 is 1, so the bus conflict keeps the value 1.
        mapper.CpuWrite(0xA000, 0x01);
        mapper.PpuRead(0x1000).Should().Be(0, "bank 1 is separate RAM");
        mapper.PpuWrite(0x1000, 0x55);
        mapper.PpuRead(0x0000).Should().Be(0x11, "$0000 is fixed to bank 0");

        mapper.CpuWrite(0x8000, 0x00);
        mapper.PpuRead(0x1000).Should().Be(0x11);
        mapper.CpuWrite(0xA000, 0x01);
        mapper.PpuRead(0x1000).Should().Be(0x55);
    }

    [Fact]
    public void Unrom512_banks_prg_and_chr_ram_without_conflicts_when_flashable()
    {
        var mapper = new Unrom512(Rom(30, 16, 0, battery: true));
        mapper.CpuWrite(0x8000, 0x63); // PRG 3, CHR 3

        mapper.CpuRead(0x8000, 0).Should().Be(6);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.PpuWrite(0x0000, 0x42);
        mapper.CpuWrite(0x8000, 0x03);
        mapper.PpuRead(0x0000).Should().Be(0, "CHR bank 0 is a different 8 KB");
        mapper.CpuWrite(0x8000, 0x63);
        mapper.PpuRead(0x0000).Should().Be(0x42);
    }

    [Theory]
    [InlineData(0, 0x02, 4, 7)]  // 32 KB: banks 2, 3 -> 8 KB 4 and 7
    [InlineData(1, 0x01, 2, 15)] // UNROM-like: 1 then 7
    [InlineData(2, 0x83, 7, 7)]  // 8 KB 7 everywhere
    [InlineData(3, 0x02, 4, 4)]  // 16 KB mirrored
    public void Mapper15_modes(int mode, byte value, int at8000, int atC000)
    {
        var mapper = new Mapper15(Rom(15, 16, 0));
        mapper.CpuWrite((ushort)(0x8000 | mode), value);

        mapper.CpuRead(0x8000, 0).Should().Be((byte)at8000);
        mapper.CpuRead(0xC000, 0).Should().Be((byte)(mode == 1 ? 14 : atC000 - (mode == 0 ? 1 : 0)));
    }

    [Fact]
    public void Irem_g101_swaps_the_prg_layout()
    {
        var mapper = new IremG101(Rom(32, 16, 64));
        mapper.CpuWrite(0x8000, 3);
        mapper.CpuWrite(0xA000, 5);
        mapper.CpuWrite(0xB003, 9);

        mapper.CpuRead(0x8000, 0).Should().Be(3);
        mapper.CpuRead(0xA000, 0).Should().Be(5);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.PpuRead(0x0C00).Should().Be(9);

        mapper.CpuWrite(0x9000, 3);
        mapper.CpuRead(0x8000, 0).Should().Be(14);
        mapper.CpuRead(0xC000, 0).Should().Be(3);
        mapper.Mirroring.Should().Be(Mirroring.Horizontal);
    }

    [Fact]
    public void Taito_tc0190_banks_and_tc0690_counts_scanlines()
    {
        var tc0190 = new TaitoTc0190(Rom(33, 16, 64));
        tc0190.CpuWrite(0x8000, 0x42);
        tc0190.CpuWrite(0x8002, 3); // 2 KB bank 3 -> 1 KB 6
        tc0190.CpuWrite(0xA003, 11);
        tc0190.CpuRead(0x8000, 0).Should().Be(2);
        tc0190.Mirroring.Should().Be(Mirroring.Horizontal);
        tc0190.PpuRead(0x0000).Should().Be(6);
        tc0190.PpuRead(0x1C00).Should().Be(11);
        tc0190.HasCycleHooks.Should().BeFalse();

        var tc0690 = new TaitoTc0190(Rom(48, 16, 64));
        tc0690.CpuWrite(0xC000, 0xFD); // latch 2 (written inverted)
        tc0690.CpuWrite(0xC001, 0);
        tc0690.CpuWrite(0xC002, 0);
        for (var line = 0; line < 3; line++)
        {
            tc0690.IrqPending.Should().BeFalse();
            for (var i = 0; i < 8; i++)
            {
                tc0690.OnCpuCycle();
            }

            tc0690.OnA12Fall();
            for (var i = 0; i < 8; i++)
            {
                tc0690.OnCpuCycle();
            }

            tc0690.OnA12Rise();
        }

        tc0690.IrqPending.Should().BeTrue("reload, then 2, 1, 0");
        tc0690.CpuWrite(0xC003, 0);
        tc0690.IrqPending.Should().BeFalse();
    }

    [Fact]
    public void Irem_h3001_counts_cpu_cycles_down()
    {
        var mapper = new CycleIrqBoard(Rom(65, 16, 64));
        mapper.CpuWrite(0x8000, 4);
        mapper.CpuWrite(0xB002, 7);
        mapper.CpuRead(0x8000, 0).Should().Be(4);
        mapper.PpuRead(0x0800).Should().Be(7);

        mapper.CpuWrite(0x9005, 0x00);
        mapper.CpuWrite(0x9006, 10);
        mapper.CpuWrite(0x9004, 0);
        mapper.CpuWrite(0x9003, 0x80);
        for (var i = 0; i < 9; i++)
        {
            mapper.OnCpuCycle();
        }

        mapper.IrqPending.Should().BeFalse();
        mapper.OnCpuCycle();
        mapper.IrqPending.Should().BeTrue();
    }

    [Fact]
    public void Sunsoft3_irq_fires_when_the_counter_wraps()
    {
        var mapper = new CycleIrqBoard(Rom(67, 16, 64));
        mapper.CpuWrite(0xF800, 3);
        mapper.CpuWrite(0x9800, 5); // 2 KB bank 5 at $0800 -> 1 KB 10
        mapper.CpuRead(0x8000, 0).Should().Be(6);
        mapper.PpuRead(0x0800).Should().Be(10);

        mapper.CpuWrite(0xC800, 0x00); // high
        mapper.CpuWrite(0xC800, 0x04); // low
        mapper.CpuWrite(0xD800, 0x10);
        for (var i = 0; i < 5; i++)
        {
            mapper.IrqPending.Should().BeFalse();
            mapper.OnCpuCycle();
        }

        mapper.IrqPending.Should().BeTrue();
    }

    [Fact]
    public void Vrc3_counts_up_to_overflow_and_reloads()
    {
        var mapper = new Vrc3(Rom(73, 16, 0));
        mapper.CpuWrite(0xF000, 2);
        mapper.CpuRead(0x8000, 0).Should().Be(4);

        // Latch $FFFD: three cycles to overflow.
        mapper.CpuWrite(0x8000, 0x0D);
        mapper.CpuWrite(0x9000, 0x0F);
        mapper.CpuWrite(0xA000, 0x0F);
        mapper.CpuWrite(0xB000, 0x0F);
        mapper.CpuWrite(0xC000, 0x02);
        mapper.OnCpuCycle();
        mapper.OnCpuCycle();
        mapper.IrqPending.Should().BeFalse();
        mapper.OnCpuCycle();
        mapper.IrqPending.Should().BeTrue();
        mapper.CpuWrite(0xD000, 0);
        mapper.IrqPending.Should().BeFalse();
    }

    [Fact]
    public void Vrc1_uses_the_fifth_chr_bit_from_9000()
    {
        var mapper = new Vrc1(Rom(75, 16, 128));
        mapper.CpuWrite(0xC000, 6);
        mapper.CpuWrite(0x9000, 0x05); // horizontal, CHR1 high bit
        mapper.CpuWrite(0xF000, 0x01);

        mapper.CpuRead(0xC000, 0).Should().Be(6);
        mapper.Mirroring.Should().Be(Mirroring.Horizontal);
        mapper.PpuRead(0x1000).Should().Be(68 & 0xFF, "4 KB bank 17 is 1 KB bank 68");
    }

    [Fact]
    public void Namco_108_variants_bank_chr_their_own_way()
    {
        var m76 = new Namco108Variant(Rom(76, 16, 64));
        m76.CpuWrite(0x8000, 2);
        m76.CpuWrite(0x8001, 5);
        m76.PpuRead(0x0000).Should().Be(10, "2 KB banks");

        var m88 = new Namco108Variant(Rom(88, 16, 128));
        m88.CpuWrite(0x8000, 2);
        m88.CpuWrite(0x8001, 3);
        m88.PpuRead(0x1000).Should().Be(67, "the right table comes from the upper 64 KB");

        var m154 = new Namco108Variant(Rom(154, 16, 128));
        m154.CpuWrite(0x8000, 0x40);
        m154.Mirroring.Should().Be(Mirroring.SingleScreenB);

        var m95 = new Namco108Variant(Rom(95, 16, 64));
        m95.CpuWrite(0x8000, 0);
        m95.CpuWrite(0x8001, 0x20);
        m95.NametablePage(0).Should().Be(1);
        m95.NametablePage(2).Should().Be(0);
    }

    [Fact]
    public void Discrete_boards_152_180_184_232()
    {
        var m152 = new Mapper152(Rom(152, 16, 64));
        m152.CpuWrite(0xC000, 0xFF); // ROM at $C000 is bank 14 = $0E: conflict -> $0E
        m152.PpuRead(0x0000).Should().Be(48, "CHR bank 14 wraps to 6 of the 8 banks");
        m152.CpuRead(0x8000, 0).Should().Be(0);

        var m180 = new Mapper180(Rom(180, 16, 0));
        m180.CpuWrite(0x8000, 3);
        m180.CpuRead(0x8000, 0).Should().Be(0);
        m180.CpuRead(0xC000, 0).Should().Be(6);

        var m184 = new Sunsoft1(Rom(184, 4, 32));
        m184.CpuWrite(0x6000, 0x52);
        m184.PpuRead(0x0000).Should().Be(8);
        m184.PpuRead(0x1000).Should().Be(20);

        var m232 = new Quattro(Rom(232, 32, 0));
        m232.CpuWrite(0x8000, 0x08);
        m232.CpuWrite(0xC000, 0x02);
        m232.CpuRead(0x8000, 0).Should().Be(12);
        m232.CpuRead(0xC000, 0).Should().Be(14);
    }

    [Fact]
    public void Mapper185_disables_chr_until_the_right_value()
    {
        var mapper = new Mapper185(Rom(185, 4, 8, submapper: 6));
        mapper.PpuRead(0x0000).Should().Be(0xFF);
        mapper.CpuWrite(0x8000, 0x02); // ROM byte is 0, so the conflict would mask it...
        mapper.PpuRead(0x0000).Should().Be(0xFF, "the bus conflict wins");

        var open = new Mapper185(Rom(185, 4, 8, submapper: 4));
        open.PpuRead(0x0000).Should().Be(0, "submapper 4 enables on 0");
    }

    [Fact]
    public void Taito_x1005_registers_and_ram()
    {
        var mapper = new TaitoX1005(Rom(80, 16, 128));
        mapper.CpuWrite(0x7EFA, 3);
        mapper.CpuWrite(0x7EF0, 0x0A); // 2 KB bank 5 -> 1 KB 10
        mapper.CpuWrite(0x7EF5, 30);
        mapper.CpuWrite(0x7EF6, 1);

        mapper.CpuRead(0x8000, 0).Should().Be(3);
        mapper.PpuRead(0x0000).Should().Be(10);
        mapper.PpuRead(0x1C00).Should().Be(30);
        mapper.Mirroring.Should().Be(Mirroring.Vertical);

        mapper.CpuWrite(0x7F10, 0x99);
        mapper.CpuRead(0x7F10, 0x12).Should().Be(0x12, "RAM is locked");
        mapper.CpuWrite(0x7EF8, 0xA3);
        mapper.CpuWrite(0x7F10, 0x99);
        mapper.CpuRead(0x7F10, 0).Should().Be(0x99);

        var m207 = new TaitoX1005(Rom(207, 16, 128));
        m207.CpuWrite(0x7EF1, 0x80);
        m207.NametablePage(2).Should().Be(1);
        m207.NametablePage(0).Should().Be(0);
    }

    public static TheoryData<int, int, int, int> Boards { get; } = new()
    {
        { 13, 4, 0, 0 },
        { 15, 16, 0, 0 },
        { 30, 16, 0, 0 },
        { 32, 16, 64, 0 },
        { 33, 16, 64, 0 },
        { 48, 16, 64, 0 },
        { 65, 16, 64, 0 },
        { 67, 16, 64, 0 },
        { 73, 16, 0, 0 },
        { 75, 16, 128, 0 },
        { 76, 16, 64, 0 },
        { 80, 16, 128, 0 },
        { 88, 16, 128, 0 },
        { 95, 16, 64, 0 },
        { 152, 16, 64, 0 },
        { 154, 16, 128, 0 },
        { 180, 16, 0, 0 },
        { 184, 4, 32, 0 },
        { 185, 4, 8, 0 },
        { 207, 16, 128, 0 },
        { 232, 32, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Boards))]
    public void Console_state_round_trips(int mapper, int prg8K, int chr1K, int submapper)
    {
        var image = BankedRom.Build(mapper, prg8K, chr1K, submapper);
        var original = NesConsole.FromBytes(image);
        original.Power();
        for (var i = 0; i < 15; i++)
        {
            original.RunFrame();
        }

        var saved = original.SaveSnapshot();
        for (var i = 0; i < 15; i++)
        {
            original.RunFrame();
        }

        var restored = NesConsole.FromBytes(image);
        restored.LoadSnapshot(saved);
        for (var i = 0; i < 15; i++)
        {
            restored.RunFrame();
        }

        restored.Ppu.FrameBuffer.ToArray().Should().Equal(original.Ppu.FrameBuffer.ToArray());
        restored.SaveSnapshot().Should().Equal(original.SaveSnapshot());
    }
}
