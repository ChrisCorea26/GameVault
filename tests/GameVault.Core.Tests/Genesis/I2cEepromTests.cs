using GameVault.Core.Genesis;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Genesis;

/// <summary>Genesis serial EEPROM saves (outstanding-work plan §7), driven bit by bit as a game's code drives them.</summary>
public sealed class I2cEepromTests
{
    /// <summary>The game's side of the bus: two lines through a write callback, SDA read back through another.</summary>
    private sealed class Bus(Action<bool, bool> drive, Func<bool> sda)
    {
        private bool _scl = true;
        private bool _sda = true;

        private void Set(bool scl, bool data)
        {
            _scl = scl;
            _sda = data;
            drive(scl, data);
        }

        public void Start()
        {
            Set(_scl, true);
            Set(true, true);
            Set(true, false);
            Set(false, false);
        }

        public void Stop()
        {
            Set(false, false);
            Set(true, false);
            Set(true, true);
        }

        /// <summary>Sends a byte MSB first; returns whether the EEPROM acknowledged it.</summary>
        public bool Send(byte value)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var b = ((value >> bit) & 1) != 0;
                Set(false, b);
                Set(true, b);
            }

            Set(false, true); // release SDA for the acknowledge
            Set(true, true);
            var ack = !sda();
            Set(false, true);
            return ack;
        }

        public byte Receive(bool acknowledge)
        {
            var value = 0;
            Set(false, true);
            for (var bit = 0; bit < 8; bit++)
            {
                Set(true, true);
                value = (value << 1) | (sda() ? 1 : 0);
                Set(false, true);
            }

            Set(false, !acknowledge);
            Set(true, !acknowledge);
            Set(false, true);
            return (byte)value;
        }
    }

    private static Bus Lines(I2cEeprom eeprom) => new(eeprom.Drive, () => eeprom.Sda);

    [Fact]
    public void The_x24c01_writes_a_page_and_reads_it_back()
    {
        var eeprom = new I2cEeprom(I2cEepromKind.X24C01, 128, 4);
        var bus = Lines(eeprom);

        bus.Start();
        bus.Send(0x10 << 1).Should().BeTrue("address $10, write");
        bus.Send(0xAA).Should().BeTrue();
        bus.Send(0x55).Should().BeTrue();
        bus.Stop();

        bus.Start();
        bus.Send((0x10 << 1) | 1).Should().BeTrue("address $10, read");
        bus.Receive(acknowledge: true).Should().Be(0xAA);
        bus.Receive(acknowledge: false).Should().Be(0x55);
        bus.Stop();

        eeprom.Data[0x10].Should().Be(0xAA);
        eeprom.Data[0x12].Should().Be(0xFF, "untouched bytes stay erased");
    }

    [Fact]
    public void A_page_write_wraps_within_its_page()
    {
        var eeprom = new I2cEeprom(I2cEepromKind.X24C01, 128, 4);
        var bus = Lines(eeprom);

        bus.Start();
        bus.Send(0x06 << 1);
        foreach (var b in new byte[] { 1, 2, 3 })
        {
            bus.Send(b);
        }

        bus.Stop();

        eeprom.Data[6].Should().Be(1);
        eeprom.Data[7].Should().Be(2);
        eeprom.Data[4].Should().Be(3, "the third byte wrapped to the page's start");
    }

    [Fact]
    public void A_24c02_does_a_random_read_after_a_dummy_write()
    {
        var eeprom = new I2cEeprom(I2cEepromKind.Standard, 256, 8);
        var bus = Lines(eeprom);

        bus.Start();
        bus.Send(0xA0).Should().BeTrue();
        bus.Send(0x80).Should().BeTrue();
        bus.Send(0x42);
        bus.Stop();

        bus.Start();
        bus.Send(0xB0).Should().BeFalse("another device's address isn't acknowledged");
        bus.Stop();

        bus.Start();
        bus.Send(0xA0);
        bus.Send(0x80);
        bus.Start(); // repeated START: read from the address just set
        bus.Send(0xA1).Should().BeTrue();
        bus.Receive(acknowledge: false).Should().Be(0x42);
        bus.Stop();
    }

    [Fact]
    public void A_cartridge_whose_header_declares_an_eeprom_saves_through_200001()
    {
        var rom = new byte[0x80000];
        "SEGA GENESIS    "u8.CopyTo(rom.AsSpan(0x100));
        rom[0x1B0] = (byte)'R';
        rom[0x1B1] = (byte)'A';
        rom[0x1B2] = 0xE8;
        rom[0x1B3] = 0x40;
        rom[0x1B5] = 0x20; // $200001–$200001
        rom[0x1B7] = 0x01;
        rom[0x1B9] = 0x20;
        rom[0x1BB] = 0x01;
        var cartridge = new GenesisCartridge(rom);
        cartridge.Eeprom.Should().NotBeNull();
        cartridge.HasBattery.Should().BeTrue();

        // SDA is bit 0 and SCL bit 1 of $200001; SDA reads back on bit 0.
        var bus = new Bus(
            (scl, sda) => cartridge.WriteByte(0x200001, (byte)((scl ? 2 : 0) | (sda ? 1 : 0))),
            () => (cartridge.ReadByte(0x200001) & 1) != 0);
        bus.Start();
        bus.Send(0x05 << 1);
        bus.Send(0x99);
        bus.Stop();

        cartridge.BatteryRam[5].Should().Be(0x99, "the save file is the EEPROM's contents");

        var writer = new StateWriter();
        cartridge.SaveState(writer);
        var copy = new GenesisCartridge(rom);
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.BatteryRam[5].Should().Be(0x99);
    }

    /// <summary>A 2 MB ROM whose header names a product code (and optionally a checksum and stack pointer).</summary>
    private static byte[] BoardRom(string product, int size = 0x200000, ushort checksum = 0, uint stack = 0xFFFF00)
    {
        var rom = new byte[size];
        rom[0] = (byte)(stack >> 24);
        rom[1] = (byte)(stack >> 16);
        rom[2] = (byte)(stack >> 8);
        rom[3] = (byte)stack;
        "SEGA GENESIS    "u8.CopyTo(rom.AsSpan(0x100));
        System.Text.Encoding.ASCII.GetBytes(product.PadRight(14)).CopyTo(rom.AsSpan(0x180));
        rom[0x18E] = (byte)(checksum >> 8);
        rom[0x18F] = (byte)checksum;
        for (var i = 0; i < size; i += 2)
        {
            if (i >= 0x200)
            {
                rom[i] = 0x12; // recognisable ROM bytes
                rom[i + 1] = 0x34;
            }
        }

        return rom;
    }

    /// <summary>Writes one byte at <paramref name="address"/> and reads it back, through the bus the test hands in.</summary>
    private static void WriteAndReadBack(Bus bus, I2cEeprom eeprom, int address, byte value)
    {
        var twoBytes = eeprom.Kind == I2cEepromKind.TwoByteAddress;
        var device = (byte)(0xA0 | (twoBytes ? 0 : ((address >> 8) & 7) << 1));
        bus.Start();
        bus.Send(device).Should().BeTrue("the device byte is acknowledged");
        if (twoBytes)
        {
            bus.Send((byte)(address >> 8)).Should().BeTrue();
        }

        bus.Send((byte)address).Should().BeTrue();
        bus.Send(value).Should().BeTrue();
        bus.Stop();

        bus.Start();
        bus.Send(device);
        if (twoBytes)
        {
            bus.Send((byte)(address >> 8));
        }

        bus.Send((byte)address);
        bus.Start();
        bus.Send((byte)(device | 1)).Should().BeTrue();
        bus.Receive(acknowledge: false).Should().Be(value);
        bus.Stop();
    }

    [Fact]
    public void An_ea_board_drives_the_x24c01_on_bits_7_and_6()
    {
        var cartridge = new GenesisCartridge(BoardRom("GM T-50396 -00")); // NHLPA Hockey '93
        cartridge.EepromBoard!.Wiring.Should().Be(GenesisEepromWiring.ElectronicArts);
        var bus = new Bus(
            (scl, sda) => cartridge.WriteByte(0x200001, (byte)((scl ? 0x40 : 0) | (sda ? 0x80 : 0))),
            () => (cartridge.ReadByte(0x200001) & 0x80) != 0);

        bus.Start();
        bus.Send(0x20 << 1);
        bus.Send(0x5A);
        bus.Stop();
        bus.Start();
        bus.Send((0x20 << 1) | 1);
        bus.Receive(acknowledge: false).Should().Be(0x5A);
        bus.Stop();

        cartridge.BatteryRam.Length.Should().Be(128);
        cartridge.BatteryRam[0x20].Should().Be(0x5A);
    }

    [Fact]
    public void Acclaims_16m_board_reads_sda_back_on_bit_1()
    {
        var cartridge = new GenesisCartridge(BoardRom("GM T-081326-00")); // NBA Jam
        cartridge.EepromBoard!.Wiring.Should().Be(GenesisEepromWiring.Acclaim16M);
        cartridge.BatteryRam.Length.Should().Be(256);
        var bus = new Bus(
            (scl, sda) => cartridge.WriteWord(0x200000, (ushort)((scl ? 2 : 0) | (sda ? 1 : 0))),
            () => (cartridge.ReadWord(0x200000) & 2) != 0);

        WriteAndReadBack(bus, cartridge.Eeprom!, 0xC3, 0x77);
        cartridge.BatteryRam[0xC3].Should().Be(0x77);
    }

    [Fact]
    public void Acclaims_32m_board_splits_the_lines_and_switches_the_rom_out_for_reads()
    {
        var cartridge = new GenesisCartridge(BoardRom("GM T-81576 -00", size: 0x400000)); // College Slam
        cartridge.EepromBoard!.Wiring.Should().Be(GenesisEepromWiring.Acclaim32M);
        cartridge.Eeprom!.Kind.Should().Be(I2cEepromKind.TwoByteAddress);
        cartridge.ReadWord(0x200000).Should().Be(0x1234, "the ROM reads there after a reset");

        cartridge.WriteWord(0x200000, 0); // D0 clear: the EEPROM reads at $200000–$2FFFFF
        var scl = true;
        var data = true;
        var bus = new Bus(
            (c, d) =>
            {
                // One line at a time, as the game's code does: SCL falls before SDA changes, and rises after it.
                if (!c && scl)
                {
                    cartridge.WriteByte(0x200000, 0); // SCL on the even byte
                    scl = false;
                }

                if (d != data)
                {
                    cartridge.WriteByte(0x200001, (byte)(d ? 1 : 0)); // SDA on the odd byte
                    data = d;
                }

                if (c && !scl)
                {
                    cartridge.WriteByte(0x200000, 1);
                    scl = true;
                }
            },
            () => (cartridge.ReadByte(0x200001) & 1) != 0);

        WriteAndReadBack(bus, cartridge.Eeprom, 0x1234, 0x9C);
        cartridge.BatteryRam[0x1234].Should().Be(0x9C);

        cartridge.WriteWord(0x200000, 1);
        cartridge.ReadWord(0x200000).Should().Be(0x1234, "D0 set puts the ROM back");
    }

    [Fact]
    public void A_codemasters_board_writes_at_300000_and_reads_sda_on_bit_7_of_380001()
    {
        // Micro Machines '96 leaves its product code blank; the checksum and stack pointer pick it.
        var rom = BoardRom("GM 00000000-00", size: 0x100000, checksum: 0x165E, stack: 0x444E4C44);
        var cartridge = new GenesisCartridge(rom);
        cartridge.EepromBoard!.Wiring.Should().Be(GenesisEepromWiring.Codemasters);
        cartridge.BatteryRam.Length.Should().Be(2048);
        var bus = new Bus(
            (scl, sda) => cartridge.WriteByte(0x300000, (byte)((scl ? 2 : 0) | (sda ? 1 : 0))),
            () => (cartridge.ReadByte(0x380001) & 0x80) != 0);

        WriteAndReadBack(bus, cartridge.Eeprom!, 0x5A5, 0x3C);
        cartridge.BatteryRam[0x5A5].Should().Be(0x3C);

        var writer = new StateWriter();
        cartridge.SaveState(writer);
        var copy = new GenesisCartridge(rom);
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.BatteryRam[0x5A5].Should().Be(0x3C);
    }

    [Fact]
    public void A_listed_sega_game_without_the_header_entry_still_gets_its_eeprom()
    {
        var cartridge = new GenesisCartridge(BoardRom("GM G-4060  -00", size: 0x100000)); // Wonder Boy in Monster World
        cartridge.EepromBoard.Should().Be(GenesisEepromBoard.SegaDefault);

        var patched = BoardRom("GM G-4060  -00", size: 0x100000);
        patched[0x1B0] = (byte)'R';
        patched[0x1B1] = (byte)'A';
        patched[0x1B2] = 0xF8;
        patched[0x1B3] = 0x20;
        patched[0x1B4] = 0x00; // SRAM at $200001–$203FFF: a copy patched to save in SRAM
        patched[0x1B5] = 0x20;
        patched[0x1B6] = 0x00;
        patched[0x1B7] = 0x01;
        patched[0x1B8] = 0x00;
        patched[0x1B9] = 0x20;
        patched[0x1BA] = 0x3F;
        patched[0x1BB] = 0xFF;
        new GenesisCartridge(patched).Eeprom.Should().BeNull("the patched copy saves in its SRAM");
    }

    [Fact]
    public void Other_cartridges_get_no_eeprom()
    {
        new GenesisCartridge(BoardRom("GM T-99999 -00")).Eeprom.Should().BeNull();
        new GenesisCartridge(BoardRom("GM 00000000-00", checksum: 0x1234, stack: 0x444E4C44)).Eeprom.Should().BeNull();
    }
}
