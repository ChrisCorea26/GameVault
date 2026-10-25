using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cartridge;

/// <summary>Banking, mirroring and IRQ rules for the Tier 1 boards (NESdev Wiki pages cited in each class).</summary>
public class MapperTests
{
    private static INesRom Rom(int mapper, int prg8K, int chr1K, int submapper = 0) =>
        INesRom.Parse(BankedRom.Build(mapper, prg8K, chr1K, submapper));

    private static void Mmc1Write(Mmc1 mmc1, ushort address, int value)
    {
        for (var bit = 0; bit < 5; bit++)
        {
            mmc1.OnCpuCycle();
            mmc1.OnCpuCycle(); // never on consecutive cycles
            mmc1.CpuWrite(address, (byte)((value >> bit) & 1));
        }
    }

    [Fact]
    public void Factory_creates_every_tier_1_board()
    {
        MapperFactory.Create(Rom(1, 16, 64)).Should().BeOfType<Mmc1>();
        MapperFactory.Create(Rom(2, 16, 0)).Should().BeOfType<UxRom>();
        MapperFactory.Create(Rom(3, 4, 32)).Should().BeOfType<CnRom>();
        MapperFactory.Create(Rom(4, 16, 128)).Should().BeOfType<Mmc3>();
        MapperFactory.Create(Rom(7, 16, 0)).Should().BeOfType<AxRom>();
    }

    [Fact]
    public void UxRom_switches_8000_and_fixes_the_last_bank()
    {
        var mapper = new UxRom(Rom(2, 16, 0));

        mapper.CpuWrite(0x8000, 3);

        mapper.CpuRead(0x8000, 0).Should().Be(6, "16 KB bank 3 starts at 8 KB bank 6");
        mapper.CpuRead(0xA000, 0).Should().Be(7);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
    }

    [Fact]
    public void UxRom_submapper_2_has_bus_conflicts()
    {
        var mapper = new UxRom(Rom(2, 16, 0, submapper: 2));

        // The ROM at $C000 holds 14 (0b1110); writing 3 there leaves 3 & 14 = 2.
        mapper.CpuWrite(0xC000, 3);

        mapper.CpuRead(0x8000, 0).Should().Be(4);
    }

    [Fact]
    public void CnRom_switches_8k_chr()
    {
        var mapper = new CnRom(Rom(3, 4, 32));

        mapper.CpuWrite(0x8000, 2);

        mapper.PpuRead(0x0000).Should().Be(16);
        mapper.PpuRead(0x1C00).Should().Be(23);
    }

    [Fact]
    public void AxRom_switches_32k_and_selects_one_screen()
    {
        var mapper = new AxRom(Rom(7, 16, 0));

        mapper.CpuWrite(0x8000, 0x12);

        mapper.CpuRead(0x8000, 0).Should().Be(8);
        mapper.CpuRead(0xE000, 0).Should().Be(11);
        mapper.Mirroring.Should().Be(Mirroring.SingleScreenB);
    }

    [Fact]
    public void Mmc1_powers_on_with_the_last_bank_fixed()
    {
        var mapper = new Mmc1(Rom(1, 16, 64));

        mapper.CpuRead(0x8000, 0).Should().Be(0);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
    }

    [Fact]
    public void Mmc1_serial_writes_set_prg_chr_and_mirroring()
    {
        var mapper = new Mmc1(Rom(1, 16, 64));

        Mmc1Write(mapper, 0x8000, 0x1F); // 4 KB CHR, PRG mode 3, horizontal
        Mmc1Write(mapper, 0xE000, 2);
        Mmc1Write(mapper, 0xA000, 5);
        Mmc1Write(mapper, 0xC000, 9);

        mapper.Mirroring.Should().Be(Mirroring.Horizontal);
        mapper.CpuRead(0x8000, 0).Should().Be(4);
        mapper.PpuRead(0x0000).Should().Be(20);
        mapper.PpuRead(0x1000).Should().Be(36);
    }

    [Fact]
    public void Mmc1_ignores_the_second_of_two_consecutive_writes()
    {
        var mapper = new Mmc1(Rom(1, 16, 64));

        // A read-modify-write writes on two cycles in a row; only the first counts.
        for (var bit = 0; bit < 5; bit++)
        {
            mapper.OnCpuCycle();
            mapper.OnCpuCycle();
            mapper.CpuWrite(0xE000, 1);
            mapper.OnCpuCycle();
            mapper.CpuWrite(0xE000, 0);
        }

        mapper.CpuRead(0x8000, 0).Should().Be(62 % 16, "PRG register = %11111 = 31, which wraps to bank 15 of 8");
    }

    [Fact]
    public void Mmc1_reset_bit_restores_prg_mode_3()
    {
        var mapper = new Mmc1(Rom(1, 16, 64));
        Mmc1Write(mapper, 0x8000, 0x00); // 32 KB mode

        mapper.OnCpuCycle();
        mapper.OnCpuCycle();
        mapper.CpuWrite(0x8000, 0x80);

        mapper.CpuRead(0xC000, 0).Should().Be(14);
    }

    [Fact]
    public void Mmc3_banks_prg_and_chr_with_both_inversion_modes()
    {
        var mapper = new Mmc3(Rom(4, 16, 128));
        mapper.CpuWrite(0x8000, 6);
        mapper.CpuWrite(0x8001, 3);
        mapper.CpuWrite(0x8000, 0);
        mapper.CpuWrite(0x8001, 10);
        mapper.CpuWrite(0x8000, 2);
        mapper.CpuWrite(0x8001, 40);

        mapper.CpuRead(0x8000, 0).Should().Be(3);
        mapper.CpuRead(0xC000, 0).Should().Be(14);
        mapper.PpuRead(0x0400).Should().Be(11);
        mapper.PpuRead(0x1000).Should().Be(40);

        mapper.CpuWrite(0x8000, 0xC2); // PRG and CHR inversion

        mapper.CpuRead(0x8000, 0).Should().Be(14);
        mapper.CpuRead(0xC000, 0).Should().Be(3);
        mapper.PpuRead(0x0000).Should().Be(40);
        mapper.PpuRead(0x1400).Should().Be(11);
    }

    [Fact]
    public void Mmc3_irq_fires_after_latch_plus_one_filtered_a12_rises()
    {
        var mapper = new Mmc3(Rom(4, 16, 128));
        mapper.CpuWrite(0xC000, 2);
        mapper.CpuWrite(0xC001, 0);
        mapper.CpuWrite(0xE001, 0);

        void Scanline()
        {
            mapper.OnA12Fall();
            for (var i = 0; i < 100; i++)
            {
                mapper.OnCpuCycle();
            }

            mapper.OnA12Rise();

            // Short lows (between sprite fetches) don't count.
            mapper.OnA12Fall();
            mapper.OnCpuCycle();
            mapper.OnA12Rise();
        }

        Scanline(); // reload to 2
        Scanline(); // 1
        mapper.IrqPending.Should().BeFalse();
        Scanline(); // 0 -> IRQ
        mapper.IrqPending.Should().BeTrue();

        mapper.CpuWrite(0xE000, 0);
        mapper.IrqPending.Should().BeFalse();
    }

    [Fact]
    public void Mmc3_prg_ram_follows_the_protect_register()
    {
        var mapper = new Mmc3(Rom(4, 16, 128));
        mapper.CpuWrite(0x6000, 0x42);
        mapper.CpuRead(0x6000, 0).Should().Be(0x42);

        mapper.CpuWrite(0xA001, 0xC0); // enabled, write-protected
        mapper.CpuWrite(0x6000, 0x99);
        mapper.CpuRead(0x6000, 0).Should().Be(0x42);

        mapper.CpuWrite(0xA001, 0x00); // disabled
        mapper.CpuRead(0x6000, 0x17).Should().Be(0x17);
    }

    public static TheoryData<int, int, int> Boards { get; } = new()
    {
        { 0, 4, 8 },
        { 1, 16, 64 },
        { 2, 16, 0 },
        { 3, 4, 32 },
        { 4, 16, 128 },
        { 7, 16, 0 },
    };

    /// <summary>
    /// SPEC §12 Phase 4: run, save, run on, then load into a fresh console and rerun. Video, RAM and the
    /// mapper state must match exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(Boards))]
    public void Console_state_round_trips_for_every_mapper(int mapper, int prg8K, int chr1K)
    {
        var image = BankedRom.Build(mapper, prg8K, chr1K);
        var original = NesConsole.FromBytes(image);
        original.Power();
        for (var i = 0; i < 20; i++)
        {
            original.RunFrame();
        }

        var writer = new StateWriter();
        original.SaveState(writer);
        var saved = writer.ToArray();

        for (var i = 0; i < 20; i++)
        {
            original.RunFrame();
        }

        var restored = NesConsole.FromBytes(image);
        restored.LoadState(new StateReader(saved));
        for (var i = 0; i < 20; i++)
        {
            restored.RunFrame();
        }

        restored.Ppu.FrameBuffer.ToArray().Should().Equal(original.Ppu.FrameBuffer.ToArray());
        Snapshot(restored).Should().Equal(Snapshot(original));
        if (mapper == 4)
        {
            original.Bus.Peek(0x0001).Should().BeGreaterThan(0, "the MMC3 IRQ handler ran");
        }
    }

    private static byte[] Snapshot(NesConsole console)
    {
        var writer = new StateWriter();
        console.SaveState(writer);
        return writer.ToArray();
    }
}
