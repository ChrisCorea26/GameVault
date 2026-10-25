using GameVault.Core.Cartridge;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Cartridge;

public class INesRomTests
{
    [Fact]
    public void Parses_an_ines_header()
    {
        var image = TestRoms.Nrom([0xEA], prgBanks: 2, chrBanks: 1, flags6: 0x03);

        var rom = INesRom.Parse(image);

        rom.Mapper.Should().Be(0);
        rom.PrgRom.Should().HaveCount(0x8000);
        rom.ChrRom.Should().HaveCount(0x2000);
        rom.Mirroring.Should().Be(Mirroring.Vertical);
        rom.HasBattery.Should().BeTrue();
        rom.IsNes2.Should().BeFalse();
        rom.PrgRamSize.Should().Be(0x2000);
        rom.ChrRamSize.Should().Be(0);
    }

    [Fact]
    public void Missing_chr_rom_means_8k_chr_ram()
    {
        var rom = INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 0));

        rom.ChrRom.Should().BeEmpty();
        rom.ChrRamSize.Should().Be(0x2000);
    }

    [Fact]
    public void Reads_mapper_number_from_both_nibbles()
    {
        var image = TestRoms.Nrom([0xEA]);
        image[6] = 0x40; // low nibble 4
        image[7] = 0x10; // high nibble 1 -> mapper 0x14

        INesRom.Parse(image).Mapper.Should().Be(0x14);
    }

    [Fact]
    public void Ignores_flags7_when_old_tools_left_junk_in_the_header()
    {
        var image = TestRoms.Nrom([0xEA]);
        image[6] = 0x10;
        image[7] = 0x40;
        "DiskDude!"u8.ToArray().AsSpan(..9).CopyTo(image.AsSpan(7));

        INesRom.Parse(image).Mapper.Should().Be(1);
    }

    [Fact]
    public void Parses_nes2_fields()
    {
        var image = TestRoms.Nrom([0xEA], chrBanks: 0);
        image[6] = 0x12; // mapper low nibble 1, battery
        image[7] = 0x08; // NES 2.0
        image[8] = 0x51; // submapper 5, mapper bits 8-11 = 1
        image[10] = 0x70; // 8 KB PRG-NVRAM (64 << 7)
        image[11] = 0x07; // 8 KB CHR-RAM
        image[12] = 0x01; // PAL

        var rom = INesRom.Parse(image);

        rom.IsNes2.Should().BeTrue();
        rom.Mapper.Should().Be(0x101);
        rom.Submapper.Should().Be(5);
        rom.PrgRamSize.Should().Be(0x2000);
        rom.ChrRamSize.Should().Be(0x2000);
        rom.TvSystem.Should().Be(TvSystem.Pal);
    }

    [Fact]
    public void Skips_the_trainer()
    {
        var image = TestRoms.Nrom([0xA9, 0x42]);
        image[6] |= 0x04;
        var withTrainer = image[..16].Concat(new byte[512]).Concat(image[16..]).ToArray();

        var rom = INesRom.Parse(withTrainer);

        rom.Trainer.Should().HaveCount(512);
        rom.PrgRom[0].Should().Be(0xA9);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'N', (byte)'E', (byte)'S', 0x1A, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Rejects_invalid_or_truncated_files(byte[] data)
    {
        var act = () => INesRom.Parse(data);

        act.Should().Throw<RomFormatException>();
    }

    [Fact]
    public void Unsupported_mappers_are_reported_by_number()
    {
        var image = TestRoms.Nrom([0xEA]);
        image[6] = 0xD0; // mapper 13 (CPROM), not supported yet

        var act = () => MapperFactory.Create(INesRom.Parse(image));

        act.Should().Throw<UnsupportedMapperException>().Which.Mapper.Should().Be(13);
    }
}
