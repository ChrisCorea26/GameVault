using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>The OBC1 (outstanding-work plan §5).</summary>
public sealed class Obc1Tests
{
    [Fact]
    public void Sprite_n_s_bytes_and_high_bits_land_in_its_table()
    {
        var cartridge = new SnesCartridge(SnesTests.Rom([0x80, 0xFE], chip: 0x25, ramSize: 3));
        var obc1 = cartridge.Coprocessor.Should().BeOfType<Obc1>().Subject;
        cartridge.HasBattery.Should().BeTrue();

        obc1.Write(0x007FF5, 0x00); // the table at $1C00
        obc1.Write(0x007FF6, 5);    // sprite 5
        obc1.Write(0x007FF1, 0xAB);
        obc1.Write(0x007FF4, 0x02);

        obc1.Ram[0x1C00 + 5 * 4 + 1].Should().Be(0xAB);
        obc1.Ram[0x1C00 + 0x200 + 1].Should().Be(0x02 << 2, "sprite 5's two bits are bits 2–3 of byte 1");
        obc1.Read(0x807FF1, 0).Should().Be(0xAB);
        obc1.Read(0x006123, 0).Should().Be(0, "the rest is plain SRAM");
        cartridge.BatteryRam[0x1C15].Should().Be(0xAB);

        obc1.Write(0x007FF5, 0x01); // the other table, at $1800
        obc1.Read(0x007FF1, 0).Should().Be(0);
    }
}
