using System.Text;
using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Fds;

namespace GameVault.Core.Tests.Fds;

/// <summary>
/// Famicom Disk System (Phase 7). Everything here is synthetic: disk images are built block by block, and
/// the "BIOS" is a stub routine written for the test. The real BIOS never enters the repository.
/// </summary>
public class FdsTests
{
    private static FdsAdapter Adapter(int sides = 1) =>
        new(FdsAdapter.BiosRom(FdsTestImages.StubBios()), FdsImage.Parse(FdsTestImages.Image(sides, header: false)));

    [Fact]
    public void Crc_is_crc16_kermit()
    {
        FdsDisk.Crc("123456789"u8).Should().Be(0x2189, "the CRC-16/KERMIT check value");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Images_parse_with_or_without_the_fwnes_header(bool header)
    {
        var image = FdsImage.Parse(FdsTestImages.Image(2, header));

        image.Sides.Should().HaveCount(2);
        image.Sides[1][0x15].Should().Be(1);
        FdsImage.IsFdsImage(FdsTestImages.Image(1, header)).Should().BeTrue();
        FdsImage.IsFdsImage(new byte[100]).Should().BeFalse();
    }

    [Fact]
    public void Tracks_have_gaps_start_marks_and_crcs()
    {
        var track = FdsDisk.BuildTrack(FdsTestImages.Side());

        track.AsSpan(0, FdsDisk.LeadInGap).ToArray().Should().OnlyContain(b => b == 0);
        var block1 = FdsDisk.LeadInGap;
        track[block1].Should().Be(0x80);
        track[block1 + 1].Should().Be(0x01);
        FdsDisk.Crc(track.AsSpan(block1, 1 + 56 + 2)).Should().Be(0, "the stored CRC completes the block");

        var block2 = block1 + 59 + FdsDisk.BlockGap;
        track[block2].Should().Be(0x80);
        track[block2 + 1].Should().Be(0x02);

        var block4 = block2 + 5 + FdsDisk.BlockGap + 19 + FdsDisk.BlockGap;
        track.AsSpan(block4, 6).ToArray().Should().Equal(0x80, 0x04, 0xDE, 0xAD, 0xBE, 0xEF);
        FdsDisk.Crc(track.AsSpan(block4, 1 + 5 + 2)).Should().Be(0);
    }

    [Fact]
    public void The_bios_must_be_8k()
    {
        var act = () => FdsAdapter.BiosRom(new byte[0x1000]);
        act.Should().Throw<RomFormatException>().WithMessage("*8 KB*");
    }

    [Fact]
    public void Ram_covers_6000_to_dfff_and_the_bios_sits_at_e000()
    {
        var fds = Adapter();
        fds.CpuWrite(0x6000, 0x11);
        fds.CpuWrite(0xDFFF, 0x22);
        fds.CpuWrite(0xE000, 0x33);

        fds.CpuRead(0x6000, 0).Should().Be(0x11);
        fds.CpuRead(0xDFFF, 0).Should().Be(0x22);
        fds.CpuRead(0xE000, 0).Should().Be(0x78, "the BIOS is read-only");
    }

    [Fact]
    public void Timer_irq_counts_cpu_cycles_and_repeats()
    {
        var fds = Adapter();
        fds.CpuWrite(0x4023, 0x01);
        fds.CpuWrite(0x4020, 3);
        fds.CpuWrite(0x4021, 0);
        fds.CpuWrite(0x4022, 0x03); // enabled, repeat

        for (var i = 0; i < 3; i++)
        {
            fds.OnCpuCycle();
            fds.IrqPending.Should().BeFalse();
        }

        fds.OnCpuCycle();
        fds.IrqPending.Should().BeTrue();
        (fds.CpuRead(0x4030, 0) & 1).Should().Be(1);
        fds.IrqPending.Should().BeFalse("reading $4030 acknowledges");

        for (var i = 0; i < 4; i++)
        {
            fds.OnCpuCycle();
        }

        fds.IrqPending.Should().BeTrue("the timer reloads and repeats");

        fds.CpuWrite(0x4023, 0x00);
        fds.IrqPending.Should().BeFalse("disabling disk I/O stops the timer");
    }

    [Fact]
    public void Mirroring_follows_4025_bit_3()
    {
        var fds = Adapter();
        fds.CpuWrite(0x4023, 0x01);
        fds.CpuWrite(0x4025, 0x2E);
        fds.Mirroring.Should().Be(Mirroring.Horizontal);
        fds.CpuWrite(0x4025, 0x26);
        fds.Mirroring.Should().Be(Mirroring.Vertical);
    }

    [Fact]
    public void A_stub_bios_reads_the_first_block_through_the_drive()
    {
        var console = NesConsole.FromFds(FdsTestImages.Image(1, header: true), FdsTestImages.StubBios());
        console.Power();
        for (var i = 0; i < 40; i++)
        {
            console.RunFrame();
        }

        var ram = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            ram[i] = console.Bus.Peek((ushort)(0x0200 + i));
        }

        ram[0].Should().Be(0x80, "the gap ends with the $80 mark");
        ram[1].Should().Be(0x01);
        Encoding.ASCII.GetString(ram, 2, 14).Should().Be("*NINTENDO-HVC*");
        var crc = (ushort)(ram[57] | (ram[58] << 8));
        crc.Should().Be(FdsDisk.Crc(ram.AsSpan(0, 57)), "the CRC follows the 56 data bytes");
    }

    [Fact]
    public void Crc_status_is_good_after_a_whole_block_and_bad_when_the_data_changed()
    {
        bool ReadBlockOneCrcError(FdsAdapter fds)
        {
            fds.CpuWrite(0x4023, 0x01);
            fds.CpuWrite(0x4025, 0x65);
            var bytes = 0;
            for (var cycle = 0; cycle < 2_000_000 && bytes < 59; cycle++)
            {
                fds.OnCpuCycle();
                if ((fds.CpuPeek(0x4030) & 0x80) != 0)
                {
                    fds.CpuRead(0x4030, 0);
                    bytes++;
                    if (bytes == 57)
                    {
                        fds.CpuWrite(0x4025, 0x75); // CRC control on for the two CRC bytes
                    }
                }
            }

            return (fds.CpuPeek(0x4030) & 0x10) != 0;
        }

        ReadBlockOneCrcError(Adapter()).Should().BeFalse();

        var bad = Adapter();
        bad.Tracks[0][FdsDisk.LeadInGap + 5] ^= 0xFF;
        ReadBlockOneCrcError(bad).Should().BeTrue();
    }

    [Fact]
    public void Writes_change_the_track_and_the_patch_restores_them()
    {
        var fds = Adapter();
        var original = fds.Tracks.Select(t => t.ToArray()).ToList();
        fds.CpuWrite(0x4023, 0x01);
        fds.CpuWrite(0x4024, 0x5A);
        fds.CpuWrite(0x4025, 0x61); // motor on, write mode, transfers on

        var written = -1;
        for (var cycle = 0; cycle < 200_000 && written < 0; cycle++)
        {
            fds.OnCpuCycle();
            if ((fds.CpuPeek(0x4030) & 0x80) != 0)
            {
                written = fds.HeadPosition - 1;
            }
        }

        written.Should().BeGreaterThanOrEqualTo(0);
        fds.Tracks[0][written].Should().Be(0x5A);
        fds.IsDiskModified.Should().BeTrue();

        var patch = FdsDiskPatch.Create(original, fds.Tracks);
        var fresh = Adapter();
        fresh.ApplyDiskPatch(patch);
        fresh.Tracks[0].Should().Equal(fds.Tracks[0]);

        var foreign = () => fresh.ApplyDiskPatch("nope"u8);
        foreign.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Switching_sides_leaves_the_drive_empty_for_a_moment()
    {
        var fds = Adapter(sides: 2);
        fds.CpuWrite(0x4023, 0x01);
        fds.InsertedSide.Should().Be(0);

        fds.InsertSide(1);
        fds.InsertedSide.Should().BeNull();
        (fds.CpuRead(0x4032, 0) & 1).Should().Be(1, "no disk");

        for (var i = 0; i < FdsAdapter.SwitchDelayCycles; i++)
        {
            fds.OnCpuCycle();
        }

        fds.InsertedSide.Should().Be(1);
        (fds.CpuRead(0x4032, 0) & 1).Should().Be(0);

        fds.Eject();
        fds.InsertedSide.Should().BeNull();
    }

    [Fact]
    public void Wave_ram_and_volume_envelope_drive_the_sound_output()
    {
        var fds = Adapter();
        var audio = fds.ExpansionAudio;
        fds.CpuWrite(0x4023, 0x83);
        fds.CpuWrite(0x4089, 0x80); // wave RAM writable, full master volume
        for (var i = 0; i < 64; i++)
        {
            fds.CpuWrite((ushort)(0x4040 + i), (byte)(i < 32 ? 63 : 0));
        }

        fds.CpuRead(0x4040, 0).Should().Be(63);
        fds.CpuWrite(0x4089, 0x00);
        fds.CpuWrite(0x4080, 0xA0); // envelope off, gain 32
        fds.CpuWrite(0x4082, 0x00);
        fds.CpuWrite(0x4083, 0x01); // frequency $100, running

        var high = 0;
        var low = 0;
        for (var i = 0; i < 20000; i++)
        {
            audio.Clock();
            if (audio.Output > 0.2f)
            {
                high++;
            }
            else if (audio.Output == 0)
            {
                low++;
            }
        }

        high.Should().BeGreaterThan(5000);
        low.Should().BeGreaterThan(5000);
        (fds.CpuRead(0x4090, 0) & 0x3F).Should().Be(32);
    }

    [Fact]
    public void Console_state_round_trips_including_the_disk()
    {
        var image = FdsTestImages.Image(2, header: true);
        var bios = FdsTestImages.StubBios();
        var original = NesConsole.FromFds(image, bios);
        original.EnableAudio();
        original.Power();
        for (var i = 0; i < 25; i++)
        {
            original.RunFrame();
        }

        var saved = original.SaveSnapshot();
        for (var i = 0; i < 15; i++)
        {
            original.RunFrame();
        }

        var restored = NesConsole.FromFds(image, bios);
        restored.EnableAudio();
        restored.LoadSnapshot(saved);
        for (var i = 0; i < 15; i++)
        {
            restored.RunFrame();
        }

        restored.SaveSnapshot().Should().Equal(original.SaveSnapshot());
        restored.Fds!.HeadPosition.Should().Be(original.Fds!.HeadPosition);
    }
}
