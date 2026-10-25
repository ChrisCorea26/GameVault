using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cartridge;

/// <summary>Tier 2 boards (SPEC §4.5): banking, latches and state.</summary>
public class Tier2MapperTests
{
    private static INesRom Rom(int mapper, int prg8K, int chr1K, int submapper = 0) =>
        INesRom.Parse(BankedRom.Build(mapper, prg8K, chr1K, submapper));

    [Fact]
    public void Mmc2_latches_switch_chr_after_fetching_tiles_fd_and_fe()
    {
        var mapper = new Mmc2(Rom(9, 16, 128));
        mapper.CpuWrite(0xA000, 2);
        mapper.CpuWrite(0xB000, 3); // $0000 FD -> 4 KB bank 3 (1 KB banks 12-15)
        mapper.CpuWrite(0xC000, 5); // $0000 FE -> bank 5 (20-23)
        mapper.CpuWrite(0xD000, 6);
        mapper.CpuWrite(0xE000, 7);

        mapper.CpuRead(0x8000, 0).Should().Be(2);
        mapper.CpuRead(0xA000, 0).Should().Be(13, "the last three 8 KB banks are fixed");
        mapper.PpuRead(0x0000).Should().Be(20, "latches start on $FE");

        mapper.PpuRead(0x0FD8).Should().Be(23, "the triggering fetch still uses the old bank");
        mapper.PpuRead(0x0000).Should().Be(12);
        mapper.PpuRead(0x1FDB).Should().Be(31);
        mapper.PpuRead(0x1000).Should().Be(24);
        mapper.PpuRead(0x0FE8);
        mapper.PpuRead(0x0000).Should().Be(20);

        mapper.CpuWrite(0xF000, 1);
        mapper.Mirroring.Should().Be(Mirroring.Horizontal);
    }

    [Fact]
    public void Mmc4_switches_16k_prg_and_latches_on_a_tile_range()
    {
        var mapper = new Mmc2(Rom(10, 16, 128));
        mapper.CpuWrite(0xA000, 3);
        mapper.CpuWrite(0xB000, 1);

        mapper.CpuRead(0x8000, 0).Should().Be(6);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.PpuRead(0x0FDC); // MMC4 latches anywhere in $0FD8-$0FDF
        mapper.PpuRead(0x0000).Should().Be(4);
    }

    [Fact]
    public void Color_dreams_banks_with_bus_conflicts()
    {
        var mapper = new ColorDreams(Rom(11, 16, 64));

        // The fill byte of 8 KB bank 1 at $A000 is 1: writing $21 there latches $21 & $01 = 1.
        mapper.CpuWrite(0xA000, 0x21);

        mapper.CpuRead(0x8000, 0).Should().Be(4, "32 KB bank 1");
        mapper.PpuRead(0x0000).Should().Be(0, "CHR bits were masked away by the conflict");
    }

    [Fact]
    public void GxRom_banks()
    {
        var mapper = new GxRom(Rom(66, 16, 64));
        mapper.CpuWrite(0x8000, 0x12);

        mapper.CpuRead(0x8000, 0).Should().Be(4);
        mapper.PpuRead(0x0000).Should().Be(16);
    }

    [Fact]
    public void Camerica_switches_16k_and_fire_hawk_mirroring()
    {
        var mapper = new Camerica(Rom(71, 16, 0, submapper: 1));
        mapper.CpuWrite(0xC000, 2);
        mapper.CpuWrite(0x9000, 0x10);

        mapper.CpuRead(0x8000, 0).Should().Be(4);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.Mirroring.Should().Be(Mirroring.SingleScreenB);
    }

    [Fact]
    public void Mapper_34_is_bnrom_with_chr_ram_and_nina_with_chr_rom()
    {
        var bnrom = new Mapper34(Rom(34, 16, 0));
        bnrom.CpuWrite(0xA000, 0x01); // ROM byte there is 1, so the conflict keeps 1
        bnrom.CpuRead(0x8000, 0).Should().Be(4, "32 KB bank 1");

        var nina = new Mapper34(Rom(34, 16, 64));
        nina.CpuWrite(0x7FFD, 1);
        nina.CpuWrite(0x7FFE, 3);
        nina.CpuWrite(0x7FFF, 5);
        nina.CpuRead(0x8000, 0).Should().Be(4);
        nina.PpuRead(0x0000).Should().Be(12);
        nina.PpuRead(0x1000).Should().Be(20);
        nina.CpuRead(0x7FFF, 0).Should().Be(5, "the write also lands in PRG-RAM");
    }

    [Fact]
    public void Nina_03_06_latches_at_4100()
    {
        var mapper = new Nina0306(Rom(79, 8, 64));
        mapper.CpuWrite(0x4100, 0x0B);

        mapper.CpuRead(0x8000, 0).Should().Be(4);
        mapper.PpuRead(0x0000).Should().Be(24);

        mapper.CpuWrite(0x4000 | 0x2000, 0x00); // $6000 is PRG-RAM, not the register
        mapper.PpuRead(0x0000).Should().Be(24);
    }

    [Fact]
    public void Mapper_87_swaps_the_two_chr_bits_and_140_banks_from_6000()
    {
        var m87 = new Mapper87(Rom(87, 4, 32));
        m87.CpuWrite(0x6000, 0x01);
        m87.PpuRead(0x0000).Should().Be(16, "bit 0 is the high CHR bit");

        var m140 = new Mapper140(Rom(140, 16, 64));
        m140.CpuWrite(0x7000, 0x13);
        m140.CpuRead(0x8000, 0).Should().Be(4);
        m140.PpuRead(0x0000).Should().Be(24);
    }

    [Fact]
    public void Namco_108_banks_like_a_fixed_mmc3()
    {
        var mapper = new Namco108(Rom(206, 16, 64));
        mapper.CpuWrite(0x8000, 6);
        mapper.CpuWrite(0x8001, 5);
        mapper.CpuWrite(0x8000, 2);
        mapper.CpuWrite(0x8001, 9);

        mapper.CpuRead(0x8000, 0).Should().Be(5);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.PpuRead(0x1000).Should().Be(9);
    }

    public static TheoryData<int, int, int, int> Boards { get; } = new()
    {
        { 9, 16, 128, 0 },
        { 10, 16, 128, 0 },
        { 11, 16, 64, 0 },
        { 34, 16, 0, 0 },
        { 34, 16, 64, 0 },
        { 66, 16, 64, 0 },
        { 71, 16, 0, 1 },
        { 79, 8, 64, 0 },
        { 87, 4, 32, 0 },
        { 140, 16, 64, 0 },
        { 206, 16, 64, 0 },
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
