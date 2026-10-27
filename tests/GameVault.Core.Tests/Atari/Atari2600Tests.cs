using FluentAssertions;
using GameVault.Core.Atari;
using GameVault.Core.Input;

namespace GameVault.Core.Tests.Atari;

/// <summary>A few 6507 instructions for test programs, assembled at $F000 (the 4K window's mirror games use).</summary>
internal sealed class Asm
{
    private readonly List<byte> _code = [];
    private readonly Dictionary<string, int> _labels = [];
    private readonly List<(int At, string Label)> _branches = [];
    private readonly List<(int At, string Label)> _jumps = [];

    public int Here => 0xF000 + _code.Count;

    public Asm Label(string name)
    {
        _labels[name] = Here;
        return this;
    }

    public Asm Op(params byte[] bytes)
    {
        _code.AddRange(bytes);
        return this;
    }

    public Asm Lda(byte value) => Op(0xA9, value);

    public Asm Ldx(byte value) => Op(0xA2, value);

    public Asm Ldy(byte value) => Op(0xA0, value);

    /// <summary>STA zero page (the TIA's registers and the RAM).</summary>
    public Asm Sta(byte address) => Op(0x85, address);

    /// <summary>STA absolute (the RIOT's ports and timer).</summary>
    public Asm StaAbs(ushort address) => Op(0x8D, (byte)address, (byte)(address >> 8));

    public Asm LdaAbs(ushort address) => Op(0xAD, (byte)address, (byte)(address >> 8));

    public Asm Nop(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Op(0xEA);
        }

        return this;
    }

    public Asm Dex() => Op(0xCA);

    public Asm Bne(string label)
    {
        _branches.Add((_code.Count + 1, label));
        return Op(0xD0, 0);
    }

    public Asm Jmp(string label)
    {
        _jumps.Add((_code.Count + 1, label));
        return Op(0x4C, 0, 0);
    }

    /// <summary>STA WSYNC <paramref name="lines"/> times, with X counting.</summary>
    public Asm WaitLines(int lines, string name)
    {
        Ldx((byte)lines).Label(name).Sta(0x02).Dex().Bne(name);
        return this;
    }

    /// <summary>A 4K cartridge: the code at its start, the reset vector pointing to it.</summary>
    public byte[] Build(int size = 4096)
    {
        foreach (var (at, label) in _branches)
        {
            _code[at] = (byte)(sbyte)(_labels[label] - (0xF000 + at + 1));
        }

        foreach (var (at, label) in _jumps)
        {
            _code[at] = (byte)_labels[label];
            _code[at + 1] = (byte)(_labels[label] >> 8);
        }

        var rom = new byte[size];
        _code.CopyTo(rom, 0);
        rom[size - 4] = 0x00; // reset vector $F000
        rom[size - 3] = 0xF0;
        return rom;
    }
}

/// <summary>The Atari 2600 (phase 19) through the whole console, with programs written here.</summary>
public sealed class Atari2600Tests
{
    private const byte Vsync = 0x00, Vblank = 0x01, Nusiz0 = 0x04, Colup0 = 0x06, Colup1 = 0x07, Colupf = 0x08, Colubk = 0x09;
    private const byte Ctrlpf = 0x0A, Pf0 = 0x0D, Pf1 = 0x0E, Pf2 = 0x0F, Resp0 = 0x10, Resp1 = 0x11, Resbl = 0x14;
    private const byte Audc0 = 0x15, Audf0 = 0x17, Audv0 = 0x19, Grp0 = 0x1B, Grp1 = 0x1C, Enabl = 0x1F, Hmp0 = 0x20, Hmove = 0x2A;

    /// <summary>
    /// A standard NTSC frame around <paramref name="setup"/> (run during VBLANK, after the 37th line's WSYNC is skipped
    /// for it) and <paramref name="visible"/> (each of the 192 lines' code after its WSYNC).
    /// </summary>
    private static Atari2600Console Run(Action<Asm> setup, Action<Asm>? perLine = null, int frames = 3)
    {
        var asm = new Asm();
        asm.Op(0x78, 0xD8, 0xA2, 0xFF, 0x9A); // SEI; CLD; LDX #$FF; TXS
        asm.Label("frame")
            .Lda(2).Sta(Vblank).Sta(Vsync).Sta(0x02).Sta(0x02).Sta(0x02).Lda(0).Sta(Vsync)
            .WaitLines(36, "vb");
        setup(asm);
        asm.Sta(0x02).Lda(0).Sta(Vblank);
        asm.Ldy(192).Label("line").Sta(0x02);
        perLine?.Invoke(asm);
        asm.Op(0x88).Bne("line"); // DEY; BNE
        asm.Lda(2).Sta(Vblank).WaitLines(30, "os").Jmp("frame");
        var console = new Atari2600Console(new Atari2600Cartridge(asm.Build()));
        console.Power();
        for (var i = 0; i < frames; i++)
        {
            console.RunFrame();
        }

        return console;
    }

    private static int Pixel(Atari2600Console console, int x, int row) => console.FrameBuffer[row * Tia.Width + x];

    [Fact]
    public void Frames_follow_vsync_and_the_picture_starts_where_vblank_ends()
    {
        var console = Run(a => a.Lda(0x84).Sta(Colubk));

        console.ScreenHeight.Should().Be(Tia.NtscHeight);
        Pixel(console, 80, 0).Should().Be(0x84 >> 1, "the first line after VBLANK is the picture's first");
        Pixel(console, 80, 191).Should().Be(0x84 >> 1);
        Pixel(console, 80, 193).Should().Be(0, "overscan is blanked");
        console.FrameCount.Should().Be(3);
    }

    [Fact]
    public void The_playfield_repeats_or_reflects_and_score_mode_colours_its_halves()
    {
        var plain = Run(a => a.Lda(0x1E).Sta(Colupf).Lda(0x84).Sta(Colubk).Lda(0xFF).Sta(Pf1).Lda(0x10).Sta(Pf0));
        Pixel(plain, 0, 10).Should().Be(0x0F, "PF0 bit 4 is the first four pixels");
        Pixel(plain, 4, 10).Should().Be(0x42, "PF0 bits 5–7 are clear");
        Pixel(plain, 16, 10).Should().Be(0x0F, "PF1 covers pixels 16–47");
        Pixel(plain, 47, 10).Should().Be(0x0F);
        Pixel(plain, 48, 10).Should().Be(0x42);
        Pixel(plain, 96, 10).Should().Be(0x0F, "the right half repeats the left");

        var reflected = Run(a => a.Lda(0x1E).Sta(Colupf).Lda(0xFF).Sta(Pf1).Lda(0x01).Sta(Ctrlpf));
        Pixel(reflected, 96, 10).Should().Be(0, "reflected, PF1 is at the right half's far end");
        Pixel(reflected, 112, 10).Should().Be(0x0F);
        Pixel(reflected, 143, 10).Should().Be(0x0F);

        var score = Run(a => a.Lda(0x44).Sta(Colup0).Lda(0x86).Sta(Colup1).Lda(0xFF).Sta(Pf2).Lda(0x02).Sta(Ctrlpf));
        Pixel(score, 50, 10).Should().Be(0x22, "the left half takes player 0's colour");
        Pixel(score, 130, 10).Should().Be(0x43, "the right half player 1's");
    }

    [Fact]
    public void Respx_places_a_player_after_the_beam_and_hmove_moves_it()
    {
        // After WSYNC, 10 NOPs (20 cycles) and STA RESP0: the write lands on colour clock 69, so the player starts 5
        // pixels after pixel 1.
        var console = Run(a => a.Lda(0x0E).Sta(Colup0).Lda(0x80).Sta(Grp0).Sta(0x02).Nop(10).Sta(Resp0));
        Pixel(console, 6, 20).Should().Be(0x07);
        Pixel(console, 5, 20).Should().Be(0);
        Pixel(console, 7, 20).Should().Be(0, "only the leftmost bit is set");

        // HMP0 = $F0 (-1: one pixel right), HMOVE during horizontal blank: the line's first 8 pixels are black.
        var moved = Run(
            a => a.Lda(0x0E).Sta(Colup0).Lda(0x84).Sta(Colubk).Lda(0x80).Sta(Grp0).Sta(0x02).Nop(10).Sta(Resp0)
                .Lda(0xF0).Sta(Hmp0).Sta(0x02).Sta(Hmove));
        Pixel(moved, 7, 20).Should().Be(0x07);
        Pixel(moved, 6, 20).Should().Be(0x42);
        Pixel(moved, 0, 191).Should().Be(0x42, "only the HMOVE line was blanked, and it was in VBLANK");
    }

    [Fact]
    public void Player_copies_sizes_and_reflection()
    {
        // Two copies 16 apart (NUSIZ 1), from pixel 3 (RESP0 in horizontal blank).
        var copies = Run(a => a.Lda(0x0E).Sta(Colup0).Lda(0x01).Sta(Nusiz0).Lda(0x80).Sta(Grp0).Sta(0x02).Sta(Resp0));
        Pixel(copies, 3, 5).Should().Be(0x07);
        Pixel(copies, 19, 5).Should().Be(0x07);
        Pixel(copies, 35, 5).Should().Be(0);

        var wide = Run(a => a.Lda(0x0E).Sta(Colup0).Lda(0x05).Sta(Nusiz0).Lda(0x80).Sta(Grp0).Sta(0x02).Sta(Resp0));
        Pixel(wide, 3, 5).Should().Be(0x07, "double size: the bit covers two pixels");
        Pixel(wide, 4, 5).Should().Be(0x07);
        Pixel(wide, 5, 5).Should().Be(0);

        var reflected = Run(a => a.Lda(0x0E).Sta(Colup0).Lda(0x80).Sta(Grp0).Lda(0x08).Sta(0x0B).Sta(0x02).Sta(Resp0));
        Pixel(reflected, 3, 5).Should().Be(0);
        Pixel(reflected, 10, 5).Should().Be(0x07, "reflected, bit 7 is the rightmost pixel");
    }

    [Fact]
    public void Overlapping_objects_set_their_collision_latches()
    {
        // Player 0 at pixel 3 and an 8-pixel ball at 2, over the playfield: CXP0FB (bits 7 PF, 6 BL) and CXBLPF.
        var console = Run(a => a.Lda(0xFF).Sta(Grp0).Sta(Pf0).Lda(0x30).Sta(Ctrlpf).Lda(0x02).Sta(Enabl).Sta(0x02).Sta(Resp0).Sta(Resbl));

        console.Tia.Read(0x02).Should().Be(0xC0, "P0 met the playfield and the ball");
        console.Tia.Read(0x06).Should().Be(0x80, "the ball met the playfield");
        console.Tia.Read(0x07).Should().Be(0x00, "player 1 drew nothing");
        console.Tia.Write(0x2C, 0);
        console.Tia.Read(0x02).Should().Be(0x00, "CXCLR");
    }

    [Fact]
    public void Mid_line_writes_change_the_rest_of_that_line()
    {
        // After each WSYNC: wait 30 cycles (into the visible line), then change the background colour.
        var console = Run(
            a => a.Lda(0x00).Sta(Colubk),
            line => line.Lda(0x00).Sta(Colubk).Nop(13).Lda(0x84).Sta(Colubk));

        Pixel(console, 2, 50).Should().Be(0, "the line's start keeps the old colour");
        Pixel(console, 150, 50).Should().Be(0x42, "the line's end has the new one");
    }

    [Fact]
    public void The_riot_timer_counts_by_its_interval_then_every_cycle_with_its_flag()
    {
        var riot = new Riot();
        riot.Power();
        riot.Write(0x296, 2); // TIM64T = 2
        for (var i = 0; i < 64; i++)
        {
            riot.Tick();
        }

        riot.Read(0x284).Should().Be(1);
        for (var i = 0; i < 128; i++)
        {
            riot.Tick();
        }

        riot.Read(0x285).Should().Be(0x80, "it passed zero");
        riot.Read(0x284).Should().Be(0xFF);
        riot.Tick();
        riot.Read(0x284).Should().Be(0xFE, "counting every cycle now");
    }

    [Fact]
    public void Joysticks_fire_buttons_and_console_switches()
    {
        var console = Run(_ => { });
        console.SetControllerState(0, ControllerState.Up | ControllerState.B | ControllerState.Start);
        console.SetControllerState(1, ControllerState.Right);

        console.Riot.Read(0x280).Should().Be(0b1110_0111, "player 1 up (bit 4), player 2 right (bit 3), low when pushed");
        console.Tia.Read(0x0C).Should().Be(0x00, "INPT4: player 1's fire button");
        console.Tia.Read(0x0D).Should().Be(0x80);
        (console.Riot.Read(0x282) & 0x03).Should().Be(0x02, "Reset is pressed (bit 0 low)");
        (console.Riot.Read(0x282) & 0x08).Should().Be(0x08, "colour");
    }

    [Fact]
    public void A_pure_tone_plays_at_its_divider_s_pitch()
    {
        // AUDC0 = 4 (a square wave), AUDF0 = 31: 31.4 kHz / (2 × 32), about 491 Hz.
        var console = Run(a => a.Lda(4).Sta(Audc0).Lda(31).Sta(Audf0).Lda(15).Sta(Audv0), frames: 1);
        var audio = console.EnableAudio(48000);
        audio.ApplyNesFilters = false; // the raw square wave, so each edge crosses the middle once
        var samples = new List<float>();
        var buffer = new float[4096];
        for (var i = 0; i < 30; i++)
        {
            console.RunFrame();
            var count = audio.Read(buffer);
            samples.AddRange(buffer.Take(count));
        }

        var playing = samples.Skip(4800).ToList();
        var middle = (playing.Max() + playing.Min()) / 2;
        var crossings = playing.Zip(playing.Skip(1)).Count(p => p.First <= middle && p.Second > middle);
        (crossings / (playing.Count / 48000.0)).Should().BeApproximately(491, 25);
    }

    [Fact]
    public void Save_states_resume_exactly()
    {
        var first = Run(a => a.Lda(0x84).Sta(Colubk).Lda(4).Sta(Audc0).Lda(15).Sta(Audv0), frames: 2);
        first.EnableAudio();
        var state = first.SaveSnapshot();
        first.RunFrame();
        var expected = first.SaveSnapshot();

        var second = Run(a => a.Lda(0x84).Sta(Colubk).Lda(4).Sta(Audc0).Lda(15).Sta(Audv0), frames: 0);
        second.EnableAudio();
        second.LoadSnapshot(state);
        second.RunFrame();

        second.SaveSnapshot().Should().Equal(expected);
    }
}
