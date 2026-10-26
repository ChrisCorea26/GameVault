using GameVault.Core.PlayStation;

namespace GameVault.Library.Discs;

/// <summary>
/// A run of consecutive disc addresses stored the same way: <see cref="SectorSize"/> bytes per sector starting at
/// <see cref="Source"/> (a byte offset in a file, or a frame number in a CHD), or nothing (a gap that reads as
/// zeros) when <see cref="Stored"/> is false.
/// </summary>
internal readonly record struct DiscExtent(
    int DiscStart,
    int Length,
    bool Stored,
    int SourceIndex,
    long Source,
    int SectorSize,
    CdTrackType Type,
    bool SwapAudio);

/// <summary>
/// A disc image described as extents of disc addresses (pregaps, track data, gaps) over some storage. Builds the
/// raw 2,352-byte sector for each read, synthesizing headers for images that store only user data.
/// </summary>
public abstract class ExtentCdImage : ICdImage, IDisposable
{
    private readonly object _gate = new();
    private DiscExtent[] _extents = [];
    private byte[] _data = new byte[CdSector.RawSize];

    public IReadOnlyList<CdTrack> Tracks { get; private set; } = [];

    public int LeadOut { get; private set; }

    private protected void SetLayout(IReadOnlyList<CdTrack> tracks, List<DiscExtent> extents, int leadOut)
    {
        Tracks = tracks;
        _extents = [.. extents];
        LeadOut = leadOut;
    }

    public void ReadSector(int lba, Span<byte> destination)
    {
        var sector = destination[..CdSector.RawSize];
        var extent = Find(lba);
        if (extent is not { Stored: true } e)
        {
            sector.Clear();
            return;
        }

        lock (_gate)
        {
            if (_data.Length < e.SectorSize)
            {
                _data = new byte[e.SectorSize];
            }

            var data = _data.AsSpan(0, e.SectorSize);
            ReadStored(e, lba - e.DiscStart, data);
            if (e.Type == CdTrackType.Audio)
            {
                data.CopyTo(sector);
                if (e.SwapAudio)
                {
                    for (var i = 0; i + 1 < CdSector.RawSize; i += 2)
                    {
                        (sector[i], sector[i + 1]) = (sector[i + 1], sector[i]);
                    }
                }
            }
            else
            {
                CdSector.Build(data, lba, e.Type == CdTrackType.Mode2, sector);
            }
        }
    }

    /// <summary>Reads the stored bytes of sector <paramref name="index"/> of <paramref name="extent"/>.</summary>
    private protected abstract void ReadStored(DiscExtent extent, int index, Span<byte> data);

    private DiscExtent? Find(int lba)
    {
        int lo = 0, hi = _extents.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var e = _extents[mid];
            if (lba < e.DiscStart)
            {
                hi = mid - 1;
            }
            else if (lba >= e.DiscStart + e.Length)
            {
                lo = mid + 1;
            }
            else
            {
                return e;
            }
        }

        return null;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
