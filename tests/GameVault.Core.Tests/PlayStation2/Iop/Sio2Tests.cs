using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>SIO2 exchanges with the DualShock 2 and the memory card, as SIO2MAN, PADMAN and MCMAN make them.</summary>
public sealed class Sio2Tests
{
    private const uint Sio2 = Ps2Sio2.Base;

    private readonly IopHarness _h = new();

    private Ps2Iop Iop => _h.Iop;

    /// <summary>One command through the registers: SEND3, the in FIFO, CTRL, then the out FIFO.</summary>
    private byte[] Transfer(int port, params byte[] command)
    {
        Iop.Write32(Sio2 + 0x68, 0x3BC); // reset
        var length = (uint)command.Length;
        Iop.Write32(Sio2, (uint)port | (length << 8) | (length << 18));
        Iop.Write32(Sio2 + 4, 0);
        foreach (var b in command)
        {
            Iop.Write8(Sio2 + 0x60, b);
        }

        Iop.Write32(Sio2 + 0x68, 0x3BD); // start
        var reply = new byte[command.Length];
        for (var i = 0; i < reply.Length; i++)
        {
            reply[i] = Iop.Read8(Sio2 + 0x64);
        }

        return reply;
    }

    private uint Recv1 => Iop.Read32(Sio2 + 0x6C);

    private static byte[] Padded(int length, params byte[] start)
    {
        var bytes = new byte[length];
        start.CopyTo(bytes, 0);
        return bytes;
    }

    // ---- The pad ----

    [Fact]
    public void A_digital_poll_returns_the_buttons_active_low()
    {
        Iop.SetControllerState(0, ControllerState.B | ControllerState.Start);
        Transfer(0, 0x01, 0x42, 0x00, 0x00, 0x00).Should().Equal(0xFF, 0x41, 0x5A, 0xF7, 0xBF);
        (Recv1 & 0x2000).Should().Be(0, "the pad answered");
        (Iop.InterruptStatus & (1u << 17)).Should().NotBe(0, "the SIO2 interrupt");
        Iop.Read32(Sio2 + 0x70).Should().Be(0xFu);
    }

    [Fact]
    public void Config_mode_switches_the_pad_to_dualshock_2_with_sticks_and_pressure()
    {
        Iop.SetControllerState(
            0, ControllerState.B.WithStick(127, 127), rightX: -128, rightY: 0, l3: true);

        Transfer(0, Padded(5, 0x01, 0x43, 0x00, 0x01)).Should().Equal(0xFF, 0x41, 0x5A, 0xFD, 0xBF);
        Transfer(0, Padded(9, 0x01, 0x45, 0x00)).Should().Equal(0xFF, 0xF3, 0x5A, 0x03, 0x02, 0x00, 0x02, 0x01, 0x00);
        Transfer(0, Padded(9, 0x01, 0x44, 0x00, 0x01, 0x03));
        Transfer(0, Padded(9, 0x01, 0x46, 0x00, 0x00))[3..].Should().Equal(0x00, 0x00, 0x01, 0x02, 0x00, 0x0A);
        Transfer(0, Padded(9, 0x01, 0x46, 0x00, 0x01))[3..].Should().Equal(0x00, 0x00, 0x01, 0x01, 0x01, 0x14);
        Transfer(0, Padded(9, 0x01, 0x4C, 0x00, 0x01))[6].Should().Be(0x07);
        Transfer(0, Padded(9, 0x01, 0x4F, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x00, 0x00))[8].Should().Be(0x5A);
        Transfer(0, Padded(9, 0x01, 0x43, 0x00, 0x00)).Should().Equal(0xFF, 0xF3, 0x5A, 0, 0, 0, 0, 0, 0);

        var poll = Transfer(0, Padded(21, 0x01, 0x42, 0x00));
        poll[..5].Should().Equal(0xFF, 0x79, 0x5A, 0xFD, 0xBF);
        poll[5..9].Should().Equal(new byte[] { 0x00, 0x80, 0xFF, 0x01 }, "right X, right Y, left X, left Y");
        poll[9..21].Should().Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0 }, "only Cross is pressed");
    }

    [Fact]
    public void A_missing_pad_on_port_2_is_reported_in_recv1()
    {
        Iop.Sio2.PadConnected[1] = false;
        Transfer(1, 0x01, 0x42, 0x00, 0x00, 0x00).Should().Equal(0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        (Recv1 & 0x2D000).Should().Be(0x2D000u);
    }

    // ---- The memory card ----

    [Fact]
    public void The_card_answers_probe_and_specs_and_slot_2_is_empty()
    {
        Iop.MemoryCard = Ps2MemoryCard.CreateFormatted();
        Transfer(0, 0x81, 0x11, 0x00, 0x00).Should().Equal(0x00, 0x00, 0x2B, 0x55);
        Recv1.Should().Be(0x1100u);
        Transfer(0, Padded(13, 0x81, 0x26)).Should().Equal(0x00, 0x00, 0x2B, 0x00, 0x02, 0x10, 0x00, 0x00, 0x40, 0x00, 0x00, 0x52, 0x55);
        Transfer(1, 0x81, 0x11, 0x00, 0x00).Should().Equal(0xFF, 0xFF, 0xFF, 0xFF);
        Recv1.Should().Be(0x1D100u);
    }

    /// <summary>Starts SIO2's DMA channels 11 and 12 for one command of <paramref name="command"/>'s bytes (padded to words).</summary>
    private byte[] TransferByDma(params byte[] command)
    {
        var words = (command.Length + 3) / 4;
        var bytes = new byte[words * 4];
        command.CopyTo(bytes, 0);
        bytes.CopyTo(Iop.Ram, 0x50000);

        Iop.Write32(0x1F801570, Iop.Read32(0x1F801570) | 0x880000); // DPCR2: channels 11 and 12
        Iop.Write32(Sio2 + 0x68, 0x3BC);
        Iop.Write32(Sio2, (uint)command.Length << 8 | (uint)command.Length << 18);
        Iop.Write32(Sio2 + 4, 0);
        Iop.Write32(0x1F801540, 0x50000);
        Iop.Write32(0x1F801544, 0x10000 | (uint)words);
        Iop.Write32(0x1F801548, 0x01000201);
        Iop.Write32(0x1F801550, 0x60000);
        Iop.Write32(0x1F801554, 0x10000 | (uint)words);
        Iop.Write32(0x1F801558, 0x41000200);
        Iop.Write32(Sio2 + 0x68, 0x3BD);
        _h.Run(1000);
        (Iop.Read32(0x1F801558) & 0x01000000).Should().Be(0, "DMA 12 finished");
        return Iop.Ram.AsSpan(0x60000, command.Length).ToArray();
    }

    private static byte[] Sector(uint sector, byte command)
    {
        var s = new byte[9];
        s[0] = 0x81;
        s[1] = command;
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(2), sector);
        s[6] = (byte)(s[2] ^ s[3] ^ s[4] ^ s[5]);
        return s;
    }

    private byte[] ReadPage(uint page)
    {
        TransferByDma(Sector(page, 0x23))[^2..].Should().Equal(0x2B, 0x55);
        var data = new List<byte>();
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var reply = TransferByDma(Padded(134, 0x81, 0x43, 0x80));
            reply[3].Should().Be(0x2B);
            var bytes = reply.AsSpan(4, 128).ToArray();
            reply[132].Should().Be(bytes.Aggregate((byte)0, (x, b) => (byte)(x ^ b)), "the XOR checksum");
            data.AddRange(bytes);
        }

        Transfer(0, 0x81, 0x81, 0x00, 0x00);
        return [.. data];
    }

    [Fact]
    public void Format_then_read_the_superblock_back_through_sio2()
    {
        Iop.MemoryCard = Ps2MemoryCard.CreateFormatted();
        var page = ReadPage(0);
        Encoding.ASCII.GetString(page, 0, 35).Should().Be("Sony PS2 Memory Card Format 1.2.0.0");
        BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0x28)).Should().Be(512);
        BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0x2A)).Should().Be(2);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x30)).Should().Be(8192u);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x34)).Should().Be(41u);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x38)).Should().Be(8135u);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x40)).Should().Be(1023u);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x44)).Should().Be(1022u);
        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x50)).Should().Be(8u);
        page[0x150].Should().Be(2);
        page[0x151].Should().Be(0x52);
    }

    [Fact]
    public void A_written_page_reads_back_with_its_ecc_computed()
    {
        var card = Ps2MemoryCard.CreateFormatted();
        Iop.MemoryCard = card;
        var data = Enumerable.Range(0, 512).Select(i => (byte)(i * 7 + 3)).ToArray();

        Transfer(0, Sector(100, 0x21));
        Transfer(0, 0x81, 0x82, 0x00, 0x00).Should().Equal(0x00, 0x00, 0x2B, 0x55);
        card.Data.AsSpan(100 * 528, 528 * 16).IndexOfAnyExcept((byte)0xFF).Should().Be(-1, "the block is erased");

        Transfer(0, Sector(100, 0x22));
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var command = Padded(134, 0x81, 0x42, 0x80);
            data.AsSpan(chunk * 128, 128).CopyTo(command.AsSpan(3));
            var reply = Transfer(0, command);
            reply[3].Should().Be(0x2B);
            reply[132].Should().Be(data.AsSpan(chunk * 128, 128).ToArray().Aggregate((byte)0, (x, b) => (byte)(x ^ b)));
        }

        Transfer(0, 0x81, 0x12, 0x00, 0x00);
        card.Dirty.Should().BeTrue();
        ReadPage(100).Should().Equal(data);

        var expected = new byte[528];
        data.CopyTo(expected, 0);
        Ps2MemoryCard.WriteEcc(expected);
        card.Data.AsSpan(100 * 528, 528).ToArray().Should().Equal(expected);
    }

    [Fact]
    public void The_authentication_xor_is_answered()
    {
        Iop.MemoryCard = new Ps2MemoryCard();
        var reply = Transfer(0, 0x81, 0xF0, 0x01, 1, 2, 4, 8, 16, 32, 64, 128, 0, 0, 0);
        reply.Should().HaveCount(14);
        reply[^2].Should().Be(0xFF);
        reply[^1].Should().Be(0x55);
        Transfer(0, Padded(5, 0x81, 0xF3))[^2..].Should().Equal(0x2B, 0x55);
    }
}
