using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using static GameVault.Core.Tests.PlayStation2.Console.MipsAssembler;

namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>
/// A 4 MB stand-in for the player's BIOS, hand-assembled by the tests (no Sony code). Like the real one, its reset code
/// at $BFC00000 runs on both processors and branches on COP0's PRId (ps2tek: "BIOS Boot Process": the EE when PRId ≥
/// $59, the IOP's $1F otherwise). Then, as real software does it, through the hardware registers:
/// <list type="bullet">
/// <item>The IOP prints to its TTY ($1F80380C), puts <see cref="IopGreeting"/> in SMCOM and sets SMFLG's $10000; the
/// EE polls SMFLG, keeps SMCOM at RDRAM $10000, answers with <see cref="EeGreeting"/> in MSCOM and sets MSFLG; the IOP
/// keeps MSCOM at its RAM's $10000.</item>
/// <item>SIF1: the EE sends a 3-quadword packet (the IOP's tag: $30000, 8 words, then the data) by DMAC channel 6 in
/// chain mode (a REFE tag); the IOP receives it with its channel 10 into RAM at $30000.</item>
/// <item>SIF0: the IOP sends the first four of those words inverted from $50000 by channel 9 in tag mode (its tag with
/// the EE's END tag for $30000 after it); the EE receives them with channel 5 (destination chain) at RDRAM $30000.</item>
/// <item>The IOP reads CD sector <see cref="CdSector"/> into RAM at $60000 with N command 06h (ReadCd) and DMA channel
/// 3, when a disc is in the drive (and <c>readDisc</c>).</item>
/// <item>The EE sets up the GS's display (640×448, PSMCT32 at 0) and draws a <see cref="SpriteColour"/> sprite over the
/// screen through the GIF's FIFO (PATH3), waits for the IOP's SMFLG $40000, prints, maps EELOAD's page in the TLB,
/// copies a little EELOAD (which prints "EELOAD") to $82000 and jumps there — where the fast-boot hook takes over.</item>
/// <item>Its EE exception handler ($BFC00380, BEV set) implements ExecPS2 (syscall 7): EPC = a0, gp = a1, then the
/// program starts with a0 = argc and a1 = argv.</item>
/// </list>
/// </summary>
internal static class StandInBios
{
    public const uint IopGreeting = 0x1234_ABCD;
    public const uint EeGreeting = 0xEE00_C0DE;
    public const int CdSector = 20;
    public const uint SpriteColour = 0x80F8_4020; // A, B, G, R

    public static readonly uint[] Sif1Data = [.. Enumerable.Range(0, 8).Select(i => 0x51F1_0000u + (uint)i)];

    private const uint Rom = 0xBFC0_0000;
    private const uint GifPacket = Rom + 0x8000;
    private const uint Sif1Packet = Rom + 0x8100;
    private const uint EeloadImage = Rom + 0x9000;
    private const int EeloadBytes = 0x100;

    /// <param name="readDisc">The IOP reads the disc (the drive's spin-up takes a third of a second, slow in Debug runs).</param>
    public static byte[] Build(bool readDisc = true)
    {
        var bios = new byte[Ps2Console.BiosSize];
        var code = Code(readDisc).AssembleBytes();
        code.CopyTo(bios, 0);
        WriteGifPacket(bios.AsSpan((int)(GifPacket - Rom)));
        WriteSif1Packet(bios.AsSpan((int)(Sif1Packet - Rom)));
        var eeload = Eeload().AssembleBytes();
        eeload.Length.Should().BeLessThanOrEqualTo(EeloadBytes);
        eeload.CopyTo(bios, (int)(EeloadImage - Rom));
        return bios;
    }

    private static MipsAssembler Code(bool readDisc)
    {
        var a = new MipsAssembler(Rom);

        // ---- Reset: both processors start here ----
        a.Mfc0(T0, 15); // PRId: $2E20 on the EE, $1F on the IOP
        a.Sltiu(T1, T0, 0x59);
        a.Bne(T1, Zero, "iop");
        a.J("ee");

        // ---- The IOP's exception vector (BEV): not used ----
        a.Org(Rom + 0x180);
        a.Halt();

        // ---- The EE's exception vector (BEV): ExecPS2 ----
        a.Org(Rom + 0x380);
        a.Addiu(K0, Zero, 7);
        a.Bne(V1, K0, "ee_unknown");
        a.Mtc0(A0, 14); // EPC = the entry point
        a.Move(Gp, A1);
        a.Move(A0, A2); // argc
        a.Move(A1, A3); // argv
        a.Eret();
        a.Label("ee_unknown");
        a.Halt();

        a.Org(Rom + 0x400);
        EeMain(a);
        IopMain(a, readDisc);
        Routines(a);
        return a;
    }

    private static void EeMain(MipsAssembler a)
    {
        a.Label("ee");
        a.Li(T0, 0x7040_0000); // CU0–2 and BEV; ERL off (so ERET uses EPC)
        a.Mtc0(T0, 12);
        a.La(A0, "ee_hello");
        a.Li(A1, 0xB000_F180); // the EE's debug UART
        a.Jal("puts");

        // The mailbox: wait for the IOP's SMFLG, take SMCOM, answer through MSCOM and MSFLG.
        a.Li(S0, 0xB000_F000);
        a.Lui(T1, 1); // $10000
        a.Label("ee_wait_smflg");
        a.Lw(T0, S0, 0x230);
        a.And(T0, T0, T1);
        a.Beq(T0, Zero, "ee_wait_smflg");
        a.Lw(T2, S0, 0x210);
        a.Li(T3, 0xA001_0000);
        a.Sw(T2, T3, 0);
        a.Sw(T1, S0, 0x230); // the EE clears SMFLG's bit by writing it
        a.Li(T0, EeGreeting);
        a.Sw(T0, S0, 0x200);
        a.Sw(T1, S0, 0x220);

        // The DMAC on; SIF0 (channel 5) waits for the IOP's tags and data.
        a.Li(S1, 0xB000_E000);
        a.Addiu(T0, Zero, 1);
        a.Sw(T0, S1, 0); // D_CTRL.DMAE
        a.Li(S2, 0xB000_C000);
        a.Sw(Zero, S2, 0x20);
        a.Addiu(T0, Zero, 0x104); // destination chain, STR
        a.Sw(T0, S2, 0);

        // SIF1 (channel 6): the packet and its REFE tag into RDRAM at $20000, then a chain transfer from the tag at $20040.
        a.Li(A0, Sif1Packet);
        a.Li(A1, 0xA002_0000);
        a.Addiu(A2, Zero, 0x50);
        a.Jal("copy");
        a.Li(S3, 0xB000_C400);
        a.Sw(Zero, S3, 0x20);
        a.Li(T0, 0x0002_0040);
        a.Sw(T0, S3, 0x30);
        a.Addiu(T0, Zero, 0x105); // from memory, source chain, STR
        a.Sw(T0, S3, 0);
        a.Label("ee_wait_sif1");
        a.Lw(T0, S3, 0);
        a.Andi(T0, T0, 0x100);
        a.Bne(T0, Zero, "ee_wait_sif1");
        a.Label("ee_wait_sif0");
        a.Lw(T0, S2, 0);
        a.Andi(T0, T0, 0x100);
        a.Bne(T0, Zero, "ee_wait_sif0");

        // The GS: circuit 1 shows a 640×448 PSMCT32 buffer at 0 (NTSC, interlaced field mode).
        a.Li(S4, 0xB200_0000);
        a.Li(T0, 0xFF21); // PMODE: EN1, MMOD, ALP $FF
        a.Sw(T0, S4, 0x00);
        a.Sw(Zero, S4, 0x04);
        a.Addiu(T0, Zero, 1); // SMODE2: interlaced, field
        a.Sw(T0, S4, 0x20);
        a.Sw(Zero, S4, 0x24);
        a.Li(T0, 10 << 9); // DISPFB1: FBW 10
        a.Sw(T0, S4, 0x70);
        a.Sw(Zero, S4, 0x74);
        a.Li(T0, 636 | (50 << 12) | (3 << 23)); // DISPLAY1: DX, DY, MAGH
        a.Sw(T0, S4, 0x80);
        a.Li(T0, 2559 | (447 << 12)); // DW, DH
        a.Sw(T0, S4, 0x84);

        // The sprite, quadword by quadword into the GIF's FIFO.
        a.Li(A0, GifPacket);
        a.Li(A1, 0xB000_6000);
        a.Addiu(A2, Zero, 8);
        a.Label("ee_gif");
        a.Lq(T0, A0, 0);
        a.Sq(T0, A1, 0);
        a.Addiu(A0, A0, 16);
        a.Addiu(A2, A2, -1);
        a.Bgtz(A2, "ee_gif");

        // Wait for the IOP's "done" ($40000 in SMFLG).
        a.Lui(T1, 4);
        a.Label("ee_wait_iop");
        a.Lw(T0, S0, 0x230);
        a.And(T0, T0, T1);
        a.Beq(T0, Zero, "ee_wait_iop");
        a.La(A0, "ee_done");
        a.Li(A1, 0xB000_F180);
        a.Jal("puts");

        // EELOAD: its page (virtual $82000–$83FFF) mapped to the same physical pages, its code copied there, and a jump.
        a.Mtc0(Zero, 0); // Index
        a.Mtc0(Zero, 5); // PageMask: 4 KB
        a.Li(T0, 0x0008_2000);
        a.Mtc0(T0, 10); // EntryHi
        a.Li(T0, (0x82u << 6) | 0x17); // EntryLo0: PFN $82, uncached, D, V, G
        a.Mtc0(T0, 2);
        a.Li(T0, (0x83u << 6) | 0x17);
        a.Mtc0(T0, 3);
        a.Tlbwi();
        a.Li(A0, EeloadImage);
        a.Li(A1, 0xA008_2000);
        a.Addiu(A2, Zero, EeloadBytes);
        a.Jal("copy");
        a.Li(T0, Ps2Console.EeLoaderEntry);
        a.Jr(T0);
    }

    private static void IopMain(MipsAssembler a, bool readDisc)
    {
        a.Label("iop");
        a.La(A0, "iop_hello");
        a.Li(A1, 0xBF80_380C); // the IOP's TTY
        a.Jal("puts");

        // The mailbox, the IOP's side.
        a.Li(S0, 0xBD00_0000);
        a.Li(T0, IopGreeting);
        a.Sw(T0, S0, 0x10); // SMCOM
        a.Lui(T1, 1);
        a.Sw(T1, S0, 0x30); // SMFLG |= $10000
        a.Label("iop_wait_msflg");
        a.Lw(T0, S0, 0x20);
        a.And(T0, T0, T1);
        a.Beq(T0, Zero, "iop_wait_msflg");
        a.Lw(T2, S0, 0x00); // MSCOM
        a.Li(T3, 0xA001_0000);
        a.Sw(T2, T3, 0);
        a.Sw(T1, S0, 0x20); // the IOP clears MSFLG's bit by writing it

        // SIF1 in: channel 10 (DPCR2 enables 9 and 10).
        a.Li(S1, 0xBF80_1500);
        a.Lw(T0, S1, 0x70);
        a.Ori(T0, T0, 0x8800);
        a.Sw(T0, S1, 0x70);
        a.Li(T0, 0x0100_0201);
        a.Sw(T0, S1, 0x38);
        a.Lui(T2, 0x0100);
        a.Label("iop_wait_sif1");
        a.Lw(T0, S1, 0x38);
        a.And(T0, T0, T2);
        a.Bne(T0, Zero, "iop_wait_sif1");

        // SIF0 out: four words, inverted, from $50000; the tag at $40000 with the EE's END tag for $30000.
        a.Li(A0, 0xA003_0000);
        a.Li(A1, 0xA005_0000);
        a.Addiu(A2, Zero, 4);
        a.Label("iop_invert");
        a.Lw(T0, A0, 0);
        a.Nor(T0, T0, Zero);
        a.Sw(T0, A1, 0);
        a.Addiu(A0, A0, 4);
        a.Addiu(A1, A1, 4);
        a.Addiu(A2, A2, -1);
        a.Bgtz(A2, "iop_invert");
        a.Li(T3, 0xA004_0000);
        a.Li(T0, 0x8005_0000); // the data's address, end of transfer
        a.Sw(T0, T3, 0);
        a.Addiu(T0, Zero, 4); // words
        a.Sw(T0, T3, 4);
        a.Li(T0, 0x7000_0001); // the EE's tag: END, one quadword
        a.Sw(T0, T3, 8);
        a.Li(T0, 0x0003_0000); // … to RDRAM $30000
        a.Sw(T0, T3, 12);
        a.Sw(Zero, T3, 16);
        a.Sw(Zero, T3, 20);
        a.Li(T0, 0x0004_0000);
        a.Sw(T0, S1, 0x2C); // TADR
        a.Li(T0, 0x0100_0701);
        a.Sw(T0, S1, 0x28);
        a.Label("iop_wait_sif0");
        a.Lw(T0, S1, 0x28);
        a.And(T0, T0, T2);
        a.Bne(T0, Zero, "iop_wait_sif0");

        // The CDVD: with a disc in the drive, ReadCd one sector by DMA channel 3 into $60000.
        a.Li(S2, 0xBF40_2000);
        a.Lbu(T0, S2, 0x0F); // the disc type: 0 = none
        a.Beq(T0, Zero, "iop_no_disc");
        if (!readDisc)
        {
            a.B("iop_no_disc");
        }

        a.Li(S3, 0xBF80_1000);
        a.Lw(T0, S3, 0xF0);
        a.Ori(T0, T0, 0x8000); // DPCR: channel 3 on
        a.Sw(T0, S3, 0xF0);
        a.Li(T0, 0x0006_0000);
        a.Sw(T0, S3, 0xB0);
        a.Li(T0, (1u << 16) | 512);
        a.Sw(T0, S3, 0xB4);
        a.Li(T0, 0x4100_0200);
        a.Sw(T0, S3, 0xB8);
        foreach (var p in (byte[])[CdSector, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0])
        {
            a.Addiu(T0, Zero, p);
            a.Sb(T0, S2, 5);
        }

        a.Addiu(T0, Zero, 0x06);
        a.Sb(T0, S2, 4);
        a.Label("iop_wait_cd");
        a.Lbu(T0, S2, 8);
        a.Andi(T0, T0, 1);
        a.Beq(T0, Zero, "iop_wait_cd");
        a.Addiu(T0, Zero, 1);
        a.Sb(T0, S2, 8);
        a.La(A0, "iop_cd");
        a.Li(A1, 0xBF80_380C);
        a.Jal("puts");
        a.Label("iop_no_disc");

        a.La(A0, "iop_done");
        a.Li(A1, 0xBF80_380C);
        a.Jal("puts");
        a.Lui(T0, 4);
        a.Sw(T0, S0, 0x30); // SMFLG |= $40000: done
        a.Halt();
    }

    private static void Routines(MipsAssembler a)
    {
        // puts(a0 = string, a1 = the output byte)
        a.Label("puts");
        a.Lbu(T0, A0, 0);
        a.Beq(T0, Zero, "puts_end");
        a.Sb(T0, A1, 0);
        a.Addiu(A0, A0, 1);
        a.B("puts");
        a.Label("puts_end");
        a.Jr(Ra);

        // copy(a0 = source, a1 = destination, a2 = bytes, a multiple of 4)
        a.Label("copy");
        a.Lw(T0, A0, 0);
        a.Sw(T0, A1, 0);
        a.Addiu(A0, A0, 4);
        a.Addiu(A1, A1, 4);
        a.Addiu(A2, A2, -4);
        a.Bgtz(A2, "copy");
        a.Jr(Ra);

        a.Label("ee_hello");
        a.Ascii("EE\n");
        a.Label("ee_done");
        a.Ascii("EE done\n");
        a.Label("iop_hello");
        a.Ascii("IOP\n");
        a.Label("iop_cd");
        a.Ascii("IOP read the disc\n");
        a.Label("iop_done");
        a.Ascii("IOP done\n");
    }

    /// <summary>The stand-in's EELOAD: it prints and stops (the real one would load OSDSYS).</summary>
    private static MipsAssembler Eeload()
    {
        var a = new MipsAssembler(Ps2Console.EeLoaderEntry);
        a.La(A0, "text");
        a.Li(A1, 0xB000_F180);
        a.Label("loop");
        a.Lbu(T0, A0, 0);
        a.Beq(T0, Zero, "end");
        a.Sb(T0, A1, 0);
        a.Addiu(A0, A0, 1);
        a.B("loop");
        a.Label("end");
        a.Halt();
        a.Label("text");
        a.Ascii("EELOAD\n");
        return a;
    }

    /// <summary>A GIFtag (PACKED, A+D, EOP) and seven A+D writes: FRAME, ZBUF (masked), SCISSOR, a sprite over the screen.</summary>
    private static void WriteGifPacket(Span<byte> rom)
    {
        static void Qword(Span<byte> rom, int index, ulong low, ulong high)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rom[(index * 16)..], low);
            BinaryPrimitives.WriteUInt64LittleEndian(rom[((index * 16) + 8)..], high);
        }

        Qword(rom, 0, 7 | (1UL << 15) | (1UL << 60), 0xE);
        Qword(rom, 1, 10UL << 16, 0x4C); // FRAME_1: FBP 0, FBW 10, PSMCT32
        Qword(rom, 2, 0x100 | (1UL << 32), 0x4E); // ZBUF_1: ZMSK
        Qword(rom, 3, (639UL << 16) | (447UL << 48), 0x40); // SCISSOR_1
        Qword(rom, 4, 6, 0x00); // PRIM: sprite
        Qword(rom, 5, SpriteColour, 0x01); // RGBAQ
        Qword(rom, 6, 0, 0x05); // XYZ2 (0, 0)
        Qword(rom, 7, (640UL << 4) | ((448UL << 4) << 16), 0x05); // XYZ2 (640, 448)
    }

    /// <summary>The IOP's tag ($30000, 8 words, end), the data, and at +$40 the EE's REFE tag for the 3 quadwords at $20000.</summary>
    private static void WriteSif1Packet(Span<byte> rom)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(rom, 0x8003_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(rom[4..], (uint)Sif1Data.Length);
        for (var i = 0; i < Sif1Data.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rom[(16 + (i * 4))..], Sif1Data[i]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(rom[0x40..], 3 | (0x2_0000UL << 32));
    }
}
