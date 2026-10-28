using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>The ST018 (snes-chips-3.md §4): the ARM behind its mailboxes, on a program written here.</summary>
public sealed class St018Tests
{
    /// <summary>
    /// An st018.rom whose program waits for the SNES's byte, adds one and sends it back, forever:
    /// r1 = $40000000; poll the status (bit 3: a byte from the SNES); read it; add 1; send it.
    /// </summary>
    private static byte[] Dump()
    {
        uint[] program =
        [
            0xE3A01101, // $00: MOV r1, #0x40000000
            0xE5D12020, // $04: LDRB r2, [r1, #0x20] (the status)
            0xE3120008, // $08: TST r2, #8
            0x0AFFFFFC, // $0C: BEQ $04
            0xE5D13010, // $10: LDRB r3, [r1, #0x10] (the SNES's byte)
            0xE2833001, // $14: ADD r3, r3, #1
            0xE5C13000, // $18: STRB r3, [r1] (to the SNES)
            0xEAFFFFF8, // $1C: B $04
        ];
        var dump = new byte[St018.DumpSize];
        for (var i = 0; i < program.Length; i++)
        {
            BitConverter.GetBytes(program[i]).CopyTo(dump, i * 4);
        }

        dump[St018.ProgramRomSize] = 0x5A; // the data ROM's first byte
        return dump;
    }

    private long _clock;

    private void Run(St018 chip, long clocks = 100_000)
    {
        _clock += clocks;
        chip.CatchUp(_clock);
    }

    private St018 Chip()
    {
        var rom = SnesTests.Rom([0x80, 0xFE], chip: 0xF5, ramSize: 1);
        var cartridge = new SnesCartridge(rom, name => name == "st018.rom" ? Dump() : null);
        var chip = cartridge.Coprocessor.Should().BeOfType<St018>().Subject;
        chip.Power();
        return chip;
    }

    [Fact]
    public void Without_its_rom_the_game_says_where_to_put_it()
    {
        var act = () => new SnesCartridge(SnesTests.Rom([0x80, 0xFE], chip: 0xF5));

        act.Should().Throw<MissingChipRomException>().Which.FileNames.Should().Equal("st018.rom");
    }

    [Fact]
    public void The_arm_becomes_ready_after_its_reset_delay()
    {
        var chip = Chip();
        chip.Write(0x003804, 1); // hold in reset
        Run(chip);
        (chip.Read(0x003804, 0) & 0x80).Should().Be(0, "held");

        chip.Write(0x003804, 0);
        Run(chip, 30_000);
        (chip.Read(0x003804, 0) & 0x80).Should().Be(0, "the reset sequence takes 65,536 clocks");
        Run(chip, 60_000);
        (chip.Read(0x803804, 0) & 0x80).Should().Be(0x80, "ready, in the $80 mirror too");
    }

    [Fact]
    public void The_snes_and_the_arm_trade_bytes_through_the_mailboxes()
    {
        var chip = Chip();
        Run(chip, 200_000);

        for (byte value = 40; value < 45; value++)
        {
            chip.Write(0x003802, value);
            (chip.Read(0x003804, 0) & 8).Should().Be(8, "the byte waits for the ARM");
            Run(chip, 1_000);
            (chip.Read(0x003804, 0) & 9).Should().Be(1, "the ARM took it and answered");
            chip.Read(0x003800, 0).Should().Be((byte)(value + 1));
            (chip.Read(0x003804, 0) & 1).Should().Be(0, "reading the answer clears its flag");
        }
    }

    [Fact]
    public void A_save_state_round_trips_the_arm_and_the_mailboxes()
    {
        var chip = Chip();
        Run(chip, 200_000);
        chip.Write(0x003802, 9);

        var writer = new StateWriter();
        chip.SaveState(writer);
        Run(chip, 1_000);
        var answer = chip.Read(0x003800, 0);

        chip.LoadState(new StateReader(writer.ToArray()));
        _clock -= 1_000;
        Run(chip, 1_000);
        chip.Read(0x003800, 0).Should().Be(answer).And.Be(10);
    }
}
