using FluentAssertions;
using GameVault.Core.Atari;
using GameVault.Core.Cartridge;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Atari;

/// <summary>Every 2600 bank-switching scheme (phase 19): detection, hotspots, RAM, and a save/load round trip each.</summary>
public sealed class Atari2600CartridgeTests
{
    /// <summary>An image whose 1K blocks each start with their number (so a read tells which part is mapped).</summary>
    private static byte[] Blocks(int size)
    {
        var rom = new byte[size];
        for (var i = 0; i < size; i++)
        {
            rom[i] = i % 1024 == 0 ? (byte)(i / 1024) : (byte)(i * 7 + 1);
        }

        return rom;
    }

    /// <summary>Saves the cartridge, loads it into a fresh one, and checks the copy maps the same bytes.</summary>
    private static void RoundTrips(Atari2600Cartridge cartridge, byte[] rom, string? scheme = null)
    {
        var writer = new StateWriter();
        cartridge.SaveState(writer);
        var copy = new Atari2600Cartridge(rom, scheme);
        copy.LoadState(new StateReader(writer.ToArray()));
        for (var address = 0x1000; address < 0x1FE0; address += 0x100)
        {
            copy.Read(address).Should().Be(cartridge.Read(address), $"${address:X4} after loading the state");
        }

        copy.Ram.ToArray().Should().Equal(cartridge.Ram.ToArray());
    }

    [Fact]
    public void Plain_2k_and_4k_images_need_no_switching()
    {
        var small = new Atari2600Cartridge(Blocks(2048));
        small.Scheme.Should().Be(Atari2600Scheme.Plain);
        small.Read(0x1800).Should().Be(0, "2K is mirrored");
        new Atari2600Cartridge(Blocks(4096)).Read(0x1C00).Should().Be(3);

        var odd = () => new Atari2600Cartridge(new byte[5000]);
        odd.Should().Throw<RomFormatException>();
    }

    [Theory]
    [InlineData(8192, Atari2600Scheme.F8, 0xFF8, 2)]
    [InlineData(16384, Atari2600Scheme.F6, 0xFF6, 4)]
    [InlineData(32768, Atari2600Scheme.F4, 0xFF4, 8)]
    public void Atari_boards_switch_4k_banks_at_their_hotspots(int size, Atari2600Scheme scheme, int first, int banks)
    {
        var rom = Blocks(size);
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.Scheme.Should().Be(scheme);
        cartridge.SuperChip.Should().BeFalse();
        cartridge.Read(0x1000).Should().Be((byte)((banks - 1) * 4), "it starts in the last bank");

        cartridge.Read(0x1000 + first); // touch the first hotspot
        cartridge.Read(0x1400).Should().Be(1);
        cartridge.Write(0x1000 + first + banks - 1, 0); // writes switch too
        cartridge.Read(0x1800).Should().Be((byte)((banks - 1) * 4 + 2));
        RoundTrips(cartridge, rom);
    }

    [Fact]
    public void A_superchip_board_has_128_bytes_of_ram_written_low_and_read_high()
    {
        var rom = Blocks(8192);
        rom.AsSpan(0, 256).Fill(0xFF);
        rom.AsSpan(4096, 256).Fill(0xFF);
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.SuperChip.Should().BeTrue();

        cartridge.Write(0x1005, 0x42);
        cartridge.Read(0x1085).Should().Be(0x42);
        cartridge.Ram[5].Should().Be(0x42);
        RoundTrips(cartridge, rom);
        new Atari2600Cartridge(Blocks(8192), "F8SC").SuperChip.Should().BeTrue("a profile can say so");
    }

    [Fact]
    public void Parker_brothers_e0_switches_three_1k_windows()
    {
        var rom = Blocks(8192);
        new byte[] { 0x8D, 0xE0, 0x1F }.CopyTo(rom, 100); // STA $1FE0
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.Scheme.Should().Be(Atari2600Scheme.E0);

        cartridge.Read(0x1FE2);
        cartridge.Read(0x1FEB);
        cartridge.Read(0x1FF0);
        cartridge.Read(0x1000).Should().Be(2);
        cartridge.Read(0x1400).Should().Be(3);
        cartridge.Read(0x1800).Should().Be(0);
        cartridge.Read(0x1C00).Should().Be(7, "the last 1K is fixed");
        RoundTrips(cartridge, rom);
    }

    [Fact]
    public void Activision_fe_follows_the_byte_after_an_access_to_01fe()
    {
        var rom = Blocks(8192);
        new byte[] { 0x20, 0x00, 0xD0, 0xC6, 0xC5 }.CopyTo(rom, 100);
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.Scheme.Should().Be(Atari2600Scheme.FE);

        cartridge.Snoop(0x01FE, 0x00, write: true);
        cartridge.Snoop(0x01FF, 0xF0, write: true); // a JSR to $Fxxx: bit 5 set, bank 0
        cartridge.Read(0x1000).Should().Be(0);
        cartridge.Snoop(0x01FE, 0x00, write: false);
        cartridge.Snoop(0x01FF, 0xD0, write: false); // an RTS to $Dxxx: bit 5 clear, bank 1
        cartridge.Read(0x1000).Should().Be(4);
        RoundTrips(cartridge, rom);
    }

    [Fact]
    public void Tigervision_3f_switches_the_low_2k_on_writes_to_the_tia_s_range()
    {
        var rom = Blocks(8192);
        new byte[] { 0x85, 0x3F, 0xEA, 0x85, 0x3F }.CopyTo(rom, 10);
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.Scheme.Should().Be(Atari2600Scheme.Tigervision3F);

        cartridge.Snoop(0x003F, 2, write: true);
        cartridge.Read(0x1000).Should().Be(4, "2K bank 2 is the ROM's 4th and 5th K");
        cartridge.Read(0x1800).Should().Be(6, "the last 2K is fixed");
        cartridge.Snoop(0x0040, 0, write: true);
        cartridge.Read(0x1000).Should().Be(4, "$40 isn't in the range");
        RoundTrips(cartridge, rom);
    }

    [Fact]
    public void Cbs_ram_plus_fa_has_three_banks_and_256_bytes_of_ram()
    {
        var rom = Blocks(12288);
        var cartridge = new Atari2600Cartridge(rom);
        cartridge.Scheme.Should().Be(Atari2600Scheme.FA);

        cartridge.Read(0x1FF9);
        cartridge.Read(0x1400).Should().Be(5);
        cartridge.Write(0x10F0, 0x77);
        cartridge.Read(0x11F0).Should().Be(0x77);
        RoundTrips(cartridge, rom);
    }
}
