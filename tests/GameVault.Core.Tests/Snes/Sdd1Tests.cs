using GameVault.Core.Snes;

namespace GameVault.Core.Tests.Snes;

/// <summary>The S-DD1 (outstanding-work plan §5): its memory controller, and the decompressor on the DMA.</summary>
public sealed class Sdd1Tests
{
    private static byte[] Store(int address, byte value) => [0xA9, value, 0x8D, (byte)address, (byte)(address >> 8)];

    // WRAM $0100 through $2180, 16 bytes from $C00000 on channel 0 with the S-DD1 on, megabyte 1 at $C0; then $4801.
    private static readonly byte[] Program =
    [
        0x18, 0xFB,
        .. Store(0x2181, 0x00), .. Store(0x2182, 0x01), .. Store(0x2183, 0x00),
        .. Store(0x4300, 0x00), .. Store(0x4301, 0x80),
        .. Store(0x4302, 0x00), .. Store(0x4303, 0x00), .. Store(0x4304, 0xC0),
        .. Store(0x4305, 0x10), .. Store(0x4306, 0x00),
        .. Store(0x4804, 0x01), .. Store(0x4800, 0x01), .. Store(0x4801, 0x01),
        .. Store(0x420B, 0x01),
        0xAD, 0x01, 0x48, 0x85, 0x10, // LDA $4801; STA $10
        0x80, 0xFE,
    ];

    private static (SnesConsole Console, SnesCartridge Cartridge) Run(byte fill)
    {
        var rom = SnesTests.Rom(Program, size: 0x200000, chip: 0x43);
        rom.AsSpan(0x100000).Fill(fill);
        var cartridge = new SnesCartridge(rom);
        var console = new SnesConsole(cartridge);
        console.Power();
        console.RunFrame();
        return (console, cartridge);
    }

    [Fact]
    public void A_dma_from_an_enabled_channel_reads_the_decompressor_and_then_turns_it_off()
    {
        var (zeros, cartridge) = Run(0x00);
        cartridge.Coprocessor.Should().BeOfType<Sdd1>();
        zeros.Wram.Slice(0x100, 16).ToArray().Should().OnlyContain(b => b == 0, "a stream of zero code words is all most-probable bits");
        zeros.Wram[0x10].Should().Be(0, "the channel's $4801 bit clears when its count runs out");

        var (ones, _) = Run(0xFF);
        ones.Wram.Slice(0x100, 16).ToArray().Should().NotEqual(Enumerable.Repeat((byte)0xFF, 16), "the bytes are decoded, not copied");
    }

    [Fact]
    public void The_memory_controller_maps_a_megabyte_into_each_sixteen_banks()
    {
        var rom = new byte[0x400000];
        for (var block = 0; block < 4; block++)
        {
            rom.AsSpan(block * 0x100000, 0x100000).Fill((byte)(block + 1));
        }

        var sdd1 = new Sdd1(rom);
        sdd1.Power();
        sdd1.Read(0xF00000, 0).Should().Be(4);
        sdd1.Write(0x004807, 0x01);
        sdd1.Read(0xF00000, 0).Should().Be(2);
        sdd1.Read(0x004807, 0).Should().Be(1);
    }

    [Fact]
    public void A_save_state_mid_stream_resumes_the_same_bytes()
    {
        var rom = new byte[0x200000];
        new Random(7).NextBytes(rom);
        var first = new Sdd1(rom);
        first.Power();
        first.SnoopDma(0x4302, 0x00);
        first.SnoopDma(0x4303, 0x10);
        first.SnoopDma(0x4304, 0xC0);
        first.SnoopDma(0x4305, 0x00);
        first.SnoopDma(0x4306, 0x01);
        first.Write(0x4800, 1);
        first.Write(0x4801, 1);
        for (var i = 0; i < 40; i++)
        {
            first.Read(0xC01000, 0);
        }

        var writer = new GameVault.Core.State.StateWriter();
        first.SaveState(writer);
        var expected = Enumerable.Range(0, 40).Select(_ => first.Read(0xC01000, 0)).ToArray();

        var second = new Sdd1(rom);
        second.LoadState(new GameVault.Core.State.StateReader(writer.ToArray()));
        Enumerable.Range(0, 40).Select(_ => second.Read(0xC01000, 0)).ToArray().Should().Equal(expected);
    }
}
