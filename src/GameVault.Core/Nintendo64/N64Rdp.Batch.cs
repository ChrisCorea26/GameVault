using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>
/// Drawing a command run on several cores. While <see cref="ParallelWork"/> is set, the RDP still runs every
/// command as it arrives, except the drawing ones: those are recorded, with the commands around them, and drawn
/// before the run returns (the CPU can read RDRAM straight after) by replicas of the RDP. A replica starts from the
/// RDP's own state, replays the recorded commands and draws only its band of rows (every Nth strip of eight lines).
/// Every per-line step runs for every line, so a pixel's value doesn't depend on which replica drew it; RDRAM, the
/// RDP's state and its save state come out byte for byte as one RDP would leave them.
/// </summary>
/// <remarks>
/// <para>
/// What a pixel carries to the next: one-cycle mode's COMBINED input (the previous pixel's result) and the
/// combiner's other inputs, which keep their last value. Every write to an input is stamped with the command and line
/// that made it, and after each stretch of shared work every replica takes each input from the latest stamp: the
/// value the last pixel in one RDP's order left. A primitive that reads an input before writing it (the COMBINED
/// carry), or uses the random generator (noise, random dither), is drawn by one replica alone, in order; one that
/// reads an input it doesn't write, after earlier recorded primitives wrote it, starts after a merge.
/// </para>
/// <para>
/// Memory: the recording is drawn first when a load (Load Tile, Load Block, Load TLUT) reads RDRAM the recording draws
/// to, when a primitive would draw over RDRAM a recorded load read or over the commands still to come, or when it
/// draws RDRAM the recording draws through another layout (other address, width or size). A primitive whose own rows
/// can share memory (a Z buffer over its colour image, or a scissor wider than the image) is drawn by one replica.
/// </para>
/// </remarks>
public sealed partial class N64Rdp
{
    private const int MaxBands = 8;
    private const int BandShift = 3;

    /// <summary>A recording with fewer pixels than this is drawn by one replica (sharing it out would cost more).</summary>
    private const long ParallelPixels = 6000;

    private const int SlotNextTexel = SlotCount; // the two-cycle next texel, kept with the inputs
    private const int StampedSlots = SlotCount + 1;

    /// <summary>The inputs that only commands set (never a pixel).</summary>
    private const uint ConstantSlots = (1u << SlotPrim) | (1u << SlotEnv) | (1u << SlotOne) | (1u << SlotZero) |
        (1u << SlotKeyCenter) | (1u << SlotKeyScale) | (1u << SlotK4) | (1u << SlotK5) | (1u << SlotPrimAlpha) |
        (1u << SlotEnvAlpha) | (1u << SlotPrimLod);

    private readonly bool _replica;
    private int _band;
    private int _bands = 1;

    /// <summary>The stamp the next input write gets: (command + 1) &lt;&lt; 16 | (line + 1).</summary>
    private long _stamp;

    private readonly long[] _slotStamp = new long[StampedSlots];

    private N64Rdp[]? _replicas;
    private int[] _replicaAt = [];
    private bool _recording;
    private readonly List<ulong> _words = [];
    private Recorded[] _recorded = new Recorded[64];
    private int _recordedCount;
    private readonly List<(long Start, long End)> _pendingWrites = [];
    private readonly List<(long Start, long End)> _pendingLoads = [];
    private readonly List<Surface> _surfaces = [];
    private uint _segmentWrittenSlots;
    private readonly StateWriter _syncWriter = new(16 * 1024);
    private Action<int>? _replicaWork;
    private int _segmentStart;
    private int _segmentEnd;
    private int _segmentBands;

    private N64Rdp(IN64RcpHost host, bool replica)
        : this(host)
    {
        _replica = replica;
    }

    /// <summary>
    /// Where command runs may be drawn on several cores (the platform's thread pool); null draws them as they arrive.
    /// The pictures and the state are the same either way.
    /// </summary>
    public IParallelWork? ParallelWork { get; set; }

    /// <summary>Primitives drawn by more than one replica so far (diagnostics and tests; not part of the state).</summary>
    public long ParallelPrimitives { get; private set; }

    private enum CommandKind : byte
    {
        State,
        Load,
        Draw,
    }

    private struct Recorded
    {
        public int Id;
        public int Offset;
        public int Length;
        public CommandKind Kind;
        public bool Serial;
        public bool MergeBefore;
        public long Pixels;
    }

    private readonly record struct Surface(uint Address, int Width, int Size, long Start, long End);

    private bool Batching => ParallelWork is { Concurrency: > 1 } && !_replica;

    private static CommandKind KindOf(int id) => id switch
    {
        >= 0x08 and <= 0x0F or 0x24 or 0x25 or 0x36 => CommandKind.Draw,
        0x30 or 0x33 or 0x34 => CommandKind.Load,
        _ => CommandKind.State,
    };

    /// <summary>Runs a complete command while batching: draws are recorded, everything else runs here too.</summary>
    private void Record(int id)
    {
        var kind = KindOf(id);
        if (kind == CommandKind.Draw)
        {
            RecordDraw(id);
            return;
        }

        if (kind == CommandKind.Load)
        {
            var (start, end) = LoadSource(id, _command[0]);
            if (Overlaps(_pendingWrites, start, end))
            {
                FlushBatch();
            }

            Execute(id);
            if (_recording)
            {
                Append(id, kind);
                _pendingLoads.Add((start, end));
            }

            return;
        }

        Execute(id);
        if (_recording)
        {
            Append(id, kind);
        }
    }

    private void RecordDraw(int id)
    {
        var w = _command[0];
        int yTop, yBottom;
        if (id <= 0x0F)
        {
            yTop = SignExtend((int)w & 0x3FFF, 14) >> 2;
            yBottom = (SignExtend((int)(w >> 32) & 0x3FFF, 14) >> 2) + 1;
        }
        else
        {
            yTop = ((int)w & 0xFFF) >> 2;
            yBottom = (((int)(w >> 32) & 0xFFF) >> 2) + 1;
        }

        var y0 = Math.Max(Math.Max(yTop, _scissorYh >> 2), 0);
        var y1 = Math.Min(Math.Min(yBottom, (_scissorYl >> 2) + 1), LineCount - 2);
        var filling = _cycleType is CycleFill or CycleCopy;
        var zUsed = !filling && (_zCompare || _zUpdate);
        var color = ImageSurface(_colorImage.Address, _colorImage.Width, _colorImage.Size, y0, y1);
        var z = zUsed ? ImageSurface(_zImageAddress, _colorImage.Width, Size16, y0, y1) : default;

        // Rows of this primitive that can share memory, the random generator, or a carried input read before it is
        // written: one replica draws it, in order.
        var serial = _colorImage.Width == 0 || RightmostPixel(id, filling) >= _colorImage.Width || y1 < y0
            || (zUsed && color.Start < z.End && z.Start < color.End);
        uint writes = 0, staleReads = 0;
        if (!filling)
        {
            AnalyzeInputs(id, out writes, out staleReads, out var carried);
            serial |= carried || UsesRandom();
        }

        // Drawing over a recorded load's source or the commands to come, or the recording's memory through another
        // layout: draw the recording first.
        if (_recording && (Overlaps(_pendingLoads, color.Start, color.End) || (zUsed && Overlaps(_pendingLoads, z.Start, z.End))
            || ConflictsWithRecording(color) || (zUsed && ConflictsWithRecording(z))))
        {
            FlushBatch();
        }

        if (!_recording)
        {
            BeginRecording();
        }

        // A primitive drawn alone leaves every replica with the same inputs, as does a merge; a stretch shared out leaves
        // them different for the inputs its primitives wrote.
        var mergeBefore = (staleReads & _segmentWrittenSlots) != 0;
        ref var r = ref Append(id, CommandKind.Draw);
        r.Serial = serial;
        r.MergeBefore = mergeBefore;
        r.Pixels = (long)Math.Max(0, y1 - y0) * Math.Max(1, Math.Min(_colorImage.Width, (_scissorXl - _scissorXh) >> 2));
        if (serial)
        {
            _segmentWrittenSlots = 0;
        }
        else
        {
            if (mergeBefore)
            {
                _segmentWrittenSlots = 0;
            }

            _segmentWrittenSlots |= writes;
        }
        CommandsExecuted++;
        _pendingWrites.Add((color.Start, color.End));
        AddSurface(color);
        if (zUsed)
        {
            _pendingWrites.Add((z.Start, z.End));
            AddSurface(z);
        }

        // The commands still to read must come after the drawing (the list could be drawn over).
        if (!_xbus && Overlaps(_pendingWrites, _current, _end))
        {
            FlushBatch();
        }
    }

    /// <summary>
    /// The rightmost pixel a primitive can draw: its edges' largest X, clipped to the scissor. A pixel past the image's
    /// last column is the next row's first, so such a primitive's rows can share memory. Fill and copy modes draw the
    /// right edge's column; the other modes only when the edge has a fraction of a pixel there (coverage).
    /// </summary>
    private int RightmostPixel(int id, bool filling)
    {
        var c = _command;
        long right; // in 1/8 pixel
        if (id <= 0x0F)
        {
            var w0 = c[0];
            long yl = SignExtend((int)(w0 >> 32) & 0x3FFF, 14);
            long ym = SignExtend((int)(w0 >> 16) & 0x3FFF, 14);
            long yh = SignExtend((int)w0 & 0x3FFF, 14) & ~3L;
            long xl = SignExtend((int)(c[1] >> 32), 28), dxl = SignExtend((int)c[1], 30);
            long xh = SignExtend((int)(c[2] >> 32), 28), dxh = SignExtend((int)c[2], 30);
            long xm = SignExtend((int)(c[3] >> 32), 28), dxm = SignExtend((int)c[3], 30);

            // Each edge's X at its ends (slopes are per scanline; Y is in quarter scanlines).
            var x = Math.Max(Math.Max(xh, xh + (dxh * Math.Max(yl - yh, 0) / 4)), Math.Max(xm, xm + (dxm * Math.Max(ym - yh, 0) / 4)));
            x = Math.Max(x, Math.Max(xl, xl + (dxl * Math.Max(yl - ym, 0) / 4)));
            right = (x >> 13) + 2; // with a sticky fraction, and the walker's rounding
        }
        else
        {
            right = (((long)(c[0] >> 44) & 0xFFF) << 1) + (filling ? 0 : 1);
        }

        right = Math.Min(right, _scissorXl << 1);
        return (int)(filling || (right & 7) != 0 ? right >> 3 : (right >> 3) - 1);
    }

    private ref Recorded Append(int id, CommandKind kind)
    {
        if (_recordedCount == _recorded.Length)
        {
            Array.Resize(ref _recorded, _recorded.Length * 2);
        }

        ref var r = ref _recorded[_recordedCount++];
        r = default;
        r.Id = id;
        r.Kind = kind;
        r.Offset = _words.Count;
        r.Length = CommandLength[id];
        for (var i = 0; i < r.Length; i++)
        {
            _words.Add(_command[i]);
        }

        return ref r;
    }

    /// <summary>Starts a recording: every replica takes this RDP's state.</summary>
    private void BeginRecording()
    {
        var bands = Math.Clamp(ParallelWork?.Concurrency ?? 1, 1, MaxBands);
        if (_replicas is null || _replicas.Length != bands)
        {
            _replicas = CreateReplicas(bands); // one per band, synced at every recording
        }

        _syncWriter.Reset();
        SaveState(_syncWriter);
        var state = _syncWriter.ToArray();
        foreach (var replica in _replicas)
        {
            replica.LoadState(new StateReader(state));
            replica._nextTexel = _nextTexel;
            Array.Clear(replica._slotStamp);
        }

        Array.Fill(_replicaAt, 0);
        _recording = true;
        _segmentWrittenSlots = 0;
    }

    private N64Rdp[] CreateReplicas(int count)
    {
        var replicas = new N64Rdp[count];
        for (var i = 0; i < replicas.Length; i++)
        {
            replicas[i] = new N64Rdp(_host, replica: true);
        }

        _replicaAt = new int[count];
        _replicaWork = band =>
        {
            var replica = replicas[band];
            replica.Replay(this, _replicaAt[band], _segmentStart, skipDraws: true, band: 0, bands: 1);
            replica.Replay(this, _segmentStart, _segmentEnd, skipDraws: false, band, _segmentBands);
            _replicaAt[band] = _segmentEnd;
        };
        return replicas;
    }

    /// <summary>Draws the recording (if there is one) and takes back what drawing changed: the inputs, the clock, the noise.</summary>
    private void FlushBatch()
    {
        if (!_recording)
        {
            return;
        }

        var replicas = _replicas!;
        var bands = replicas.Length;
        var i = 0;
        while (i < _recordedCount)
        {
            // A stretch that can be shared out: up to the next primitive drawn alone or starting after a merge.
            var j = i;
            long pixels = 0;
            var draws = 0;
            while (j < _recordedCount && !(_recorded[j].Kind == CommandKind.Draw && (_recorded[j].Serial || (_recorded[j].MergeBefore && draws > 0))))
            {
                if (_recorded[j].Kind == CommandKind.Draw)
                {
                    pixels += _recorded[j].Pixels;
                    draws++;
                }

                j++;
            }

            if (j == i)
            {
                // A primitive drawn alone, by replica 0, after every replica has merged what came before.
                replicas[0].Replay(this, _replicaAt[0], i, skipDraws: true, 0, 1);
                replicas[0].Replay(this, i, i + 1, skipDraws: false, 0, 1);
                _replicaAt[0] = i + 1;
                ShareInputsFrom(replicas[0], replicas);
                i++;
                continue;
            }

            if (pixels < ParallelPixels || draws == 0)
            {
                replicas[0].Replay(this, _replicaAt[0], i, skipDraws: true, 0, 1);
                replicas[0].Replay(this, i, j, skipDraws: false, 0, 1);
                _replicaAt[0] = j;
                ShareInputsFrom(replicas[0], replicas);
            }
            else
            {
                _segmentStart = i;
                _segmentEnd = j;
                _segmentBands = bands;
                ParallelWork!.For(bands, _replicaWork!);
                ParallelPrimitives += draws;
                MergeInputs(replicas, bands);
            }

            i = j;
        }

        // What drawing changed in the RDP: the inputs' last values, the clock and the random generator (replica 0
        // counted every line and drew every primitive that uses it).
        CopyInputs(replicas[0], this);
        _clock = replicas[0]._clock;
        _noise = replicas[0]._noise;

        _recording = false;
        _recordedCount = 0;
        _words.Clear();
        _pendingWrites.Clear();
        _pendingLoads.Clear();
        _surfaces.Clear();
    }

    /// <summary>Replays recorded commands [<paramref name="from"/>, <paramref name="to"/>) on a replica.</summary>
    private void Replay(N64Rdp primary, int from, int to, bool skipDraws, int band, int bands)
    {
        _band = band;
        _bands = bands;
        var recorded = primary._recorded;
        var words = CollectionsMarshal.AsSpan(primary._words);
        for (var i = from; i < to; i++)
        {
            ref var r = ref recorded[i];
            if (skipDraws && r.Kind == CommandKind.Draw)
            {
                continue;
            }

            words.Slice(r.Offset, r.Length).CopyTo(_command);
            _stamp = (long)(i + 1) << 16;
            Execute(r.Id);
        }

        _band = 0;
        _bands = 1;
    }

    /// <summary>After a shared stretch: each input from the replica whose write was last in one RDP's order.</summary>
    private static void MergeInputs(N64Rdp[] replicas, int bands)
    {
        for (var slot = 0; slot < StampedSlots; slot++)
        {
            var best = 0;
            for (var r = 1; r < bands; r++)
            {
                if (replicas[r]._slotStamp[slot] > replicas[best]._slotStamp[slot])
                {
                    best = r;
                }
            }

            var from = replicas[best];
            for (var r = 0; r < replicas.Length; r++)
            {
                if (r != best)
                {
                    CopySlotState(from, replicas[r], slot);
                }
            }
        }
    }

    private static void ShareInputsFrom(N64Rdp source, N64Rdp[] replicas)
    {
        foreach (var replica in replicas)
        {
            if (replica != source)
            {
                CopyInputs(source, replica);
            }
        }
    }

    private static void CopyInputs(N64Rdp from, N64Rdp to)
    {
        for (var slot = 0; slot < StampedSlots; slot++)
        {
            CopySlotState(from, to, slot);
        }
    }

    private static void CopySlotState(N64Rdp from, N64Rdp to, int slot)
    {
        if (slot == SlotNextTexel)
        {
            to._nextTexel = from._nextTexel;
        }
        else
        {
            from._ext.AsSpan(slot * 4, 4).CopyTo(to._ext.AsSpan(slot * 4, 4));
            from._mul.AsSpan(slot * 4, 4).CopyTo(to._mul.AsSpan(slot * 4, 4));
        }

        to._slotStamp[slot] = from._slotStamp[slot];
    }

    /// <summary>Whether line <paramref name="line"/> is this replica's to draw.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MyLine(int line) => _bands == 1 || (int)((uint)(line >> BandShift) % (uint)_bands) == _band;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Stamp(int slot) => _slotStamp[slot] = _stamp;

    // ---- What a primitive reads and writes --------------------------------------------------------------------

    /// <summary>The random generator: noise, random colour dither, or the dithered alpha compare.</summary>
    private bool UsesRandom() => _noiseUsed || _alphaDither == 2 || _rgbDither == 2 || (_alphaCompare && _ditherAlpha);

    /// <summary>
    /// The combiner inputs a one- or two-cycle primitive writes for its pixels, the ones it reads without writing
    /// (left from before it), and whether a pixel reads an input that it writes only later (left by the previous
    /// pixel: the COMBINED carry, or a texel the two-cycle swap writes after reading).
    /// </summary>
    private void AnalyzeInputs(int id, out uint writes, out uint staleReads, out bool carried)
    {
        const uint combined = (1u << SlotCombined) | (1u << SlotCombinedAlpha);
        const uint tex0 = (1u << SlotTex0) | (1u << SlotTex0Alpha);
        const uint tex1 = (1u << SlotTex1) | (1u << SlotTex1Alpha);
        var twoCycle = _cycleType == CycleTwo;
        var textured = id is 0x24 or 0x25 || (id <= 0x0F && (id & 2) != 0);
        var alphaAsColor = _alphaAsColorUsed;
        var firstReads = CycleReads(0);
        var secondReads = CycleReads(1);

        // Written before the combiner: shade, then the texture unit's texels.
        uint before = 0;
        if (_shadeUsed)
        {
            before |= (1u << SlotShade) | (alphaAsColor ? 1u << SlotShadeAlpha : 0);
        }

        if (textured)
        {
            before |= (1u << SlotTex0) | (alphaAsColor ? 1u << SlotTex0Alpha : 0);
            if (_lodFractionUsed)
            {
                before |= 1u << SlotLodFraction;
            }

            if (twoCycle ? _needTex1 : _needNextTex1)
            {
                before |= (1u << SlotTex1) | (alphaAsColor ? 1u << SlotTex1Alpha : 0);
            }

            if (twoCycle && _needNextTex1)
            {
                before |= 1u << SlotNextTexel;
            }
        }

        uint stale;
        if (twoCycle)
        {
            // The first cycle, then its result as COMBINED, then the swap (TEXEL1 copied to TEXEL0, the next texel to
            // TEXEL1), then the second cycle.
            var swap = _needTex1 || _needNextTex1;
            var later = combined | (swap ? tex0 : 0) | (swap && _needNextTex1 ? (1u << SlotTex1) | (alphaAsColor ? 1u << SlotTex1Alpha : 0) : 0);
            carried = (firstReads & later & ~before) != 0;
            stale = firstReads & ~before & ~later;
            var afterFirst = before | combined;
            if (swap)
            {
                carried |= (tex1 & ~afterFirst & later) != 0;
                stale |= tex1 & ~afterFirst & ~later;
                if (_needNextTex1)
                {
                    stale |= (1u << SlotNextTexel) & ~before;
                }
            }

            writes = before | later;
            stale |= secondReads & ~writes;
        }
        else
        {
            // COMBINED is written after the cycle when it is read: the next pixel reads it.
            var later = _combinedUsed ? combined : 0;
            carried = (secondReads & later) != 0;
            stale = secondReads & ~before & ~later;
            writes = before | later;
        }

        staleReads = stale & ~ConstantSlots;
    }

    private uint CycleReads(int cycle) =>
        Bit(_ccA[cycle]) | Bit(_ccB[cycle]) | Bit(_ccM[cycle]) | Bit(_ccD[cycle])
        | Bit(_acA[cycle]) | Bit(_acB[cycle]) | Bit(_acM[cycle]) | Bit(_acD[cycle]);

    private static uint Bit(int slot) => 1u << slot;

    // ---- Memory ranges ----------------------------------------------------------------------------------------

    private static Surface ImageSurface(uint address, int width, int size, int y0, int y1)
    {
        var start = address + (long)TexelBytes(y0 * width, size);
        var end = address + (long)TexelBytes((y1 + 1) * width, size) + 8;
        return new Surface(address, width, size, start, end);
    }

    /// <summary>The RDRAM a load reads (from the texture image).</summary>
    private (long Start, long End) LoadSource(int id, ulong w)
    {
        var image = _textureImage;
        var sl = (int)(w >> 44) & 0xFFF;
        var tl = (int)(w >> 32) & 0xFFF;
        var sh = (int)(w >> 12) & 0xFFF;
        var th = (int)w & 0xFFF;
        long start, end;
        switch (id)
        {
            case 0x33: // Load Block: texels SL…SH from row TL, in a line
                start = image.Address + (long)TexelBytes(tl * image.Width + sl, image.Size);
                end = start + Math.Min(((long)TexelBytes(Math.Max(sh - sl + 1, 0), image.Size) + 7) & ~7L, 2048 * 8);
                break;
            default: // Load Tile and Load TLUT: a rectangle in 10.2 coordinates
                start = image.Address + (long)TexelBytes((tl >> 2) * image.Width + (sl >> 2), image.Size);
                end = image.Address + (long)TexelBytes((th >> 2) * image.Width + (sh >> 2) + 1, image.Size) + 16;
                if (id == 0x30)
                {
                    end += 8L * (((sh - sl) >> 2) + 1); // TLUT steps can be 8 bytes an entry
                }

                break;
        }

        return (start, Math.Max(end, start + 8) + 8);
    }

    /// <summary>
    /// Whether [<paramref name="start"/>, <paramref name="end"/>) meets any of <paramref name="ranges"/>. Addresses wrap
    /// at 16 MB (the RDP masks them), so a range past the end counts as everything.
    /// </summary>
    private static bool Overlaps(List<(long Start, long End)> ranges, long start, long end)
    {
        foreach (var (s, e) in ranges)
        {
            if (e > 0x1000000 || end > 0x1000000 || (start < e && s < end))
            {
                return true;
            }
        }

        return false;
    }

    private bool ConflictsWithRecording(in Surface surface)
    {
        foreach (var other in _surfaces)
        {
            var same = other.Address == surface.Address && other.Width == surface.Width && other.Size == surface.Size;
            if (!same && (surface.End > 0x1000000 || other.End > 0x1000000 || (surface.Start < other.End && other.Start < surface.End)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Notes a buffer the recording draws to, widening its range when it is already noted.</summary>
    private void AddSurface(in Surface surface)
    {
        for (var i = 0; i < _surfaces.Count; i++)
        {
            var other = _surfaces[i];
            if (other.Address == surface.Address && other.Width == surface.Width && other.Size == surface.Size)
            {
                _surfaces[i] = other with { Start = Math.Min(other.Start, surface.Start), End = Math.Max(other.End, surface.End) };
                return;
            }
        }

        _surfaces.Add(surface);
    }
}
