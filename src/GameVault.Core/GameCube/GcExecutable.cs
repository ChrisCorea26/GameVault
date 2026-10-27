using System.Buffers.Binary;
using GameVault.Core.Cartridge;

namespace GameVault.Core.GameCube;

/// <summary>
/// GameCube executables (phase-18.md): DOL files (seven text and eleven data sections, BSS and an entry point, in a
/// 256-byte header) and big-endian PowerPC ELF files (their loadable program headers). Addresses are virtual
/// ($80000000 and up); they're loaded at their physical addresses in main RAM.
/// </summary>
public static class GcExecutable
{
    private const int DolHeaderSize = 0x100;

    public static bool IsElf(ReadOnlySpan<byte> data) =>
        data.Length >= 0x34 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F'
        && data[4] == 1 && data[5] == 2 && BinaryPrimitives.ReadUInt16BigEndian(data[0x12..]) == 20; // ELF32, big-endian, PowerPC

    /// <summary>A DOL: its header's sections fit in the file and its entry point is in the cached RAM mirror.</summary>
    public static bool IsDol(ReadOnlySpan<byte> data)
    {
        if (data.Length < DolHeaderSize || IsElf(data))
        {
            return false;
        }

        var entry = Word(data, 0xE0);
        if (entry is < 0x80000000 or >= 0x81800000)
        {
            return false;
        }

        var any = false;
        for (var i = 0; i < 18; i++)
        {
            var offset = Word(data, i * 4);
            var address = Word(data, 0x48 + i * 4);
            var size = Word(data, 0x90 + i * 4);
            if (size == 0)
            {
                continue;
            }

            if (offset < DolHeaderSize || offset + size > data.Length || address is < 0x80000000 or >= 0x81800000)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>Copies the program into <paramref name="ram"/> and returns its entry point.</summary>
    /// <exception cref="RomFormatException">A section that doesn't fit.</exception>
    public static uint Load(ReadOnlySpan<byte> data, Span<byte> ram)
    {
        return IsElf(data) ? LoadElf(data, ram) : LoadDol(data, ram);
    }

    private static uint Word(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    private static void Copy(ReadOnlySpan<byte> source, Span<byte> ram, uint address)
    {
        var physical = (int)(address & 0x01FFFFFF);
        if (physical + source.Length > ram.Length)
        {
            throw new RomFormatException("A section of that GameCube program doesn't fit in memory.");
        }

        source.CopyTo(ram[physical..]);
    }

    private static uint LoadDol(ReadOnlySpan<byte> data, Span<byte> ram)
    {
        for (var i = 0; i < 18; i++)
        {
            var offset = (int)Word(data, i * 4);
            var address = Word(data, 0x48 + i * 4);
            var size = (int)Word(data, 0x90 + i * 4);
            if (size > 0)
            {
                Copy(data.Slice(offset, size), ram, address);
            }
        }

        var bssAddress = Word(data, 0xD8);
        var bssSize = (int)Word(data, 0xDC);
        var bss = (int)(bssAddress & 0x01FFFFFF);
        if (bssSize > 0 && bss + bssSize <= ram.Length)
        {
            ram.Slice(bss, bssSize).Clear();
        }

        return Word(data, 0xE0);
    }

    private static uint LoadElf(ReadOnlySpan<byte> data, Span<byte> ram)
    {
        var programHeaders = (int)Word(data, 0x1C);
        var entrySize = BinaryPrimitives.ReadUInt16BigEndian(data[0x2A..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(data[0x2C..]);
        for (var i = 0; i < count; i++)
        {
            var header = programHeaders + i * entrySize;
            if (header + 0x20 > data.Length || Word(data, header) != 1)
            {
                continue; // only PT_LOAD
            }

            var offset = (int)Word(data, header + 4);
            var address = Word(data, header + 8);
            var fileSize = (int)Word(data, header + 0x10);
            var memorySize = (int)Word(data, header + 0x14);
            if (offset + fileSize > data.Length)
            {
                throw new RomFormatException("That GameCube ELF file is cut short.");
            }

            Copy(data.Slice(offset, fileSize), ram, address);
            var physical = (int)(address & 0x01FFFFFF);
            if (memorySize > fileSize && physical + memorySize <= ram.Length)
            {
                ram.Slice(physical + fileSize, memorySize - fileSize).Clear();
            }
        }

        return Word(data, 0x18);
    }
}
