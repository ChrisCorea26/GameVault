using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>Which chip a cartridge gets, the message when its ROM is missing, and a whole console talking to a DSP.</summary>
public sealed class SnesChipCartridgeTests
{
    // The echo program from NecDspTests: wait for a word, add one, send it back, park.
    private static uint[] EchoProgram()
    {
        static uint Op(int src, int dst, int alu = 0) => (uint)((alu << 16) | (src << 4) | dst);
        static uint Jp(int brch, int target) => (2u << 22) | (uint)(brch << 13) | (uint)(target << 2);
        var program = new uint[NecDsp.ProgramWords];
        uint[] code = [Op(8, 1), Jp(0x0BE, 1), Op(8, 1), Op(0, 0, alu: 9), Op(1, 6), Jp(0x100, 5)];
        code.CopyTo(program, 0);
        for (var i = code.Length; i < program.Length; i++)
        {
            program[i] = Jp(0x100, i);
        }

        return program;
    }

    private static byte[] Dump(uint[] program)
    {
        var dump = new byte[NecDsp.DumpSize];
        for (var i = 0; i < program.Length; i++)
        {
            dump[i * 3] = (byte)program[i];
            dump[i * 3 + 1] = (byte)(program[i] >> 8);
            dump[i * 3 + 2] = (byte)(program[i] >> 16);
        }

        return dump;
    }

    [Fact]
    public void A_dsp_game_without_its_program_rom_says_where_to_put_it()
    {
        var rom = SnesTests.Rom([0x80, 0xFE], chip: 0x03);

        var act = () => new SnesCartridge(rom, _ => null);

        act.Should().Throw<MissingChipRomException>()
            .Which.Message.Should().Contain("DSP-1").And.Contain("dsp1b.rom").And.Contain("Settings → Super Nintendo");
    }

    [Fact]
    public void A_dsp_game_gets_the_dsp_at_its_board_s_addresses()
    {
        var dump = Dump(EchoProgram());
        var lo = new SnesCartridge(SnesTests.Rom([0x80, 0xFE], chip: 0x05, ramSize: 1), name => name == "dsp1b.rom" ? dump : null);
        var hi = new SnesCartridge(SnesTests.Rom([0x80, 0xFE], hiRom: true, chip: 0x03), name => name == "dsp1.rom" ? dump : null);

        lo.Coprocessor.Should().BeOfType<NecDsp>().Which.Name.Should().Be("DSP-1");
        lo.Coprocessor!.Claims(0x308000).Should().BeTrue();
        lo.Coprocessor.Claims(0xB0C000).Should().BeTrue("and its FastROM mirror");
        lo.Coprocessor.Claims(0x208000).Should().BeFalse();
        lo.HasBattery.Should().BeTrue("chip $05: DSP, RAM and battery");
        hi.Coprocessor!.Claims(0x006000).Should().BeTrue();
        hi.Coprocessor.Claims(0x207000).Should().BeFalse();
    }

    [Fact]
    public void Chips_gamevault_does_not_emulate_yet_are_refused_clearly()
    {
        var act = () => new SnesCartridge(SnesTests.Rom([0x80, 0xFE], chip: 0x43));

        act.Should().Throw<UnsupportedChipException>().Which.Chip.Should().Be("S-DD1");
    }

    [Fact]
    public void The_cpu_sends_a_word_to_the_dsp_and_reads_the_answer()
    {
        byte[] cpu =
        [
            0x18, 0xFB,                         // CLC; XCE (8-bit A)
            0xA9, 0x41, 0x8F, 0x00, 0x80, 0x30, // LDA #$41; STA $308000
            0xA9, 0x10, 0x8F, 0x00, 0x80, 0x30, // LDA #$10; STA $308000
            0xAF, 0x00, 0xC0, 0x30,             // wait: LDA $30C000 (SR's high byte)
            0x10, 0xFA,                         // BPL wait (RQM is bit 7)
            0xAF, 0x00, 0x80, 0x30, 0x85, 0x10, // LDA $308000; STA $10
            0xAF, 0x00, 0x80, 0x30, 0x85, 0x11, // LDA $308000; STA $11
            0x80, 0xFE,
        ];
        var dump = Dump(EchoProgram());
        var console = new SnesConsole(new SnesCartridge(SnesTests.Rom(cpu, chip: 0x03), name => name == "dsp1b.rom" ? dump : null));
        console.Power();

        console.RunFrame();

        console.Wram[0x10].Should().Be(0x42);
        console.Wram[0x11].Should().Be(0x10);

        // The chip's state rides in the save state: a reloaded console carries on identically.
        var state = console.SaveSnapshot();
        console.RunFrame();
        var after = console.SaveSnapshot();
        var copy = new SnesConsole(new SnesCartridge(SnesTests.Rom(cpu, chip: 0x03), name => name == "dsp1b.rom" ? dump : null));
        copy.Power();
        copy.LoadSnapshot(state);
        copy.RunFrame();
        copy.SaveSnapshot().Should().Equal(after);
    }
}
