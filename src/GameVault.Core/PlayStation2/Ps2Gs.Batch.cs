namespace GameVault.Core.PlayStation2;

/// <summary>
/// Drawing in batches: a completed primitive is queued with the draw state it was kicked with, and the queue is drawn
/// when something needs local memory as the drawing left it (a transfer, a CLUT load, the displayed picture, a save
/// state, a reader of <see cref="LocalMemory"/>) or when it is full. Drawing a batch can be shared among several
/// renderers (<see cref="ParallelWork"/>), each taking every Nth strip of 16 rows, so a pixel sees the same
/// primitives in the same order as a lone renderer would: the result is the same, byte for byte, whatever the cores.
/// </summary>
/// <remarks>
/// <para>
/// That holds when no two rows of the batch share memory. The GS keeps the pages (8 KB) each queued state draws to and
/// reads textures from, and before queuing a primitive it draws the batch when the new state would read pages the batch
/// draws to (render to texture), draw over a texture the batch reads, or draw the batch's pages through a different
/// layout (other base, width or format). A
/// state whose own rows can share memory (a texture or Z buffer over its frame buffer, or a frame narrower than the
/// scissor) is drawn by one renderer alone, in order.
/// </para>
/// <para>
/// Events (SIGNAL, FINISH, LABEL) and register reads don't depend on drawing, so queuing changes nothing the EE can see
/// except through local memory, and every way to read local memory draws the batch first.
/// </para>
/// </remarks>
public sealed partial class Ps2Gs
{
    private const int BatchCapacity = 4096;
    private const int MaxBands = 8;
    private const int Pages = LocalMemorySize / 8192;

    /// <summary>A batch with fewer pixels than this is drawn by one renderer (sharing it out would cost more).</summary>
    private const long ParallelPixels = 8192;

    private Command[] _commands = new Command[256];
    private int _count;
    private DrawState[] _states = new DrawState[16];
    private int _stateCount;
    private int _stateIndex = -1;
    private bool _stateSerial;
    private readonly List<uint[]> _palettes = [];
    private int _paletteCount;

    // The pages the batch draws to and reads textures from, and the layouts it draws through.
    private readonly ulong[] _pendingWrites = new ulong[Pages / 64];
    private readonly ulong[] _pendingReads = new ulong[Pages / 64];
    private readonly List<Surface> _surfaces = [];
    private readonly ulong[] _scratchA = new ulong[Pages / 64];
    private readonly ulong[] _scratchB = new ulong[Pages / 64];
    private readonly ulong[] _scratchC = new ulong[Pages / 64];
    private readonly ulong[] _uploadPages = new ulong[Pages / 64];

    private Renderer[]? _renderers;
    private Action<int>? _bandWork;
    private int _segmentStart;
    private int _segmentEnd;
    private int _segmentBands;

    /// <summary>
    /// Where batches may be drawn on several cores (the platform's thread pool); null draws them on the calling thread.
    /// The pictures are the same either way.
    /// </summary>
    public IParallelWork? ParallelWork { get; set; }

    /// <summary>Draws every batch again at its own resolution (the GPU renderer); null for none.</summary>
    internal IPs2GsBackend? Backend { get; set; }

    /// <summary>Local memory as it stands, without drawing the batch first (for <see cref="Backend"/>).</summary>
    internal byte[] Memory => _memory;

    /// <summary>Primitives drawn by more than one renderer so far (diagnostics and tests; not part of the state).</summary>
    public long ParallelPrimitives { get; private set; }

    internal struct Command
    {
        public int Kind;
        public int State;
        public bool Serial;
        public long Pixels;
        public Vertex A;
        public Vertex B;
        public Vertex C;
    }

    /// <summary>A buffer drawn to: its base, width and pixel layout (the page table), and its pages.</summary>
    private readonly record struct Surface(int Bp, int Bw, int[] Table, bool Is16, int FirstPage, int PageCount);

    /// <summary>Queues a primitive (the vertices as kicked; see <see cref="Renderer"/> for how each kind draws).</summary>
    private void Enqueue(int kind, in Vertex a, in Vertex b, in Vertex c)
    {
        if (_count == BatchCapacity)
        {
            Flush();
        }

        if (_stateIndex < 0)
        {
            QueueState();
        }

        if (_count == _commands.Length)
        {
            Array.Resize(ref _commands, _commands.Length * 2);
        }

        ref var command = ref _commands[_count++];
        command.Kind = kind;
        command.State = _stateIndex;
        command.Serial = _stateSerial;
        command.A = a;
        command.B = b;
        command.C = c;
        command.Pixels = kind switch
        {
            PrimPoint => 1,
            PrimLine => (Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)) >> 4) + 1,
            PrimSprite => ((long)(Math.Abs(b.X - a.X) >> 4) + 1) * ((Math.Abs(b.Y - a.Y) >> 4) + 1),
            _ => (((long)(Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X))) >> 4) + 1)
                * (((long)(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y))) >> 4) + 1) / 2,
        };
    }

    /// <summary>
    /// Adds the current draw state to the batch, first drawing the batch if the state reads what it draws or draws it
    /// through another layout.
    /// </summary>
    private void QueueState()
    {
        ref readonly var s = ref _state;
        var frame = FrameSurface(in s);
        var zUsed = s.Zte && s.Ztst != 0;
        var z = zUsed ? ZSurface(in s) : default;
        var writes = _scratchA;
        var reads = _scratchB;
        Array.Clear(writes);
        Array.Clear(reads);
        Mark(writes, frame.FirstPage, frame.PageCount);
        if (zUsed)
        {
            Mark(writes, z.FirstPage, z.PageCount);
        }

        if (s.Tme)
        {
            MarkTexture(reads, in s);
        }

        // Rows of this state alone that can share memory: one renderer draws it.
        var serial = s.Fbw == 0 || s.Scax1 >= s.Fbw * 64 || frame.PageCount >= Pages
            || (zUsed && (Overlaps(frame, z) || z.PageCount >= Pages))
            || (s.Tme && Intersects(reads, writes));

        // Reading what the batch draws, drawing over a texture it reads, or drawing its pages through another layout:
        // draw the batch first.
        if (_count > 0 && (Intersects(reads, _pendingWrites) || Intersects(writes, _pendingReads)
            || ConflictsWithBatch(frame) || (zUsed && ConflictsWithBatch(z))))
        {
            Flush();
        }

        if (_stateCount == _states.Length)
        {
            Array.Resize(ref _states, _states.Length * 2);
        }

        _states[_stateCount] = _state;
        _stateIndex = _stateCount++;
        _stateSerial = serial;
        Or(_pendingWrites, writes);
        Or(_pendingReads, reads);
        AddSurface(frame);
        if (zUsed)
        {
            AddSurface(z);
        }
    }

    /// <summary>Draws the batch (if there is one) into local memory.</summary>
    private void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        Backend?.Draw(_commands.AsSpan(0, _count), _states.AsSpan(0, _stateCount), _pendingWrites, _pendingReads);
        _renderers ??= CreateRenderers();
        var bands = Math.Clamp(ParallelWork?.Concurrency ?? 1, 1, MaxBands);
        var i = 0;
        while (i < _count)
        {
            if (_commands[i].Serial || bands == 1)
            {
                var end = i + 1;
                while (end < _count && (bands == 1 || _commands[end].Serial))
                {
                    end++;
                }

                _renderers[0].Execute(_commands, _states, i, end, 0, 1);
                i = end;
                continue;
            }

            var j = i;
            long pixels = 0;
            while (j < _count && !_commands[j].Serial)
            {
                pixels += _commands[j].Pixels;
                j++;
            }

            if (pixels < ParallelPixels)
            {
                _renderers[0].Execute(_commands, _states, i, j, 0, 1);
            }
            else
            {
                _segmentStart = i;
                _segmentEnd = j;
                _segmentBands = bands;
                ParallelWork!.For(bands, _bandWork!);
                ParallelPrimitives += j - i;
            }

            i = j;
        }

        Backend?.Drawn(_pendingWrites);
        DiscardBatch();
    }

    /// <summary>Forgets the batch (drawn, or replaced by a reset or a loaded state).</summary>
    private void DiscardBatch()
    {
        _count = 0;
        _stateCount = 0;
        _stateIndex = -1;
        Array.Clear(_pendingWrites);
        Array.Clear(_pendingReads);
        _surfaces.Clear();

        // The current state's palette stays in use; the others can be reused.
        _paletteCount = 0;
        if (_state.Palette is { } current)
        {
            var at = _palettes.IndexOf(current);
            if (at > 0)
            {
                (_palettes[0], _palettes[at]) = (_palettes[at], _palettes[0]);
            }

            _paletteCount = 1;
        }
    }

    private Renderer[] CreateRenderers()
    {
        var renderers = new Renderer[MaxBands];
        for (var i = 0; i < renderers.Length; i++)
        {
            renderers[i] = new Renderer(_memory);
        }

        _bandWork = band => renderers[band].Execute(_commands, _states, _segmentStart, _segmentEnd, band, _segmentBands);
        return renderers;
    }

    /// <summary>A palette snapshot for a state about to be queued.</summary>
    private uint[] RentPalette()
    {
        if (_paletteCount == _palettes.Count)
        {
            _palettes.Add(new uint[256]);
        }

        return _palettes[_paletteCount++];
    }

    // ---- Pages --------------------------------------------------------------------------------------------------

    /// <summary>Pixel layouts: page width and height in pixels, and pages per row for a buffer width (in 64s).</summary>
    private static (int Width, int Height) PageSize(int psm) => psm switch
    {
        PsmCt16 or PsmCt16S or PsmZ16 or PsmZ16S => (64, 64),
        PsmT8 => (128, 64),
        PsmT4 => (128, 128),
        _ => (64, 32),
    };

    /// <summary>
    /// The pages of a buffer's rows [<paramref name="y0"/>, <paramref name="y1"/>], columns 0 … <paramref name="x1"/>:
    /// its page rows are consecutive, so this is one range (with one page more when the base isn't page-aligned).
    /// </summary>
    private static (int First, int Count) BufferPages(int bp, int bw, int psm, int x1, int y0, int y1)
    {
        var (width, height) = PageSize(psm);
        var perRow = Math.Max(width == 128 ? bw >> 1 : bw, (x1 / width) + 1);
        var first = (bp >> 5) + ((y0 / height) * perRow);
        var count = (((y1 / height) - (y0 / height) + 1) * perRow) + ((bp & 31) != 0 ? 1 : 0);
        return (first, count);
    }

    private static Surface FrameSurface(in DrawState s)
    {
        var (first, count) = BufferPages(s.Fbp, s.Fbw, s.Fpsm, s.Scax1, s.Scay0, s.Scay1);
        return new Surface(s.Fbp, s.Fbw, s.FbTable, s.Fb16, first, count);
    }

    private static Surface ZSurface(in DrawState s)
    {
        var (first, count) = BufferPages(s.Zbp, s.Fbw, s.Z16 ? PsmZ16 : PsmZ32, s.Scax1, s.Scay0, s.Scay1);
        return new Surface(s.Zbp, s.Fbw, s.ZTable, s.Z16, first, count);
    }

    /// <summary>The pages a texture can be read from: its size, or all of 1024 for the region modes.</summary>
    private static void MarkTexture(ulong[] pages, in DrawState s)
    {
        var width = s.Wms >= 2 ? 1024 : s.TexW;
        var height = s.Wmt >= 2 ? 1024 : s.TexH;
        var (first, count) = BufferPages(s.Tbp, s.Tbw, s.Tpsm, width - 1, 0, height - 1);
        Mark(pages, first, count);
    }

    private static void Mark(ulong[] pages, int first, int count)
    {
        if (count >= Pages)
        {
            Array.Fill(pages, ulong.MaxValue);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var page = (first + i) & (Pages - 1);
            pages[page >> 6] |= 1UL << (page & 63);
        }
    }

    private static bool Intersects(ulong[] a, ulong[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if ((a[i] & b[i]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void Or(ulong[] into, ulong[] from)
    {
        for (var i = 0; i < into.Length; i++)
        {
            into[i] |= from[i];
        }
    }

    private bool Overlaps(in Surface a, in Surface b)
    {
        Array.Clear(_scratchC);
        Mark(_scratchC, a.FirstPage, a.PageCount);
        for (var i = 0; i < b.PageCount && i < Pages; i++)
        {
            var page = (b.FirstPage + i) & (Pages - 1);
            if ((_scratchC[page >> 6] & (1UL << (page & 63))) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The batch draws to some of <paramref name="surface"/>'s pages through another layout.</summary>
    private bool ConflictsWithBatch(in Surface surface)
    {
        foreach (var other in _surfaces)
        {
            var same = other.Bp == surface.Bp && other.Bw == surface.Bw && ReferenceEquals(other.Table, surface.Table)
                && other.Is16 == surface.Is16;
            if (!same && Overlaps(other, surface))
            {
                return true;
            }
        }

        return false;
    }

    private void AddSurface(in Surface surface)
    {
        foreach (var other in _surfaces)
        {
            if (other == surface)
            {
                return;
            }
        }

        _surfaces.Add(surface);
    }

    /// <summary>Before writing a transfer's pixels: draws the batch if it reads or draws the transfer's pages.</summary>
    private void FlushForUpload()
    {
        if (_count > 0 && (Intersects(_uploadPages, _pendingWrites) || Intersects(_uploadPages, _pendingReads)))
        {
            Flush();
        }
    }

    /// <summary>Marks the pages a host-to-local transfer writes.</summary>
    private void SetUploadPages(int bp, int bw, int psm, int x, int y, int width, int height)
    {
        Array.Clear(_uploadPages);
        var x1 = x + width - 1;
        var y1 = y + height - 1;
        if (x1 >= 2048 || y1 >= 2048)
        {
            Array.Fill(_uploadPages, ulong.MaxValue); // coordinates wrap: anywhere
            return;
        }

        var (first, count) = BufferPages(bp, bw, psm, x1, y, y1);
        Mark(_uploadPages, first, count);
    }

    /// <summary>Before a CLUT load: draws the batch if it draws to the pages the CLUT comes from.</summary>
    private void FlushForClut(int cbp, int cbw, int psm, int x1, int y0, int y1)
    {
        if (_count == 0)
        {
            return;
        }

        Array.Clear(_scratchC);
        var (first, count) = BufferPages(cbp, cbw, psm, x1, y0, y1);
        Mark(_scratchC, first, count);
        if (Intersects(_scratchC, _pendingWrites))
        {
            Flush();
        }
    }
}
