using GameVault.Core.PlayStation;

namespace GameVault.Core.PlayStation2;

/// <summary>The kind of disc in the drive, as the CDVD reports it.</summary>
public enum Ps2DiscType
{
    /// <summary>A PlayStation 2 CD (a blue-bottom disc): 2,352-byte sectors, read through <see cref="IPs2Disc.Cd"/>.</summary>
    Cd,

    /// <summary>A single-layer DVD (DVD-5).</summary>
    Dvd,

    /// <summary>A dual-layer DVD (DVD-9); <see cref="IPs2Disc.Layer1Start"/> says where the second layer begins.</summary>
    DvdDualLayer,
}

/// <summary>
/// A disc the PlayStation 2's drive reads (SPEC §7.11, Phase 17). The platform implements it over the image files
/// (.iso, .cue/.bin, .chd), so a 4.7 GB disc never sits in memory and the core does no file I/O. Reads are pure: the
/// same sector always gives the same bytes, which keeps emulation deterministic.
/// </summary>
public interface IPs2Disc
{
    public const int SectorSize = 2048;

    Ps2DiscType Type { get; }

    /// <summary>The number of 2,048-byte sectors.</summary>
    long SectorCount { get; }

    /// <summary>The first sector of a dual-layer DVD's second layer (0 otherwise).</summary>
    long Layer1Start { get; }

    /// <summary>
    /// Reads the 2,048 bytes of user data of sector <paramref name="lba"/> (LBA 0 is the first sector of the data
    /// track: the ISO 9660 volume starts at sector 16). Sectors past the end read as zeros.
    /// </summary>
    void ReadSector(long lba, Span<byte> destination);

    /// <summary>The raw CD image of a CD disc, for raw and audio reads; null for DVDs.</summary>
    ICdImage? Cd { get; }
}

/// <summary>A disc held in memory as 2,048-byte sectors (tests and small homebrew discs).</summary>
public sealed class MemoryPs2Disc(byte[] data, Ps2DiscType type = Ps2DiscType.Dvd) : IPs2Disc
{
    public Ps2DiscType Type => type;

    public long SectorCount => data.Length / IPs2Disc.SectorSize;

    public long Layer1Start => 0;

    public ICdImage? Cd => null;

    public void ReadSector(long lba, Span<byte> destination)
    {
        var offset = lba * IPs2Disc.SectorSize;
        if (lba < 0 || offset + IPs2Disc.SectorSize > data.Length)
        {
            destination[..IPs2Disc.SectorSize].Clear();
            return;
        }

        data.AsSpan((int)offset, IPs2Disc.SectorSize).CopyTo(destination);
    }
}
