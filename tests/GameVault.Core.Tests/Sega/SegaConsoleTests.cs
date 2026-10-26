using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.Sega;
using GameVault.Core.Systems;

namespace GameVault.Core.Tests.Sega;

/// <summary>The SG-1000 and Master System (SPEC §7.11, Phase 11), with small hand-assembled programs.</summary>
public sealed class SegaConsoleTests
{
    /// <summary>OUT ($BF): a VDP register write.</summary>
    private static byte[] SetRegister(int register, byte value) => [0x3E, value, 0xD3, 0xBF, 0x3E, (byte)(0x80 | register), 0xD3, 0xBF];

    private static byte[] Rom(byte[] main, byte[]? interruptHandler = null, int size = 0x8000)
    {
        var rom = new byte[size];
        Array.Fill(rom, (byte)0xFF);
        rom[0] = 0xC3; // JP $0100
        rom[1] = 0x00;
        rom[2] = 0x01;
        (interruptHandler ?? [0xFB, 0xED, 0x4D]).CopyTo(rom, 0x38); // default: EI; RETI
        main.CopyTo(rom, 0x100);
        return rom;
    }

    private static SegaConsole Sms(byte[] rom)
    {
        var console = SegaConsole.FromBytes(GameSystem.MasterSystem, rom);
        console.Power();
        return console;
    }

    [Fact]
    public void Mode4_draws_the_background_with_the_cram_colour_and_a_border()
    {
        var program = new List<byte> { 0xF3 };
        program.AddRange(SetRegister(0, 0x04)); // Mode 4
        program.AddRange(SetRegister(2, 0xFF)); // name table at $3800
        program.AddRange(SetRegister(1, 0x40)); // display on
        program.AddRange([0x3E, 0x00, 0xD3, 0xBF, 0x3E, 0xC0, 0xD3, 0xBF]); // CRAM address 0
        program.AddRange([0x3E, 0x03, 0xD3, 0xBE]); // colour 0 = red
        program.AddRange([0x18, 0xFE]); // JR $
        var console = Sms(Rom([.. program]));

        console.RunFrame();
        console.RunFrame();

        console.Vdp.ActiveHeight.Should().Be(192);
        var red = console.Palette[console.FrameBuffer[(24 + 100) * 256 + 128]];
        red.Should().Be(0xFFFF0000);
        console.Palette[console.FrameBuffer[5 * 256]].Should().Be(0xFF000000, "the border is the backdrop colour (CRAM 16, black)");
    }

    [Fact]
    public void Frame_interrupts_run_once_per_frame_in_im1()
    {
        // Handler: IN A,($BF) (acknowledge); LD HL,$C000; INC (HL); EI; RETI
        byte[] handler = [0xDB, 0xBF, 0x21, 0x00, 0xC0, 0x34, 0xFB, 0xED, 0x4D];
        var program = new List<byte> { 0xF3, 0xED, 0x56 };
        program.AddRange(SetRegister(0, 0x04));
        program.AddRange(SetRegister(1, 0x60)); // display and frame interrupts on
        program.AddRange([0xFB, 0x76, 0x18, 0xFD]); // EI; loop: HALT; JR loop
        var console = Sms(Rom([.. program], handler));

        for (var i = 0; i < 10; i++)
        {
            console.RunFrame();
        }

        console.Ram[0].Should().BeInRange(9, 10);
        console.FrameCount.Should().Be(10);
    }

    [Fact]
    public void Joypads_read_active_low_and_start_is_the_pause_nmi()
    {
        // loop: IN A,($DC); LD ($C001),A; IN A,($DD); LD ($C002),A; JR loop.  NMI at $66: LD HL,$C003; INC (HL); RETN
        byte[] main = [0xDB, 0xDC, 0x32, 0x01, 0xC0, 0xDB, 0xDD, 0x32, 0x02, 0xC0, 0x18, 0xF4];
        var rom = Rom(main);
        byte[] nmi = [0x21, 0x03, 0xC0, 0x34, 0xED, 0x45];
        nmi.CopyTo(rom, 0x66);
        var console = Sms(rom);

        console.SetControllerState(0, ControllerState.Right | ControllerState.A);
        console.SetControllerState(1, ControllerState.Up | ControllerState.B);
        console.RunFrame();

        console.Ram[1].Should().Be(unchecked((byte)~(0x08 | 0x20 | 0x40)));
        (console.Ram[2] & 0x3F).Should().Be(0x3F & ~0x04, "player 2's Button 1 is bit 2 of port B");

        console.SetControllerState(0, ControllerState.Start);
        console.RunFrame();
        console.SetControllerState(0, ControllerState.Start); // held: no second NMI
        console.RunFrame();
        console.Ram[3].Should().Be(1);
    }

    [Fact]
    public void The_sega_mapper_switches_16k_banks()
    {
        // LD A,3; LD ($FFFE),A; LD A,($4000); LD ($C000),A; JR $
        var rom = Rom([0x3E, 0x03, 0x32, 0xFE, 0xFF, 0x3A, 0x00, 0x40, 0x32, 0x00, 0xC0, 0x18, 0xFE], size: 0x10000);
        rom[3 * 0x4000] = 0x5A;
        var console = Sms(rom);
        console.Cartridge.Mapper.Should().Be(SegaMapper.Sega);

        console.RunFrame();

        console.Ram[0].Should().Be(0x5A);
    }

    [Fact]
    public void Codemasters_boards_are_detected_from_their_checksum()
    {
        var rom = new byte[0x20000];
        rom[0x7FE6] = 0x34;
        rom[0x7FE7] = 0x12;
        var inverse = 0x10000 - 0x1234;
        rom[0x7FE8] = (byte)inverse;
        rom[0x7FE9] = (byte)(inverse >> 8);
        SegaCartridge.DetectMapper(rom).Should().Be(SegaMapper.Codemasters);
        SegaCartridge.DetectMapper(new byte[0x8000]).Should().Be(SegaMapper.None);
    }

    [Fact]
    public void The_psg_makes_sound()
    {
        // Channel 0 tone $0FE at full volume: $80|$0E, $0F; $90 (volume 0 = loudest). Then JR $.
        var console = Sms(Rom([0x3E, 0x8E, 0xD3, 0x7F, 0x3E, 0x0F, 0xD3, 0x7F, 0x3E, 0x90, 0xD3, 0x7F, 0x18, 0xFE]));
        var audio = console.EnableAudio(48000);

        console.RunFrame();
        console.RunFrame();

        var samples = new float[4096];
        var count = audio.Read(samples);
        count.Should().BeGreaterThan(1000);
        samples.Take(count).Max().Should().BeGreaterThan(0.05f);
    }

    [Fact]
    public void Save_states_round_trip_and_replay_identically()
    {
        byte[] handler = [0xDB, 0xBF, 0x21, 0x00, 0xC0, 0x34, 0xFB, 0xED, 0x4D];
        var program = new List<byte> { 0xF3, 0xED, 0x56 };
        program.AddRange(SetRegister(0, 0x04));
        program.AddRange(SetRegister(1, 0x60));
        program.AddRange([0xFB, 0x76, 0x18, 0xFD]);
        var rom = Rom([.. program], handler);
        var console = Sms(rom);
        for (var i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        var state = console.SaveSnapshot();
        for (var i = 0; i < 5; i++)
        {
            console.RunFrame();
        }

        var expectedRam = console.Ram.ToArray();
        var expectedFrame = console.FrameBuffer.ToArray();

        var other = Sms(rom);
        other.LoadSnapshot(state);
        for (var i = 0; i < 5; i++)
        {
            other.RunFrame();
        }

        other.Ram.ToArray().Should().Equal(expectedRam);
        other.FrameBuffer.ToArray().Should().Equal(expectedFrame);

        var wrongGame = Sms(Rom([0x18, 0xFE]));
        wrongGame.Invoking(c => c.LoadSnapshot(state)).Should().Throw<GameVault.Core.State.StateFormatException>();
    }

    [Fact]
    public void Sg1000_graphics_i_shows_the_backdrop_through_transparent_tiles()
    {
        var program = new List<byte> { 0xF3, 0x31, 0x00, 0xC4 };
        program.AddRange(SetRegister(7, 0x04)); // backdrop dark blue
        program.AddRange(SetRegister(1, 0xC0)); // 16K, display on
        program.AddRange([0x18, 0xFE]);
        var console = SegaConsole.FromBytes(GameSystem.Sg1000, Rom([.. program]));
        console.Power();

        console.RunFrame();
        console.RunFrame();

        console.Palette[console.FrameBuffer[(24 + 50) * 256 + 10]].Should().Be(0xFF5455ED);
        console.System.Should().Be(GameSystem.Sg1000);
    }

    [Fact]
    public void The_v_counter_jumps_back_after_line_218_on_ntsc()
    {
        var vdp = new SegaVdp(VdpModel.Sms);
        vdp.Power();
        vdp.StartLine(0xDA);
        vdp.VCounter.Should().Be(0xDA);
        vdp.StartLine(0xDB);
        vdp.VCounter.Should().Be(0xD5);
        vdp.StartLine(261);
        vdp.VCounter.Should().Be(0xFF);
    }
}
