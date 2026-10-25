using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests.Cartridge;

public class NromTests
{
    [Fact]
    public void Mirrors_16k_prg_into_both_halves()
    {
        var nrom = new Nrom(INesRom.Parse(TestRoms.Nrom([0x11, 0x22])));

        nrom.CpuRead(0x8000, 0).Should().Be(0x11);
        nrom.CpuRead(0xC000, 0).Should().Be(0x11);
        nrom.CpuRead(0xC001, 0).Should().Be(0x22);
    }

    [Fact]
    public void Prg_ram_is_readable_and_writable_and_unmapped_space_is_open_bus()
    {
        var nrom = new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA])));

        nrom.CpuWrite(0x6123, 0x5A);

        nrom.CpuRead(0x6123, 0).Should().Be(0x5A);
        nrom.CpuRead(0x5000, 0x47).Should().Be(0x47);
    }

    [Fact]
    public void Chr_rom_ignores_writes_but_chr_ram_keeps_them()
    {
        var romBoard = new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 1)));
        var ramBoard = new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 0)));

        romBoard.PpuWrite(0x0010, 0x99);
        ramBoard.PpuWrite(0x0010, 0x99);

        romBoard.PpuRead(0x0010).Should().Be(0);
        ramBoard.PpuRead(0x0010).Should().Be(0x99);
    }

    [Fact]
    public void State_round_trips_prg_ram_and_chr_ram()
    {
        StateAssert.RoundTrips(
            () => new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 0))),
            nrom =>
            {
                nrom.CpuWrite(0x6000, 0x12);
                nrom.CpuWrite(0x7FFF, 0x34);
                nrom.PpuWrite(0x1FFF, 0x56);
            });
    }
}
