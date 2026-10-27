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
}
