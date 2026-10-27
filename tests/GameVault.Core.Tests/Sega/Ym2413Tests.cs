using GameVault.Core.Sega;
using GameVault.Core.Systems;

namespace GameVault.Core.Tests.Sega;

/// <summary>The Japanese Master System's FM Sound Unit (outstanding-work plan §8).</summary>
public sealed class Ym2413Tests
{
    private static byte[] Out(int port, byte value) => [0x3E, value, 0xD3, (byte)port];

    private static byte[] Fm(int register, byte value) => [.. Out(0xF0, (byte)register), .. Out(0xF1, value)];

    private static byte[] Rom(byte[] main, bool japaneseHeader = false)
    {
        var rom = new byte[0x8000];
        Array.Fill(rom, (byte)0xFF);
        rom[0] = 0xC3; // JP $0100
        rom[1] = 0x00;
        rom[2] = 0x01;
        rom[0x38] = 0xFB; // EI; RETI
        rom[0x39] = 0xED;
        rom[0x3A] = 0x4D;
        main.CopyTo(rom, 0x100);
        if (japaneseHeader)
        {
            "TMR SEGA"u8.CopyTo(rom.AsSpan(0x7FF0));
            rom[0x7FFF] = 0x3C; // region 3 (Japanese Master System), 32 KB
        }

        return rom;
    }

    private static SegaConsole Sms(byte[] rom, bool? fm)
    {
        var console = SegaConsole.FromBytes(GameSystem.MasterSystem, rom, fmUnit: fm);
        console.Power();
        return console;
    }

    /// <summary>The detection games do: write to $F2 and read it back ($C000 holds what came back).</summary>
    private static byte[] Detect() => [.. Out(0xF2, 0x01), 0xDB, 0xF2, 0x32, 0x00, 0xC0, 0x18, 0xFE];

    [Fact]
    public void Games_find_the_unit_by_reading_back_its_control_port()
    {
        var with = Sms(Rom(Detect()), fm: true);
        var without = Sms(Rom(Detect()), fm: false);

        with.RunFrame();
        without.RunFrame();

        with.Ram[0].Should().Be(0x01);
        without.Ram[0].Should().Be(0xFF, "an export console reads the joypad port there");
    }

    [Fact]
    public void Japanese_cartridges_get_the_unit_by_their_header()
    {
        Sms(Rom([0x18, 0xFE], japaneseHeader: true), fm: null).Fm.Should().NotBeNull();
        Sms(Rom([0x18, 0xFE]), fm: null).Fm.Should().BeNull();
    }

    [Fact]
    public void A_note_plays_at_its_pitch_through_the_console_s_audio()
    {
        // Instrument 3 (piano) at full volume, A4: F-number 290, block 4 (440 Hz), then the FM output on.
        var console = Sms(Rom([.. Fm(0x30, 0x30), .. Fm(0x10, 290 & 0xFF), .. Fm(0x20, 0x10 | (4 << 1) | (290 >> 8)), .. Out(0xF2, 1), 0x18, 0xFE]), fm: true);
        var audio = console.EnableAudio(48000);

        var samples = new List<float>();
        var buffer = new float[4096];
        for (var frame = 0; frame < 30; frame++)
        {
            console.RunFrame();
            var count = audio.Read(buffer);
            samples.AddRange(buffer.Take(count));
        }

        var playing = samples.Skip(4800).ToList();
        playing.Max().Should().BeGreaterThan(0.01f, "the note sounds");
        var crossings = playing.Zip(playing.Skip(1)).Count(p => p.First <= 0 && p.Second > 0);
        (crossings / (playing.Count / 48000.0)).Should().BeApproximately(440, 20);
    }

    [Fact]
    public void Rhythm_mode_sounds_the_bass_drum()
    {
        var console = Sms(Rom([.. Fm(0x36, 0x00), .. Fm(0x16, 0x20), .. Fm(0x26, 0x05), .. Fm(0x0E, 0x30), .. Out(0xF2, 1), 0x18, 0xFE]), fm: true);
        var audio = console.EnableAudio(48000);

        console.RunFrame();
        console.RunFrame();

        var buffer = new float[4096];
        var count = audio.Read(buffer);
        buffer.Take(count).Select(Math.Abs).Max().Should().BeGreaterThan(0.01f);
    }

    [Fact]
    public void A_save_state_with_the_unit_resumes_exactly()
    {
        var rom = Rom([.. Fm(0x30, 0x51), .. Fm(0x10, 0x80), .. Fm(0x20, 0x16), .. Fm(0x0E, 0x21), .. Out(0xF2, 1), 0x18, 0xFE]);
        var first = Sms(rom, fm: true);
        first.EnableAudio();
        first.RunFrame();
        var state = first.SaveSnapshot();
        first.RunFrame();
        var expected = first.SaveSnapshot();

        var second = Sms(rom, fm: true);
        second.EnableAudio();
        second.LoadSnapshot(state);
        second.RunFrame();

        second.SaveSnapshot().Should().Equal(expected);
        second.Fm!.Control.Should().Be(1);
    }
}

/// <summary>The SC-3000's keyboard on the PPI (outstanding-work plan §8).</summary>
public sealed class Sc3000KeyboardTests
{
    // Select row 2 (OUT ($DE)), read $DC and $DD into $C000/$C001; select row 7 and read $DC into $C002.
    private static readonly byte[] Program =
    [
        0x3E, 0x02, 0xD3, 0xDE, 0xDB, 0xDC, 0x32, 0x00, 0xC0, 0xDB, 0xDD, 0x32, 0x01, 0xC0,
        0x3E, 0x07, 0xD3, 0xDE, 0xDB, 0xDC, 0x32, 0x02, 0xC0, 0x18, 0xFE,
    ];

    private static SegaConsole Sc3000()
    {
        var rom = new byte[0x8000];
        Program.CopyTo(rom, 0);
        var console = SegaConsole.FromBytes(GameSystem.Sg1000, rom, keyboard: true);
        console.Power();
        return console;
    }

    [Fact]
    public void A_held_key_reads_low_in_its_row_and_row_7_is_the_joypads()
    {
        var console = Sc3000();
        console.HasKeyboard.Should().BeTrue();
        var rows = new ushort[7];
        rows[2] = (1 << 2) | (1 << 8); // D and 0
        console.SetKeyboardRows(rows);
        console.SetControllerState(0, Core.Input.ControllerState.Up);

        console.RunFrame();

        console.Ram[0].Should().Be(0xFB, "D is PA2 of row 2, active low");
        console.Ram[1].Should().Be(0xFE, "0 is PB0 of row 2; PB4–7 read high");
        (console.Ram[2] & 1).Should().Be(0, "row 7 is joypad 1: up is pressed");
    }

    [Fact]
    public void An_sg1000_without_the_keyboard_reads_its_joypads_on_those_ports()
    {
        var rom = new byte[0x8000];
        Program.CopyTo(rom, 0);
        var console = SegaConsole.FromBytes(GameSystem.Sg1000, rom);
        console.Power();
        var rows = new ushort[7];
        rows[2] = 0xFFF;
        console.SetKeyboardRows(rows);

        console.RunFrame();

        console.Ram[0].Should().Be(0xFF, "no keyboard: nothing pressed");
    }
}
