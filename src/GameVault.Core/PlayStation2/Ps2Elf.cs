using System.Buffers.Binary;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// A PlayStation 2 executable: a 32-bit little-endian MIPS ELF (the EE's programs, ps2sdk's and the BIOS's alike). Its
/// <c>PT_LOAD</c> segments are copied into RDRAM at their physical address (<c>p_paddr</c>, or <c>p_vaddr</c> with the
/// segment bits cleared when that is 0) and the rest of each segment's memory size, the BSS, is cleared.
/// </summary>
public sealed class Ps2Elf
{
    private const uint PtLoad = 1;
    private const ushort EmMips = 8;

    private readonly byte[] _data;
    private readonly Segment[] _segments;

    private readonly record struct Segment(uint FileOffset, uint FileSize, uint MemorySize, uint Physical, uint Virtual);

    private Ps2Elf(byte[] data, uint entry, Segment[] segments)
    {
        _data = data;
        Entry = entry;
        _segments = segments;
    }

    /// <summary>Where execution starts (a virtual address, usually in KUSEG or KSEG0).</summary>
    public uint Entry { get; }

    /// <summary>The program's bytes, as given.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>The lowest and highest physical addresses the segments occupy (the end is exclusive).</summary>
    public (uint Start, uint End) PhysicalRange
    {
        get
        {
            uint start = uint.MaxValue, end = 0;
            foreach (var s in _segments)
            {
                start = Math.Min(start, s.Physical);
                end = Math.Max(end, s.Physical + s.MemorySize);
            }

            return _segments.Length == 0 ? (0, 0) : (start, end);
        }
    }

    public static bool IsElf(ReadOnlySpan<byte> data) =>
        data.Length >= 52 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';

    /// <exception cref="InvalidDataException">The data isn't a 32-bit little-endian MIPS executable.</exception>
    public static Ps2Elf Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsElf(data))
        {
            throw new InvalidDataException("Not an ELF file.");
        }

        if (data[4] != 1 || data[5] != 1)
        {
            throw new InvalidDataException("Not a 32-bit little-endian ELF.");
        }

        var span = data.AsSpan();
        if (BinaryPrimitives.ReadUInt16LittleEndian(span[18..]) != EmMips)
        {
            throw new InvalidDataException("Not a MIPS executable.");
        }

        var entry = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        var phOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
        var phSize = BinaryPrimitives.ReadUInt16LittleEndian(span[42..]);
        var phCount = BinaryPrimitives.ReadUInt16LittleEndian(span[44..]);
        if (phSize < 32 || (ulong)phOffset + (ulong)phSize * phCount > (ulong)data.Length)
        {
            throw new InvalidDataException("The ELF's program headers lie outside the file.");
        }

        var segments = new List<Segment>();
        for (var i = 0; i < phCount; i++)
        {
            var ph = span[(int)(phOffset + (uint)(i * phSize))..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) != PtLoad)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadUInt32LittleEndian(ph[4..]);
            var vaddr = BinaryPrimitives.ReadUInt32LittleEndian(ph[8..]);
            var paddr = BinaryPrimitives.ReadUInt32LittleEndian(ph[12..]);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(ph[16..]);
            var memorySize = BinaryPrimitives.ReadUInt32LittleEndian(ph[20..]);
            if ((ulong)offset + fileSize > (ulong)data.Length)
            {
                throw new InvalidDataException("An ELF segment lies outside the file.");
            }

            var physical = (paddr != 0 ? paddr : vaddr) & 0x1FFF_FFFF;
            segments.Add(new Segment(offset, fileSize, Math.Max(memorySize, fileSize), physical, vaddr));
        }

        return new Ps2Elf(data, entry, [.. segments]);
    }

    /// <summary>
    /// Copies the segments into <paramref name="rdram"/> and clears their BSS. Parts outside RDRAM (there are none in a
    /// well-formed program) are skipped.
    /// </summary>
    public void Load(Span<byte> rdram)
    {
        foreach (var s in _segments)
        {
            for (uint i = 0; i < s.MemorySize; i++)
            {
                var address = s.Physical + i;
                if (address >= (uint)rdram.Length)
                {
                    break;
                }

                rdram[(int)address] = i < s.FileSize ? _data[s.FileOffset + i] : (byte)0;
            }
        }
    }
}
