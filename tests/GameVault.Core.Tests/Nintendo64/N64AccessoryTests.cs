using GameVault.Core.Nintendo64;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>Controller accessories on the joybus (outstanding-work plan §10).</summary>
public sealed class N64AccessoryTests
{
    /// <summary>Runs one pak command on controller <paramref name="port"/>; returns where its reply starts in PIF RAM.</summary>
    private static int Command(N64Pif pif, int port, byte[] tx, int rxLength)
    {
        Array.Clear(pif.Ram);
        var i = port; // one skip byte for each channel before it
        pif.Ram[i++] = (byte)tx.Length;
        pif.Ram[i++] = (byte)rxLength;
        tx.CopyTo(pif.Ram, i);
        var rx = i + tx.Length;
        pif.Ram[rx + rxLength] = 0xFE;
        pif.Ram[63] = 0x01;
        pif.Written();
        return rx;
    }

    private static byte[] Write(int address, byte value) => [0x03, (byte)(address >> 8), (byte)address, .. Enumerable.Repeat(value, 32)];

    [Fact]
    public void The_rumble_pak_answers_its_probe_and_runs_its_motor()
    {
        var pif = new N64Pif(null, new N64ControllerPak());
        var rumble = new N64RumblePak();
        pif.SetAccessory(1, rumble);

        var info = Command(pif, 1, [0x00], 3);
        pif.Ram[info + 2].Should().Be(0x01, "something is in the slot");

        Command(pif, 1, Write(0x8000, 0x80), 1);
        var probe = Command(pif, 1, [0x02, 0x80, 0x00], 33);
        pif.Ram.AsSpan(probe, 32).ToArray().Should().OnlyContain(b => b == 0x80);
        pif.Ram[probe + 32].Should().Be(N64Pif.DataCrc(pif.Ram.AsSpan(probe, 32)));

        Command(pif, 1, Write(0xC000, 0x01), 1);
        rumble.Rumbling.Should().BeTrue();
        Command(pif, 1, Write(0xC000, 0x00), 1);
        rumble.Rumbling.Should().BeFalse();
    }

    [Fact]
    public void Rumble_paks_replace_the_controller_pak_and_give_it_back()
    {
        var console = new N64Console(new N64Cartridge(N64ConsoleTests.Rom([])));
        console.ControllerPak.Memory[0x100] = 0x42;

        console.RumblePaks = true;
        console.Pif.Accessory(0).Should().BeOfType<N64RumblePak>();
        console.Pif.Accessory(3).Should().BeOfType<N64RumblePak>();
        Command(console.Pif, 2, Write(0xC000, 0x01), 1);
        console.RumbleLevel(2).Should().Be(1f);
        console.RumbleLevel(0).Should().Be(0f);

        console.RumblePaks = false;
        console.Pif.Accessory(0).Should().BeSameAs(console.ControllerPak);
        console.Pif.Accessory(1).Should().BeNull();
        console.ControllerPak.Memory[0x100].Should().Be(0x42);
    }
}
