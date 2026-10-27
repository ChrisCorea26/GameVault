using GameVault.Core.Snes;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Snes;

/// <summary>
/// The Super FX (outstanding-work plan §4) on hand-assembled GSU programs: arithmetic and flags, the pipeline's delay
/// slot, LOOP, the multipliers, RAM and the ROM buffer, PLOT and RPIX into SNES characters, the bus ownership the SNES
/// sees, a save state mid-program, and a whole console starting the GSU and waiting for it.
/// </summary>
public sealed class SuperFxTests
{
    private const int CodeAt = 0x2000; // ROM offset of the GSU program: $00:A000 for both the SNES and the GSU
    private const ushort CodeAddress = 0xA000;

    // ---- A few GSU encodings ----

    private static byte[] Iwt(int r, int value) => [(byte)(0xF0 | r), (byte)value, (byte)(value >> 8)];

    private static byte[] Ibt(int r, int value) => [(byte)(0xA0 | r), (byte)value];

    private static byte To(int r) => (byte)(0x10 | r);

    private static byte From(int r) => (byte)(0xB0 | r);

    private static byte With(int r) => (byte)(0x20 | r);

    private static byte Add(int r) => (byte)(0x50 | r);

    private static byte Sub(int r) => (byte)(0x60 | r);

    private static byte Inc(int r) => (byte)(0xD0 | r);

    private static byte Mult(int r) => (byte)(0x80 | r);

    private const byte Stop = 0x00, Nop = 0x01, Cache = 0x02, Loop = 0x3C, Alt1 = 0x3D, Alt2 = 0x3E, Alt3 = 0x3F;
    private const byte Plot = 0x4C, Color = 0x4E, Getb = 0xEF, Fmult = 0x9F, Bra = 0x05, Beq = 0x09, Bne = 0x08;

    private static byte[] Image(params byte[][] code)
    {
        var rom = new byte[0x10000];
        code.SelectMany(c => c).ToArray().CopyTo(rom, CodeAt);
        return rom;
    }

    private static SuperFx Gsu(byte[] rom, Action<SuperFx>? setup = null)
    {
        var gsu = new SuperFx(rom, 0x10000, 1);
        gsu.Power();
        setup?.Invoke(gsu);
        // As a game does: the program bank, then R15 (its high byte starts the GSU).
        gsu.Write(0x003034, 0x00);
        gsu.Write(0x00301E, CodeAddress & 0xFF);
        gsu.Write(0x00301F, CodeAddress >> 8);
        return gsu;
    }

    private static SuperFx Run(params byte[][] code)
    {
        var gsu = Gsu(Image(code));
        gsu.RunUntilStopped();
        gsu.Running.Should().BeFalse("the program ends with STOP");
        return gsu;
    }

    private static ushort R(SuperFx gsu, int n) => (ushort)(gsu.Read((uint)(0x3000 + n * 2), 0) | gsu.Read((uint)(0x3001 + n * 2), 0) << 8);

    [Fact]
    public void Add_with_with_and_stop_raises_the_interrupt()
    {
        var gsu = Run(Iwt(1, 0x1234), Iwt(2, 0x0F0F), [With(1), Add(2), Stop, Nop]);

        R(gsu, 1).Should().Be(0x2143);
        gsu.Irq.Should().BeTrue();
        (gsu.Read(0x3030, 0) & 0x20).Should().Be(0, "GO is clear once stopped");
        (gsu.Read(0x3031, 0) & 0x80).Should().Be(0x80, "IRQ in SFR's high byte");
        gsu.Irq.Should().BeFalse("reading it acknowledges the interrupt");
    }

    [Fact]
    public void Sub_sets_carry_zero_and_sign_and_cmp_leaves_the_register()
    {
        var gsu = Run(Iwt(0, 5), Iwt(3, 5), [Sub(3)], Iwt(4, 1), [Alt3, Sub(4), Stop, Nop]);

        R(gsu, 0).Should().Be(0, "SUB R3 gave 0; CMP R4 didn't write");
        var sfr = gsu.Sfr;
        (sfr & 0x0008).Should().NotBe(0, "CMP 0 - 1 is negative");
        (sfr & 0x0004).Should().Be(0, "and borrows");
    }

    [Fact]
    public void A_branch_runs_the_byte_after_it_before_the_target()
    {
        // BRA +3 skips two INCs, but the INC in the delay slot runs.
        var gsu = Run(Ibt(3, 0), [Bra, 3, Inc(3), Inc(3), Inc(3), Stop, Nop]);

        R(gsu, 3).Should().Be(1);
    }

    [Fact]
    public void Loop_counts_r12_down_to_r13()
    {
        const ushort body = CodeAddress + 5; // after IBT R12 (2 bytes) and IWT R13 (3)
        var gsu = Run(Ibt(12, 5), Iwt(13, body), [Inc(4), Loop, Nop, Stop, Nop]);

        R(gsu, 4).Should().Be(5);
        R(gsu, 12).Should().Be(0);
    }

    [Fact]
    public void Mult_umult_and_fmult()
    {
        var gsu = Run(
            Ibt(0, -3), Ibt(5, 7), [Mult(5)],               // R0 = -3 × 7
            Ibt(1, -3), [From(1), To(2), Alt1, Mult(5)],      // R2 = $FD × 7 (unsigned)
            Iwt(6, 0x4000), Iwt(3, 0x2000), [From(3), To(7), Fmult, Stop, Nop]); // R7 = high word of $2000 × $4000

        R(gsu, 0).Should().Be(unchecked((ushort)-21));
        R(gsu, 2).Should().Be(0xFD * 7);
        R(gsu, 7).Should().Be((ushort)((0x2000 * 0x4000) >> 16));
    }

    [Fact]
    public void Stores_loads_and_the_rom_buffer()
    {
        var rom = Image(
            Iwt(0, 0xBEEF), Iwt(1, 0x0100), [0x31],          // STW (R1)
            [To(2), Alt1, 0x41],                              // LDB (R1) → R2
            Iwt(5, 0x1234), [Alt2, 0xF5, 0x00, 0x02],         // SM ($0200), R5
            [Alt1, 0xF6, 0x00, 0x02],                         // LM R6, ($0200)
            Iwt(14, 0x8010), [Nop, To(3), Getb, Stop, Nop]);  // R14 → ROM buffer; GETB → R3
        rom[0x10] = 0x5A;
        var gsu = Gsu(rom);
        gsu.RunUntilStopped();

        gsu.Ram[0x100].Should().Be(0xEF);
        gsu.Ram[0x101].Should().Be(0xBE);
        R(gsu, 2).Should().Be(0xEF);
        R(gsu, 6).Should().Be(0x1234);
        R(gsu, 3).Should().Be(0x5A);
    }

    [Fact]
    public void Plot_writes_characters_and_rpix_reads_them_back()
    {
        // 4 bpp, 128 lines, screen at 0: colour 5 at (0, 0) and colour 10 at (9, 1).
        var gsu = Gsu(Image(
            Ibt(0, 5), [Color], Ibt(1, 0), Ibt(2, 0), [Plot],
            Ibt(0, 10), [Color], Ibt(1, 9), Ibt(2, 1), [Plot],
            Ibt(1, 0), Ibt(2, 0), [To(3), Alt1, Plot], // RPIX (0, 0) → R3, flushing the pixel caches
            [Stop, Nop]),
            g => g.Write(0x303A, 0x01)); // SCMR: 4 bpp
        gsu.RunUntilStopped();

        // Character 0, row 0: planes 0–1 at +0/+1, 2–3 at +16/+17; pixel 0 is bit 7. Colour 5 = planes 0 and 2.
        gsu.Ram[0].Should().Be(0x80);
        gsu.Ram[1].Should().Be(0x00);
        gsu.Ram[16].Should().Be(0x80);
        gsu.Ram[17].Should().Be(0x00);
        // (9, 1): character 16 (x / 8 × 16 for 128 lines), row 1, pixel 1 (bit 6); colour 10 = planes 1 and 3.
        var row = 16 * 32 + 2;
        gsu.Ram[row + 1].Should().Be(0x40);
        gsu.Ram[row + 17].Should().Be(0x40);
        gsu.Ram[row].Should().Be(0x00);
        R(gsu, 3).Should().Be(5);
    }

    [Fact]
    public void While_it_owns_the_rom_and_ram_the_snes_sees_vectors_and_open_bus()
    {
        var rom = Image([Bra, 0xFE, Nop]); // spins
        rom[0x7FEA] = 0x34; // a vector the SNES would read when the GSU doesn't own the ROM
        var gsu = Gsu(rom, g => g.Write(0x303A, 0x18)); // SCMR: RON and RAN
        gsu.Ram[0x10] = 0x77;
        gsu.CatchUp(1000);

        gsu.Running.Should().BeTrue();
        gsu.Read(0x00FFEA, 0x11).Should().Be(0x08, "the NMI vector points into WRAM");
        gsu.Read(0x700010, 0x11).Should().Be(0x11, "open bus");
        gsu.Write(0x3030, 0x00); // the SNES stops it
        gsu.Running.Should().BeFalse();
        gsu.Read(0x00FFEA, 0x11).Should().Be(0x34);
        gsu.Read(0x700010, 0x11).Should().Be(0x77);
    }

    [Fact]
    public void The_cache_runs_code_faster_than_rom()
    {
        long Cycles(bool cached)
        {
            var start = cached ? 1 : 0; // CACHE moves everything on by a byte
            byte[] loop = [.. Ibt(12, 100), .. Iwt(13, CodeAddress + start + 5), Inc(4), Loop, Nop, Stop, Nop];
            byte[] code = cached ? [Cache, .. loop] : loop;
            var gsu = Gsu(Image(code));
            gsu.RunUntilStopped();
            R(gsu, 4).Should().Be(100);
            return gsu.Clock;
        }

        Cycles(cached: true).Should().BeLessThan(Cycles(cached: false) / 2);
    }

    [Fact]
    public void A_save_state_resumes_exactly()
    {
        var rom = Image(Ibt(12, 50), Iwt(13, CodeAddress + 5), [Inc(4), With(4), Add(4), Loop, Nop, Stop, Nop]);
        var first = Gsu(rom);
        first.CatchUp(300);
        var writer = new StateWriter();
        first.SaveState(writer);
        first.RunUntilStopped();

        var second = new SuperFx(rom, 0x10000, 1);
        second.LoadState(new StateReader(writer.ToArray()));
        second.RunUntilStopped();

        var a = new StateWriter();
        first.SaveState(a);
        var b = new StateWriter();
        second.SaveState(b);
        b.ToArray().Should().Equal(a.ToArray());
        R(second, 4).Should().NotBe(0);
    }

    [Fact]
    public void A_super_fx_cartridge_runs_the_gsu_for_the_cpu()
    {
        // The 65C816 (8-bit A): PBR = 0, R15 = $A000 (GO), wait for GO to clear, copy R1's low byte to $10.
        byte[] cpu =
        [
            0x18, 0xFB,                   // CLC; XCE
            0xA9, 0x00, 0x8D, 0x34, 0x30, // LDA #0; STA $3034
            0xA9, 0x00, 0x8D, 0x1E, 0x30, // STA $301E
            0xA9, 0xA0, 0x8D, 0x1F, 0x30, // LDA #$A0; STA $301F
            0xAD, 0x30, 0x30,             // wait: LDA $3030
            0x29, 0x20,                   // AND #$20
            0xD0, 0xF9,                   // BNE wait
            0xAD, 0x02, 0x30,             // LDA $3002
            0x85, 0x10,                   // STA $10
            0x80, 0xFE,                   // BRA *
        ];
        var rom = SnesTests.Rom(cpu, chip: 0x13);
        byte[] gsu = [.. Iwt(1, 0x0021), With(1), Add(1), Stop, Nop];
        gsu.CopyTo(rom, CodeAt);
        var cartridge = new SnesCartridge(rom);
        cartridge.Coprocessor.Should().BeOfType<SuperFx>();
        var console = new SnesConsole(cartridge);
        console.Power();

        console.RunFrame();

        console.Wram[0x10].Should().Be(0x42);
        var state = console.SaveSnapshot();
        console.LoadSnapshot(state);
    }
}
