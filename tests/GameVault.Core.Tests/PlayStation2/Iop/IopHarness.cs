using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.PlayStation;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>An IOP without a BIOS, with hand-assembled programs put in RAM and run from there.</summary>
internal sealed class IopHarness
{
    public const uint Origin = 0x80001000;

    public IopHarness(IPs2Spu2? spu2 = null)
    {
        Sif = new Ps2Sif();
        Iop = new Ps2Iop(null, Sif, spu2);
    }

    public Ps2Sif Sif { get; }

    public Ps2Iop Iop { get; }

    /// <summary>Puts <paramref name="program"/> at <see cref="Origin"/> followed by an endless loop, and starts the CPU there.</summary>
    public void Load(params uint[] program)
    {
        var address = Origin & 0x1FFFFFFF;
        foreach (var word in program)
        {
            Iop.Write32(address, word);
            address += 4;
        }

        // j . ; nop
        Iop.Write32(address, 0x08000000u | ((address >> 2) & 0x03FFFFFF));
        Iop.Write32(address + 4, Mips.Nop);
        Iop.Cpu.Jump(Origin);
        Iop.Cpu.Sr = 0;
        Iop.Halted = false;
    }

    public void Run(long cycles) => Iop.Run(Iop.Cycles + cycles);

    /// <summary>lui + ori: a 32-bit constant into <paramref name="rt"/>.</summary>
    public static uint[] Li(int rt, uint value) => [Mips.Lui(rt, (int)(value >> 16)), Mips.Ori(rt, rt, (int)(value & 0xFFFF))];

    public static uint Sb(int rt, int rs, int offset) => Mips.I(0x28, rs, rt, offset);

    public static uint Lbu(int rt, int rs, int offset) => Mips.I(0x24, rs, rt, offset);

    public void Write8(uint address, byte value) => Iop.Write8(address, value);

    public byte Read8(uint address) => Iop.Read8(address);
}

/// <summary>A small ISO 9660 image built by the tests: the volume descriptors at 16 and 17, then numbered data sectors.</summary>
internal static class TestIso
{
    public const int Sectors = 64;

    public static byte[] Build()
    {
        var image = new byte[Sectors * 2048];
        var pvd = image.AsSpan(16 * 2048, 2048);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        "PLAYSTATION"u8.CopyTo(pvd[8..]);
        "GAMEVAULT_TEST"u8.CopyTo(pvd[40..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pvd[80..], Sectors);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(pvd[84..], Sectors);
        var terminator = image.AsSpan(17 * 2048, 2048);
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator[1..]);
        terminator[6] = 1;
        for (var sector = 18; sector < Sectors; sector++)
        {
            var data = image.AsSpan(sector * 2048, 2048);
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(sector + i);
            }
        }

        return image;
    }
}
