using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;

namespace GameVault.Core.Tests.Cartridge;

/// <summary>Tier 3 boards and expansion audio (SPEC §4.5): banking, IRQs, sound and state.</summary>
public class Tier3MapperTests
{
    private static INesRom Rom(int mapper, int prg8K, int chr1K, int submapper = 0) =>
        INesRom.Parse(BankedRom.Build(mapper, prg8K, chr1K, submapper));

    [Fact]
    public void Vrc6_banks_and_wires_its_audio_into_the_apu()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(24, 32, 64));
        var vrc6 = (Vrc6)console.Mapper;
        vrc6.CpuWrite(0x8000, 3);
        vrc6.CpuWrite(0xC000, 9);
        vrc6.CpuWrite(0xD002, 7);

        vrc6.CpuRead(0x8000, 0).Should().Be(6);
        vrc6.CpuRead(0xC000, 0).Should().Be(9);
        vrc6.PpuRead(0x0800).Should().Be(7);
        console.Apu.ExpansionAudio.Should().BeSameAs(vrc6.ExpansionAudio);
    }

    [Fact]
    public void Vrc6b_swaps_the_two_register_lines()
    {
        var vrc6b = new Vrc6(Rom(26, 32, 64));

        vrc6b.CpuWrite(0xD001, 5); // A0 set → sub-register 2 on VRC6b

        vrc6b.PpuRead(0x0800).Should().Be(5);
    }

    [Fact]
    public void Vrc6_pulse_and_sawtooth_produce_sound()
    {
        var audio = new Vrc6Audio();
        audio.Write(9, 0, 0x7F);  // pulse 1: duty 7, volume 15
        audio.Write(9, 1, 0x10);
        audio.Write(9, 2, 0x80);  // enable
        audio.Write(11, 0, 0x2A); // saw rate
        audio.Write(11, 1, 0x20);
        audio.Write(11, 2, 0x80);

        var levels = new HashSet<float>();
        for (var i = 0; i < 4000; i++)
        {
            audio.Clock();
            levels.Add(audio.Output);
        }

        levels.Count.Should().BeGreaterThan(4, "the pulse toggles and the saw ramps");
        levels.Max().Should().BeLessThan(1f);

        audio.Write(9, 3, 1); // halt
        var held = audio.Output;
        audio.Clock();
        audio.Output.Should().Be(held);
    }

    [Theory]
    [InlineData(21, 0x02, 0x04)] // VRC4a: A1, A2
    [InlineData(21, 0x40, 0x80)] // VRC4c: A6, A7
    [InlineData(23, 0x01, 0x02)] // VRC4f: A0, A1
    [InlineData(23, 0x04, 0x08)] // VRC4e: A2, A3
    [InlineData(25, 0x02, 0x01)] // VRC4b: A1, A0
    [InlineData(25, 0x08, 0x04)] // VRC4d: A3, A2
    public void Vrc4_variants_decode_their_address_lines(int mapper, int a0, int a1)
    {
        var vrc = new Vrc24(Rom(mapper, 32, 256));

        vrc.CpuWrite((ushort)(0xB000 | a1), 0x03); // R1 low nibble
        vrc.CpuWrite((ushort)(0xB000 | a1 | a0), 0x01); // R1 high bits

        vrc.PpuRead(0x0400).Should().Be(0x13);
        vrc.CpuWrite((ushort)(0x9000 | a1), 0x02); // swap mode
        vrc.CpuWrite(0x8000, 5);
        vrc.CpuRead(0xC000, 0).Should().Be(5);
        vrc.CpuRead(0x8000, 0).Should().Be(30, "second-last bank moves to $8000");
    }

    [Fact]
    public void Vrc2a_drops_the_low_chr_bit()
    {
        var vrc2a = new Vrc24(Rom(22, 16, 128));

        vrc2a.CpuWrite(0xB000, 0x07);

        vrc2a.PpuRead(0x0000).Should().Be(3);
    }

    [Fact]
    public void Vrc_irq_counts_scanlines_and_reloads()
    {
        var vrc = new Vrc24(Rom(21, 32, 256));
        vrc.CpuWrite(0xF000, 0x0E); // latch low = $E
        vrc.CpuWrite(0xF002, 0x0F); // latch high = $F → $FE
        vrc.CpuWrite(0xF004, 0x02); // enable, scanline mode

        var cycles = 0;
        while (!vrc.IrqPending && cycles < 1000)
        {
            vrc.OnCpuCycle();
            cycles++;
        }

        cycles.Should().BeInRange(225, 230, "two scanlines of 113.67 cycles from $FE");
        vrc.CpuWrite(0xF006, 0);
        vrc.IrqPending.Should().BeFalse();
    }

    [Fact]
    public void Vrc7_banks_with_either_address_line()
    {
        var vrc7 = new Vrc7(Rom(85, 32, 64));
        vrc7.CpuWrite(0x8010, 4);
        vrc7.CpuWrite(0x8008, 5); // VRC7b line
        vrc7.CpuWrite(0xB000, 9);
        vrc7.CpuWrite(0xE000, 0x81);

        vrc7.CpuRead(0xA000, 0).Should().Be(5);
        vrc7.PpuRead(0x0800).Should().Be(9);
        vrc7.Mirroring.Should().Be(Mirroring.Horizontal);
    }

    [Fact]
    public void Fme7_banks_counts_down_cycles_and_plays_tones()
    {
        var fme = new Fme7(Rom(69, 32, 64));
        fme.CpuWrite(0x8000, 9);
        fme.CpuWrite(0xA000, 7);
        fme.CpuWrite(0x8000, 8);
        fme.CpuWrite(0xA000, 0xC0); // RAM at $6000, enabled
        fme.CpuWrite(0x6123, 0x42);

        fme.CpuRead(0x8000, 0).Should().Be(7);
        fme.CpuRead(0x6123, 0).Should().Be(0x42);

        fme.CpuWrite(0x8000, 14);
        fme.CpuWrite(0xA000, 9);
        fme.CpuWrite(0x8000, 15);
        fme.CpuWrite(0xA000, 0);
        fme.CpuWrite(0x8000, 13);
        fme.CpuWrite(0xA000, 0x81);
        for (var i = 0; i < 9; i++)
        {
            fme.OnCpuCycle();
            fme.IrqPending.Should().BeFalse();
        }

        fme.OnCpuCycle();
        fme.IrqPending.Should().BeTrue("the counter wrapped from $0000 to $FFFF");

        var audio = (Sunsoft5BAudio)fme.ExpansionAudio;
        fme.CpuWrite(0xC000, 0);
        fme.CpuWrite(0xE000, 0x40);
        fme.CpuWrite(0xC000, 8);
        fme.CpuWrite(0xE000, 0x0F);
        var levels = new HashSet<float>();
        for (var i = 0; i < 20000; i++)
        {
            audio.Clock();
            levels.Add(audio.Output);
        }

        levels.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Txsrom_routes_nametables_from_chr_bank_bit_7()
    {
        var mapper = new Mmc3(Rom(118, 16, 128));
        mapper.CpuWrite(0x8000, 0);
        mapper.CpuWrite(0x8001, 0x80);
        mapper.CpuWrite(0x8000, 1);
        mapper.CpuWrite(0x8001, 0x00);

        mapper.NametablePage(0).Should().Be(1);
        mapper.NametablePage(1).Should().Be(1);
        mapper.NametablePage(2).Should().Be(0);
    }

    [Fact]
    public void Tqrom_maps_chr_ram_with_bit_6()
    {
        var mapper = new Mmc3(Rom(119, 16, 64));
        mapper.CpuWrite(0x8000, 2);
        mapper.CpuWrite(0x8001, 0x41); // CHR-RAM bank 1 at $1000

        mapper.PpuWrite(0x1000, 0x5A);
        mapper.PpuRead(0x1000).Should().Be(0x5A);

        mapper.CpuWrite(0x8001, 0x05); // back to ROM
        mapper.PpuRead(0x1000).Should().Be(5);
        mapper.PpuWrite(0x1000, 0x99);
        mapper.PpuRead(0x1000).Should().Be(5, "CHR-ROM ignores writes");
    }

    [Fact]
    public void Namco163_counts_cycles_and_reads_nametables_from_chr_rom()
    {
        var n163 = new Namco163(Rom(19, 32, 64));
        n163.CpuWrite(0x5000, 0xFD);
        n163.CpuWrite(0x5800, 0xFF); // counter $7FFD, enabled
        n163.OnCpuCycle();
        n163.IrqPending.Should().BeFalse();
        n163.OnCpuCycle();
        n163.IrqPending.Should().BeTrue();

        var ciram = new byte[0x800];
        ciram[0x405] = 0x77;
        n163.CpuWrite(0xC000, 0xE1); // $2000 → CIRAM page 1
        n163.CpuWrite(0xC800, 0x09); // $2400 → CHR-ROM bank 9
        n163.ReadNametable(0x2005, ciram).Should().Be(0x77);
        n163.ReadNametable(0x2405, ciram).Should().Be(9);
    }

    [Fact]
    public void Namco163_wavetable_channel_outputs_its_samples()
    {
        var audio = new Namco163Audio();
        audio.SetAddress(0x80); // auto-increment from 0
        for (var i = 0; i < 16; i++)
        {
            audio.WriteData(i < 8 ? (byte)0xFF : (byte)0x00); // 32-sample square wave
        }

        audio.SetAddress(0x80 | 0x78); // channel 7 registers
        foreach (var b in new byte[] { 0x00, 0x00, 0x10, 0x00, 0xE0, 0x00, 0x00, 0x0F })
        {
            audio.WriteData(b);
        }

        var levels = new HashSet<float>();
        for (var i = 0; i < 30000; i++)
        {
            audio.Clock();
            levels.Add(audio.Output);
        }

        levels.Should().Contain(l => l > 0).And.Contain(l => l < 0);
    }

    [Fact]
    public void Mmc5_prg_modes_ram_banks_multiplier_and_fill_mode()
    {
        var mmc5 = new Mmc5(Rom(5, 32, 128));
        mmc5.CpuWrite(0x5100, 3);
        mmc5.CpuWrite(0x5114, 0x85); // ROM 5 at $8000
        mmc5.CpuWrite(0x5115, 0x02); // RAM 2 at $A000
        mmc5.CpuWrite(0x5102, 2);
        mmc5.CpuWrite(0x5103, 1);   // RAM writable
        mmc5.CpuWrite(0xA010, 0x33);

        mmc5.CpuRead(0x8000, 0).Should().Be(5);
        mmc5.CpuRead(0xA010, 0).Should().Be(0x33);
        mmc5.CpuRead(0xE000, 0).Should().Be(31, "$5117 powers on as the last bank");

        mmc5.CpuWrite(0x5205, 12);
        mmc5.CpuWrite(0x5206, 34);
        (mmc5.CpuRead(0x5205, 0) | (mmc5.CpuRead(0x5206, 0) << 8)).Should().Be(408);

        mmc5.CpuWrite(0x5105, 0xFF); // every nametable from fill mode
        mmc5.CpuWrite(0x5106, 0x42);
        mmc5.CpuWrite(0x5107, 0x02);
        var ciram = new byte[0x800];
        mmc5.ReadNametable(0x2010, ciram).Should().Be(0x42);
        mmc5.ReadNametable(0x23C0, ciram).Should().Be(0xAA);
    }

    [Fact]
    public void Mmc5_finds_scanlines_and_fires_its_irq_while_rendering()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(5, 32, 128));
        console.Power();
        console.Mapper.CpuWrite(0x5203, 100);
        console.Mapper.CpuWrite(0x5204, 0x80);

        var irqLine = -1;
        for (var i = 0; i < 200_000 && irqLine < 0; i++)
        {
            console.StepInstruction();
            if (console.Mapper.IrqPending && console.Ppu.Scanline < 240)
            {
                irqLine = console.Ppu.Scanline;
            }
        }

        irqLine.Should().BeInRange(99, 101, "the IRQ fires on the compare scanline");
    }

    public static TheoryData<int, int, int, int> Boards { get; } = new()
    {
        { 5, 32, 128, 0 },
        { 19, 32, 64, 0 },
        { 21, 32, 256, 0 },
        { 22, 16, 128, 0 },
        { 23, 32, 256, 0 },
        { 24, 32, 64, 0 },
        { 25, 32, 256, 0 },
        { 26, 32, 64, 0 },
        { 69, 32, 64, 0 },
        { 85, 32, 64, 0 },
        { 118, 16, 128, 0 },
        { 119, 16, 64, 0 },
    };

    [Theory]
    [MemberData(nameof(Boards))]
    public void Console_state_round_trips(int mapper, int prg8K, int chr1K, int submapper)
    {
        var image = BankedRom.Build(mapper, prg8K, chr1K, submapper);
        var original = NesConsole.FromBytes(image);
        original.EnableAudio();
        original.Power();
        for (var i = 0; i < 15; i++)
        {
            original.RunFrame();
        }

        var saved = original.SaveSnapshot();
        for (var i = 0; i < 15; i++)
        {
            original.RunFrame();
        }

        var restored = NesConsole.FromBytes(image);
        restored.EnableAudio();
        restored.LoadSnapshot(saved);
        for (var i = 0; i < 15; i++)
        {
            restored.RunFrame();
        }

        restored.Ppu.FrameBuffer.ToArray().Should().Equal(original.Ppu.FrameBuffer.ToArray());
        restored.SaveSnapshot().Should().Equal(original.SaveSnapshot());
    }
}
