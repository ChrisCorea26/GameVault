using System.Text;
using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Fds;

namespace GameVault.Core.Tests.Fds;

/// <summary>Tiny Disk System images and a stub BIOS for tests (also compiled into the Platform tests).</summary>
internal static class FdsTestImages
{
    private static readonly byte[] FileData = [0xDE, 0xAD, 0xBE, 0xEF];

    /// <summary>One side: disk info, file amount (1), a file header and 4 bytes of file data.</summary>
    internal static byte[] Side(byte sideNumber = 0)
    {
        var side = new byte[FdsImage.SideSize];
        var block1 = new byte[56];
        block1[0] = 0x01;
        Encoding.ASCII.GetBytes("*NINTENDO-HVC*").CopyTo(block1, 1);
        block1[0x15] = sideNumber;
        block1.CopyTo(side, 0);

        side[56] = 0x02;
        side[57] = 1;

        var block3 = new byte[16];
        block3[0] = 0x03;
        Encoding.ASCII.GetBytes("TESTFILE").CopyTo(block3, 3);
        block3[11] = 0x00; // load address $6000
        block3[12] = 0x60;
        block3[13] = (byte)FileData.Length;
        block3.CopyTo(side, 58);

        side[74] = 0x04;
        FileData.CopyTo(side, 75);
        return side;
    }

    internal static byte[] Image(int sides, bool header)
    {
        var data = new List<byte>();
        if (header)
        {
            data.AddRange("FDS\u001A"u8.ToArray());
            data.Add((byte)sides);
            data.AddRange(new byte[11]);
        }

        for (var i = 0; i < sides; i++)
        {
            data.AddRange(Side((byte)i));
        }

        return [.. data];
    }

    /// <summary>
    /// A stub BIOS: enable disk I/O, start the motor in read mode with transfers on, then poll $4030 bit 7 and
    /// copy 64 bytes from $4031 to $0200.
    /// </summary>
    internal static byte[] StubBios()
    {
        var bios = new byte[0x2000];
        byte[] program =
        [
            0x78,                   // E000 SEI
            0xA9, 0x83,             //      LDA #$83
            0x8D, 0x23, 0x40,       //      STA $4023
            0xA9, 0x65,             //      LDA #$65 (motor on, read, horizontal, transfers on)
            0x8D, 0x25, 0x40,       //      STA $4025
            0xA2, 0x00,             //      LDX #0
            0xAD, 0x30, 0x40,       // E00D LDA $4030
            0x29, 0x80,             //      AND #$80
            0xF0, 0xF9,             //      BEQ $E00D
            0xAD, 0x31, 0x40,       //      LDA $4031
            0x9D, 0x00, 0x02,       //      STA $0200,X
            0xE8,                   //      INX
            0xE0, 0x40,             //      CPX #$40
            0xD0, 0xEE,             //      BNE $E00D
            0x4C, 0x1F, 0xE0,       // E01F JMP $E01F
            0x40,                   // E022 RTI
        ];
        program.CopyTo(bios, 0);
        bios[0x1FFA] = 0x22; // NMI -> RTI
        bios[0x1FFB] = 0xE0;
        bios[0x1FFC] = 0x00; // reset
        bios[0x1FFD] = 0xE0;
        bios[0x1FFE] = 0x22; // IRQ -> RTI
        bios[0x1FFF] = 0xE0;
        return bios;
    }
}
