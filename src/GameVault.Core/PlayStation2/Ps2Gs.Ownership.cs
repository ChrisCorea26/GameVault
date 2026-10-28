namespace GameVault.Core.PlayStation2;

/// <summary>
/// Pages an authoritative backend drew in the GS's place (docs/plans/ps2-full-speed.md §3), and the page versions a
/// backend uses to tell which pages of local memory changed since it last saw them.
/// </summary>
/// <remarks>
/// <para>
/// When the backend is <see cref="IPs2GsBackend.Authoritative"/>, <see cref="Flush"/> hands it the batch and doesn't
/// draw it: the pages the batch drew to become the backend's, and local memory's copy of them is stale. Everything that
/// reads local memory already draws the batch first (see Ps2Gs.Batch.cs); each also reads back the pages it reads or
/// partly writes, so it sees what the backend drew: transfers both ways, local copies, CLUT loads, the displayed picture,
/// save states, <see cref="LocalMemory"/> and the pixel accessors.
/// </para>
/// <para>
/// A page's version changes whenever local memory's copy of it does (drawing, uploads, copies, a read back, a reset or a
/// state load), so a backend can tell a page it still has from one that changed without hashing it.
/// </para>
/// </remarks>
public sealed partial class Ps2Gs
{
    private readonly ulong[] _gpuOwned = new ulong[Pages / 64];
    private bool _gpuOwnedAny;
    private readonly uint[] _pageVersions = new uint[Pages];
    private readonly ulong[] _downloadPages = new ulong[Pages / 64];
    private readonly ulong[] _readBack = new ulong[Pages / 64];
    private readonly ulong[] _scratchRect = new ulong[Pages / 64];
    private readonly ulong[] _scratchOwned = new ulong[Pages / 64];
    private IPs2GsBackend? _backend;

    /// <summary>
    /// Draws every batch again at its own resolution (the GPU renderer), or in the GS's place when it is
    /// <see cref="IPs2GsBackend.Authoritative"/>; null for none. Replacing a backend reads back the pages it held first.
    /// </summary>
    internal IPs2GsBackend? Backend
    {
        get => _backend;
        set
        {
            if (ReferenceEquals(value, _backend))
            {
                return;
            }

            EnsureAllNative();
            _backend = value;
        }
    }

    /// <summary>Local memory as it stands, without drawing the batch or reading pages back (for <see cref="Backend"/>).</summary>
    internal byte[] Memory => _memory;

    /// <summary>Changes whenever local memory's copy of <paramref name="page"/> does.</summary>
    internal uint PageVersion(int page) => _pageVersions[page];

    /// <summary>Whether an authoritative backend holds <paramref name="page"/> (tests).</summary>
    internal bool IsGpuOwned(int page) => (_gpuOwned[page >> 6] & (1UL << (page & 63))) != 0;

    /// <summary>The batch was handed to an authoritative backend: the pages it drew to are the backend's now.</summary>
    private void TakeOwnership(ulong[] writes)
    {
        Or(_gpuOwned, writes);
        _gpuOwnedAny = true;
    }

    /// <summary>Reads back the pages of <paramref name="pages"/> that an authoritative backend holds.</summary>
    private void EnsureNative(ulong[] pages)
    {
        if (!_gpuOwnedAny)
        {
            return;
        }

        var any = false;
        var left = false;
        for (var i = 0; i < _readBack.Length; i++)
        {
            _readBack[i] = _gpuOwned[i] & pages[i];
            any |= _readBack[i] != 0;
            _gpuOwned[i] &= ~_readBack[i];
            left |= _gpuOwned[i] != 0;
        }

        _gpuOwnedAny = left;
        if (any)
        {
            BumpPages(_readBack); // first: the backend records the versions the pages have once read back
            _backend!.ReadBack(_readBack);
        }
    }

    /// <summary>Reads back every page an authoritative backend holds.</summary>
    private void EnsureAllNative()
    {
        if (_gpuOwnedAny)
        {
            _gpuOwned.CopyTo(_scratchOwned, 0);
            EnsureNative(_scratchOwned);
        }
    }

    /// <summary>Reads back the pages of a rectangle of a buffer (a transfer's or a copy's).</summary>
    private void EnsureNativeRect(int bp, int bw, int psm, int x, int y, int width, int height)
    {
        if (!_gpuOwnedAny)
        {
            return;
        }

        MarkRect(_scratchRect, bp, bw, psm, x, y, width, height);
        EnsureNative(_scratchRect);
    }

    /// <summary>Reads back the pages a display circuit shows (whole page rows, as the GPU renderer reads them).</summary>
    private void EnsureNativeCircuit(in Circuit c, bool fieldMode)
    {
        if (!_gpuOwnedAny || !c.Enabled)
        {
            return;
        }

        var rows = fieldMode ? (c.Height + 1) / 2 : c.Height;
        MarkRect(_scratchRect, c.Fbp, c.Fbw, c.Psm, 0, c.Dby, Math.Max(c.Fbw, 1) * 64, Math.Max(rows, 1));
        EnsureNative(_scratchRect);
    }

    /// <summary>The pages of a rectangle of a buffer; anywhere when its coordinates wrap.</summary>
    private static void MarkRect(ulong[] pages, int bp, int bw, int psm, int x, int y, int width, int height)
    {
        Array.Clear(pages);
        var x1 = x + width - 1;
        var y1 = y + height - 1;
        if (x1 >= 2048 || y1 >= 2048)
        {
            Array.Fill(pages, ulong.MaxValue);
            return;
        }

        var (first, count) = BufferPages(bp, bw, psm, x1, y, y1);
        Mark(pages, first, count);
    }

    /// <summary>Local memory's copy of these pages changed.</summary>
    private void BumpPages(ulong[] pages)
    {
        for (var word = 0; word < pages.Length; word++)
        {
            var bits = pages[word];
            while (bits != 0)
            {
                _pageVersions[(word * 64) + System.Numerics.BitOperations.TrailingZeroCount(bits)]++;
                bits &= bits - 1;
            }
        }
    }

    /// <summary>Local memory changed anywhere (a reset, a state load, or a writer outside the GS).</summary>
    private void BumpAll()
    {
        for (var page = 0; page < _pageVersions.Length; page++)
        {
            _pageVersions[page]++;
        }
    }

    /// <summary>Local memory was replaced: the backend holds nothing any more.</summary>
    private void ForgetOwnership()
    {
        Array.Clear(_gpuOwned);
        _gpuOwnedAny = false;
        BumpAll();
    }
}
