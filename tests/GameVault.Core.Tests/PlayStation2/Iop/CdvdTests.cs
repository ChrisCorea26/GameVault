using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>The CDVD drive's N and S commands against a small ISO 9660 image built by the test.</summary>
public sealed class CdvdTests
{
    private const uint Cdvd = Ps2Cdvd.Base;
    private const long Second = 36_864_000;

    private readonly IopHarness _h = new();

    private Ps2Iop Iop => _h.Iop;

    private void Insert(Ps2DiscType type = Ps2DiscType.Dvd) => Iop.Disc = new MemoryPs2Disc(TestIso.Build(), type);

    private void NCommand(byte command, params byte[] parameters)
    {
        foreach (var p in parameters)
        {
            _h.Write8(Cdvd + 5, p);
        }

        _h.Write8(Cdvd + 4, command);
    }

    private byte[] SCommand(byte command, params byte[] parameters)
    {
        foreach (var p in parameters)
        {
            _h.Write8(Cdvd + 0x17, p);
        }

        _h.Write8(Cdvd + 0x16, command);
        var result = new List<byte>();
        while ((_h.Read8(Cdvd + 0x17) & 0x40) == 0)
        {
            result.Add(_h.Read8(Cdvd + 0x18));
        }

        return [.. result];
    }

    private static byte[] ReadParams(uint lba, uint count, byte spindle = 0, byte mode = 0)
    {
        var p = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(p, lba);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), count);
        p[9] = spindle;
        p[10] = mode;
        return p;
    }

    private void StartDma3(uint madr, int blockBytes, int blocks)
    {
        Iop.Write32(0x1F8010F0, Iop.Read32(0x1F8010F0) | 0x8000);
        Iop.Write32(0x1F8010B0, madr);
        Iop.Write32(0x1F8010B4, ((uint)blocks << 16) | (uint)(blockBytes / 4));
        Iop.Write32(0x1F8010B8, 0x41000200);
    }

    private bool Completed => (_h.Read8(Cdvd + 8) & 1) != 0;

    [Fact]
    public void The_disc_type_register_tells_cd_dvd_and_no_disc()
    {
        _h.Read8(Cdvd + 0xF).Should().Be(0x00);
        Insert(Ps2DiscType.Cd);
        _h.Read8(Cdvd + 0xF).Should().Be(0x12);
        Insert(Ps2DiscType.DvdDualLayer);
        _h.Read8(Cdvd + 0xF).Should().Be(0x14);
    }

    [Fact]
    public void A_nop_completes_with_the_cdvd_interrupt()
    {
        (_h.Read8(Cdvd + 5) & 0xC0).Should().Be(0x40, "ready, not busy");
        NCommand(0x00);
        Completed.Should().BeTrue();
        (Iop.InterruptStatus & (1u << 2)).Should().NotBe(0);
        _h.Write8(Cdvd + 8, 1);
        Completed.Should().BeFalse();
    }

    [Fact]
    public void A_read_without_a_disc_fails()
    {
        NCommand(0x06, ReadParams(16, 1));
        _h.Read8(Cdvd + 6).Should().Be(0x12, "no disc");
        (_h.Read8(Cdvd + 5) & 1).Should().Be(1);
    }

    [Fact]
    public void Read_dvd_delivers_2064_byte_blocks_through_dma_3_after_spinning_up()
    {
        Insert();
        StartDma3(0x40000, 2064, 2);
        NCommand(0x08, ReadParams(16, 2));
        (_h.Read8(Cdvd + 5) & 0x80).Should().Be(0x80, "busy");
        _h.Read8(Cdvd + 0xA).Should().Be(0x12, "seeking");

        _h.Run(Second / 3);
        Completed.Should().BeFalse("spinning up takes a third of a second");
        _h.Run(Second / 20);
        Completed.Should().BeTrue();

        var ram = Iop.Ram;
        ram[0x40000].Should().Be(0x20, "layer 0");
        ram.AsSpan(0x40001, 3).ToArray().Should().Equal(0x03, 0x00, 0x10);
        ram.AsSpan(0x40000 + 12, 6).ToArray().Should().Equal(1, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1');
        ram[0x40000 + 2064 + 12].Should().Be(0xFF, "the terminator in sector 17");
        ram.AsSpan(0x40000 + 2064 + 1, 3).ToArray().Should().Equal(0x03, 0x00, 0x11);
        (Iop.Read32(0x1F8010B8) & 0x01000000).Should().Be(0, "DMA 3 finished");
        _h.Read8(Cdvd + 0xA).Should().Be(0x0A, "paused");
        (_h.Read8(Cdvd + 5) & 0xC0).Should().Be(0x40);
    }

    [Fact]
    public void Read_cd_takes_one_sector_time_per_sector_once_spinning()
    {
        Insert(Ps2DiscType.Cd);
        NCommand(0x05, 18, 0, 0, 0); // seek to 18: spins the disc up
        _h.Run(Second / 2);
        Completed.Should().BeTrue();
        _h.Write8(Cdvd + 8, 1);

        // 24×: 20,480 cycles a sector; sector 20 is within the contiguous seek range.
        StartDma3(0x40000, 2048, 4);
        var start = Iop.Cycles;
        NCommand(0x06, ReadParams(20, 4, spindle: 5));
        long done = 0;
        while (!Completed && Iop.Cycles - start < Second)
        {
            _h.Run(256);
            done = Iop.Cycles - start;
        }

        Completed.Should().BeTrue();
        done.Should().BeInRange(6 * 20480 - 256, 6 * 20480 + 256, "a 2-sector seek and four sectors");
        Iop.Ram.AsSpan(0x40000 + 2048 * 3, 2048).ToArray().Should().Equal(Enumerable.Range(0, 2048).Select(i => (byte)(23 + i)));
    }

    [Fact]
    public void Read_cd_raw_modes_put_the_data_after_the_header_area()
    {
        Insert(Ps2DiscType.Cd);
        StartDma3(0x40000, 2328, 1);
        NCommand(0x06, ReadParams(18, 1, mode: 1));
        _h.Run(Second / 2);
        Completed.Should().BeTrue();
        Iop.Ram[0x40000 + 8].Should().Be(18, "the user data follows the 8-byte subheader");
    }

    [Fact]
    public void Get_toc_writes_the_dvd_format_information()
    {
        Insert();
        Iop.Write32(0x1F8010B0, 0x60000);
        NCommand(0x09);
        Completed.Should().BeTrue();
        Iop.Ram[0x60000].Should().Be(0x04, "single layer");
        BinaryPrimitives.ReadUInt32BigEndian(Iop.Ram.AsSpan(0x60000 + 20)).Should().Be((uint)(TestIso.Sectors + 0x30000 - 1));
    }

    [Fact]
    public void S_commands_report_the_mechacon_version_and_the_tray()
    {
        SCommand(0x03, 0x00).Should().Equal(0x03, 0x06, 0x02, 0x00);
        Insert();
        SCommand(0x06, 0x00).Should().Equal(0x00);
        (_h.Read8(Cdvd + 0xA) & 1).Should().Be(1, "the tray is open");
        _h.Read8(Cdvd + 0xF).Should().Be(0x00);
        (_h.Read8(Cdvd + 8) & 0x08).Should().NotBe(0, "the eject interrupt");
        SCommand(0x06, 0x01).Should().Equal(0x00);
        _h.Read8(Cdvd + 0xF).Should().Be(0x14);
    }

    [Fact]
    public void The_rtc_starts_at_its_fixed_time_and_follows_emulated_time()
    {
        Iop.Cdvd.RtcStart = new DateTime(2024, 5, 17, 12, 34, 56);
        SCommand(0x08).Should().Equal(0x00, 0x56, 0x34, 0x12, 0x00, 0x17, 0x05, 0x24);
        _h.Run(Second * 2);
        SCommand(0x08)[1].Should().Be(0x58);

        SCommand(0x09, 0x00, 0x00, 0x30, 0x23, 0x00, 0x31, 0x12, 0x99).Should().Equal(0x00);
        SCommand(0x08).Should().Equal(0x00, 0x00, 0x30, 0x23, 0x00, 0x31, 0x12, 0x99);
    }

    [Fact]
    public void Nvm_words_and_config_blocks_round_trip()
    {
        SCommand(0x0B, 0x00, 0x10, 0xAB, 0xCD).Should().Equal(0x00);
        SCommand(0x0A, 0x00, 0x10).Should().Equal(0x00, 0xAB, 0xCD);

        SCommand(0x40, 1, 1, 2).Should().Equal(0x00); // open area 1 for writing, two blocks
        var block = Enumerable.Range(0, 16).Select(i => (byte)(0x40 + i)).ToArray();
        SCommand(0x42, block).Should().Equal(0x00);
        SCommand(0x42, [.. block.Select(b => (byte)~b)]).Should().Equal(0x00);
        SCommand(0x43).Should().Equal(0x00);

        SCommand(0x40, 0, 1, 2).Should().Equal(0x00);
        SCommand(0x41).Should().Equal(block);
        SCommand(0x41).Should().Equal(block.Select(b => (byte)~b));
        SCommand(0x43);
    }

    [Fact]
    public void Identity_commands_answer_from_the_nvm()
    {
        SCommand(0x03, 0x44, 1, 2, 3, 4, 5, 6, 7, 8).Should().Equal(0x00);
        SCommand(0x03, 0x45).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7, 8);
        SCommand(0x12).Should().HaveCount(9, "the i.LINK id");
        SCommand(0x36).Should().HaveCount(15, "the region parameters");
        SCommand(0x1A, 1, 2, 3, 4).Should().Equal(new byte[] { 0x01 }, "boot certify");
    }
}
