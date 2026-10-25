namespace GameVault.Core.Cartridge.Fds;

/// <summary>
/// A Famicom Disk System image (NESdev Wiki: "FDS file format", "FDS disk format"): one or more 65,500-byte
/// sides holding the disk's blocks without gaps or CRCs, optionally after a 16-byte fwNES header
/// ("FDS\x1A", side count). The core receives bytes only; loading files is the caller's job.
/// </summary>
public sealed class FdsImage
{
    public const int SideSize = 65500;
    private const int FwNesHeaderSize = 16;

    private FdsImage(byte[][] sides) => Sides = sides;

    /// <summary>Each side as stored in the image (65,500 bytes).</summary>
    public IReadOnlyList<byte[]> Sides { get; }

    /// <summary>True when the data looks like an FDS image, with or without the fwNES header.</summary>
    public static bool IsFdsImage(ReadOnlySpan<byte> data) =>
        (data.Length >= FwNesHeaderSize && data[0] == 'F' && data[1] == 'D' && data[2] == 'S' && data[3] == 0x1A) ||
        (data.Length >= 15 && data[0] == 0x01 && data.Slice(1, 14).SequenceEqual("*NINTENDO-HVC*"u8));

    public static FdsImage Parse(ReadOnlySpan<byte> data)
    {
        if (!IsFdsImage(data))
        {
            throw new RomFormatException("This is not a Famicom Disk System image.");
        }

        if (data[0] == 'F')
        {
            data = data[FwNesHeaderSize..];
        }

        var count = data.Length / SideSize;
        if (count == 0)
        {
            throw new RomFormatException("The disk image is shorter than one disk side.");
        }

        var sides = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            sides[i] = data.Slice(i * SideSize, SideSize).ToArray();
        }

        return new FdsImage(sides);
    }
}
