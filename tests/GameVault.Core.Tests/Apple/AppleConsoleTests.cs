using FluentAssertions;
using GameVault.Core.Apple;
using GameVault.Core.Input;

namespace GameVault.Core.Tests.Apple;

/// <summary>
/// The Apple II (SPEC §7.11, Phase 12), with a tiny test ROM written here: Apple's own ROMs are copyrighted, so
/// the tests never use them.
/// </summary>
public sealed class AppleConsoleTests
{
    /// <summary>A ROM whose reset vector runs <paramref name="program"/> at $D000 (12 KB) or $C000-based IIe space.</summary>
    internal static AppleRoms TestRoms(byte[] program, bool iie = false, byte[]? diskBoot = null)
    {
        var rom = new byte[iie ? 0x4000 : 0x3000];
        Array.Fill(rom, (byte)0xEA);
        var origin = 0xD000 - (0x10000 - rom.Length);
        program.CopyTo(rom, origin);
        rom[^4] = 0x00; // reset vector: $D000
        rom[^3] = 0xD0;
        return new AppleRoms(rom, diskBoot);
    }

    private static AppleConsole Machine(byte[]? program = null, AppleModel? model = null, bool iie = false)
    {
        var console = new AppleConsole(TestRoms(program ?? [0x4C, 0x00, 0xD0], iie), model);
        console.Power();
        return console;
    }

    /// <summary>The colour of Apple pixel (x, y): 280×192, each two dots wide and two lines tall.</summary>
    private static ushort Pixel(AppleConsole console, int x, int y, int dot = 0) => console.FrameBuffer[y * 2 * AppleVideo.Width + x * 2 + dot];

    [Fact]
    public void Text_mode_draws_characters_normal_and_inverse()
    {
        var console = Machine();
        console.Write(0x0400, 0xC1); // 'A'
        console.Write(0x0401, 0x01); // inverse 'A'

        console.RunFrame();

        // 'A' row 0 is .###. in columns 1–5 of the cell: dots 2–4 lit.
        Pixel(console, 0, 0).Should().Be(AppleVideo.Black);
        Pixel(console, 2, 0).Should().Be(AppleVideo.White);
        Pixel(console, 4, 0).Should().Be(AppleVideo.White);
        Pixel(console, 5, 0).Should().Be(AppleVideo.Black);
        Pixel(console, 7 + 0, 0).Should().Be(AppleVideo.White, "inverse: the background is lit");
        Pixel(console, 7 + 2, 0).Should().Be(AppleVideo.Black);
    }

    [Fact]
    public void Lo_res_and_hi_res_use_their_colours()
    {
        var console = Machine();
        console.Read(0xC050); // graphics
        console.Read(0xC052); // full screen
        console.Write(0x0400, 0x1F); // top block white, bottom block magenta
        console.RunFrame();
        Pixel(console, 0, 0).Should().Be(15);
        Pixel(console, 0, 4).Should().Be(1);

        console.Read(0xC057); // hi-res
        console.Write(0x2000, 0x01); // pixel 0 alone: violet
        console.Write(0x2028, 0x02); // line 64, pixel 1 alone: green
        console.Write(0x2050, 0x81); // line 128, pixel 0 with the high bit: blue, half a pixel later
        console.Write(0x2400, 0x03); // line 1, pixels 0 and 1: white
        console.RunFrame();

        Pixel(console, 0, 0).Should().Be(AppleVideo.Violet);
        Pixel(console, 1, 64).Should().Be(AppleVideo.Green);
        Pixel(console, 0, 128, dot: 1).Should().Be(AppleVideo.Blue);
        Pixel(console, 0, 128, dot: 0).Should().Be(AppleVideo.Black, "shifted half a pixel right");
        Pixel(console, 0, 1).Should().Be(AppleVideo.White);
        Pixel(console, 1, 1).Should().Be(AppleVideo.White);
    }

    [Fact]
    public void Keys_wait_in_the_latch_until_the_strobe_is_cleared()
    {
        var console = Machine();
        console.TypeKey((byte)'a');

        console.Read(0xC000).Should().Be(0xC1, "the II+ has no lowercase");
        console.Read(0xC010);
        console.Read(0xC000).Should().Be(0x41);
    }

    [Fact]
    public void The_language_card_needs_two_reads_to_write_and_banks_d000()
    {
        var console = Machine();
        console.Read(0xC083);
        console.Read(0xC083); // read RAM, write enabled, bank 2
        console.Write(0xD000, 0x42);
        console.Write(0xE000, 0x43);
        console.Read(0xD000).Should().Be(0x42);

        console.Read(0xC08B);
        console.Read(0xC08B); // bank 1
        console.Read(0xD000).Should().NotBe(0x42);
        console.Read(0xE000).Should().Be(0x43, "$E000–$FFFF isn't banked");

        console.Read(0xC082); // read ROM
        console.Read(0xD000).Should().Be(0x4C, "the test ROM's JMP");
    }

    [Fact]
    public void The_iie_switches_writes_and_reads_to_auxiliary_memory()
    {
        var console = Machine(iie: true, model: AppleModel.IIeEnhanced);
        console.Write(0x0800, 0x11);
        console.Write(0xC005, 0); // RAMWRT on
        console.Write(0x0800, 0x22);
        console.Write(0xC004, 0);
        console.Read(0x0800).Should().Be(0x11);
        console.Write(0xC003, 0); // RAMRD on
        console.Read(0x0800).Should().Be(0x22);
        console.Read(0xC013).Should().BeGreaterThanOrEqualTo(0x80, "RAMRD reads back as on");
        console.Cpu.Variant.Should().Be(GameVault.Core.Cpu.CpuVariant.Cmos65C02);
    }

    [Fact]
    public void Joystick_and_buttons_come_from_the_pad()
    {
        // Trigger the paddle timers, then count how long paddle 0 stays high.
        var console = Machine();
        console.SetControllerState(0, ControllerState.Right | ControllerState.A);
        console.Read(0xC061).Should().Be(0x80);
        console.Read(0xC062).Should().Be(0);
        console.Read(0xC070);
        console.Read(0xC064).Should().Be(0x80, "a stick held right takes 255 × 11 cycles to time out");
    }

    [Fact]
    public void Toggling_the_speaker_makes_sound()
    {
        // LDA $C030; LDX #0; delay: DEX; BNE delay; JMP $D000: a click every ~1,290 cycles, a 400 Hz square wave.
        var console = Machine([0xAD, 0x30, 0xC0, 0xA2, 0x00, 0xCA, 0xD0, 0xFD, 0x4C, 0x00, 0xD0]);
        var audio = console.EnableAudio(48000);

        console.RunFrame();
        console.RunFrame();

        var samples = new float[4096];
        var count = audio.Read(samples);
        count.Should().BeGreaterThan(1000);
        samples.Take(count).Max().Should().BeGreaterThan(0.05f);
    }

    [Fact]
    public void A_6502_loop_reads_disk_bytes_through_the_controller()
    {
        // LDX #$60; LDA $C089,X (motor on); LDA $C08E,X (read); LDY #0
        // loop: LDA $C08C,X; BPL loop; STA $0300,Y; INY; BNE loop; JMP *
        byte[] program =
        [
            0xA2, 0x60, 0xBD, 0x89, 0xC0, 0xBD, 0x8E, 0xC0, 0xA0, 0x00,
            0xBD, 0x8C, 0xC0, 0x10, 0xFB, 0x99, 0x00, 0x03, 0xC8, 0xD0, 0xF5,
            0x4C, 0x15, 0xD0,
        ];
        var console = Machine(program);
        var image = new byte[AppleDisk.DiskBytes];
        new Random(5).NextBytes(image);
        console.InsertDisk(0, AppleDisk.FromSectors(image, proDosOrder: false));

        console.RunFrame();

        var read = console.MainMemory.Slice(0x300, 256).ToArray();
        read.Should().OnlyContain(b => b >= 0x96, "every byte read is a valid disk byte");
        var expected = console.Disk.DiskIn(0)!.TrackAt(0)!.ToNibbles();
        ContainsRun(expected, read.AsSpan(8, 32).ToArray()).Should().BeTrue("the bytes read are the track's, in order, with none skipped or doubled");
    }

    [Fact]
    public void Bytes_written_in_write_mode_read_back()
    {
        var disk = new DiskII();
        var image = AppleDisk.FromSectors(new byte[AppleDisk.DiskBytes], proDosOrder: false);
        disk.Insert(0, image);
        long cycle = 0;
        disk.Access(0x9, false, 0, cycle); // motor on
        disk.Access(0xD, false, 0, cycle); // Q6 on
        byte[] written = [0xD5, 0xAA, 0x96, 0xFF, 0xFE, 0xAB];
        foreach (var b in written)
        {
            disk.Access(0xF, true, b, cycle); // Q7 on, load the register
            cycle += 32;                      // eight bit times
        }

        disk.Access(0xE, false, 0, cycle); // back to reading
        disk.Access(0xC, false, 0, cycle);
        image.IsModified.Should().BeTrue();

        var track = image.TrackAt(0)!;
        var bits = new List<bool>();
        for (var i = 0; i < written.Length * 8; i++)
        {
            bits.Add(track[i]);
        }

        var bytes = Enumerable.Range(0, written.Length).Select(i => (byte)bits.Skip(i * 8).Take(8).Aggregate(0, (v, bit) => (v << 1) | (bit ? 1 : 0))).ToArray();
        bytes.Should().Equal(written);
    }

    [Fact]
    public void Disk_writes_save_as_a_patch_of_the_changed_tracks_and_apply_to_a_fresh_copy()
    {
        var disk = new DiskII();
        var image = AppleDisk.FromSectors(new byte[AppleDisk.DiskBytes], proDosOrder: false);
        var original = image.SnapshotTracks();
        disk.Insert(0, image);
        disk.Access(0x9, false, 0, 0);
        disk.Access(0xD, false, 0, 0);
        disk.Access(0xF, true, 0xD5, 0);
        disk.Access(0xE, false, 0, 32);

        var patch = image.CreatePatch(original);
        patch.Length.Should().Be(4 + 8 + original[0].Length, "only track 0 changed");

        var fresh = AppleDisk.FromSectors(new byte[AppleDisk.DiskBytes], proDosOrder: false);
        fresh.ApplyPatch(patch);
        for (var t = 0; t < AppleDisk.Tracks; t++)
        {
            fresh.TrackData[t].Bits.Should().Equal(image.TrackData[t].Bits, $"track {t}");
        }

        var act = () => fresh.ApplyPatch(new byte[] { 1, 2, 3, 4 });
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void The_stepper_moves_the_head_a_half_track_per_phase()
    {
        var disk = new DiskII();
        // DOS seeks one track out: phase 1 on, phase 0 off, phase 2 on, phase 1 off.
        disk.Access(0x3, write: false, 0, 0);
        disk.Access(0x0, write: false, 0, 0);
        disk.Access(0x5, write: false, 0, 0);
        disk.Access(0x2, write: false, 0, 0);
        disk.QuarterTrack.Should().Be(4);
    }

    [Fact]
    public void Save_states_round_trip_and_replay_identically()
    {
        // A counter in $10 and a speaker click each loop.
        byte[] program = [0xE6, 0x10, 0xAD, 0x30, 0xC0, 0x4C, 0x00, 0xD0];
        var console = Machine(program);
        console.RunFrame();
        var state = console.SaveSnapshot();
        console.RunFrame();
        console.RunFrame();
        var expected = console.MainMemory.ToArray();

        var other = Machine(program);
        other.LoadSnapshot(state);
        other.RunFrame();
        other.RunFrame();
        other.MainMemory.ToArray().Should().Equal(expected);
        other.FrameBuffer.ToArray().Should().Equal(console.FrameBuffer.ToArray());
    }

    private static bool ContainsRun(byte[] haystack, byte[] run)
    {
        for (var i = 0; i < haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < run.Length && match; j++)
            {
                match = haystack[(i + j) % haystack.Length] == run[j];
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
