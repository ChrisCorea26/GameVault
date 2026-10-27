using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>The ST010/ST011's µPD96050 (outstanding-work plan §5): the larger program, its halves, and the shared RAM.</summary>
public sealed class SetaDspTests
{
    private static uint Ld(int value, int dst) => (3u << 22) | (uint)(value << 6) | (uint)dst;

    private static uint Jp(int brch, int target) => (2u << 22) | (uint)(brch << 13) | (uint)(target << 2);

    private static uint[] Program()
    {
        var program = new uint[NecDsp.Upd96050ProgramWords];
        for (var i = 0; i < program.Length; i++)
        {
            program[i] = Jp(i >= 0x2000 ? 0x101 : 0x100, i & 0x7FF); // park
        }

        program[0] = Ld(5, 4);              // DP = 5
        program[1] = Ld(0xBEEF, 15);        // RAM[5] = $BEEF
        program[2] = Jp(0x101, 3);          // HJMP: $2003, in the upper half
        program[0x2003] = Ld(0xCAFE, 15);   // RAM[5] = $CAFE
        program[0x2004] = Ld(0x7F0, 4);     // DP = $7F0: all 11 bits
        program[0x2005] = Ld(0x1234, 15);
        return program;
    }

    private static byte[] Dump(uint[] program)
    {
        var dump = new byte[NecDsp.Upd96050DumpSize];
        for (var i = 0; i < program.Length; i++)
        {
            dump[i * 3] = (byte)program[i];
            dump[i * 3 + 1] = (byte)(program[i] >> 8);
            dump[i * 3 + 2] = (byte)(program[i] >> 16);
        }

        return dump;
    }

    [Fact]
    public void Hjmp_runs_the_upper_half_and_the_snes_reads_the_data_ram()
    {
        var rom = SnesTests.Rom([0x80, 0xFE], chip: 0xF6);
        var cartridge = new SnesCartridge(rom, name => name == "st010.rom" ? Dump(Program()) : null);
        var dsp = cartridge.Coprocessor.Should().BeOfType<NecDsp>().Subject;
        dsp.Name.Should().Be("ST010");
        cartridge.HasBattery.Should().BeTrue();
        dsp.Claims(0x600001).Should().BeTrue();
        dsp.Claims(0xE80FFF).Should().BeTrue();
        dsp.Claims(0x700000).Should().BeFalse();

        for (var i = 0; i < 10; i++)
        {
            dsp.Step();
        }

        dsp.Read(0x68000A, 0).Should().Be(0xFE, "RAM[5]'s low byte: the HJMP reached $2003");
        dsp.Read(0x68000B, 0).Should().Be(0xCA);
        dsp.Ram(0x7F0).Should().Be(0x1234);
        dsp.Write(0x680FE0, 0x55);
        cartridge.BatteryRam[0xFE0].Should().Be(0x55, "the data RAM is the save file");
    }

    [Fact]
    public void Without_its_program_rom_the_player_is_told_which_file()
    {
        var act = () => new SnesCartridge(SnesTests.Rom([0x80, 0xFE], chip: 0xF6), _ => null);

        act.Should().Throw<MissingChipRomException>().Which.Message.Should().Contain("st010.rom");
    }
}
