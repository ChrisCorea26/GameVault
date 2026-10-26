using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The Graphics Interface (ps2tek: "GIF Reference"): unpacks GIF packets from PATH1 (VU1's XGKICK), PATH2 (VIF1's
/// DIRECT/DIRECTHL) and PATH3 (the GIF DMA channel) into GS register writes.
/// </summary>
/// <remarks>
/// <para>
/// Each path keeps its own GIFtag state, so a packet carries on where it stopped when its path gets the GIF back. A
/// path holds the GIF from a packet's first GIFtag until the end of its EOP tag; the others wait (their writes
/// return 0, or PATH3's go into its FIFO). PATH1 copies the whole packet out of VU1 memory when kicked and runs it
/// as soon as the GIF is free — at once, or when the path holding it ends its packet.
/// </para>
/// <para>
/// PATH3 has the hardware's 16-quadword FIFO: while PATH3 is masked (MSKPATH3 or GIF_MODE.M3R) between packets, or
/// another path holds the GIF, its DMA fills the FIFO and stops when it is full. In intermittent mode
/// (GIF_MODE.IMT) PATH3 lets go of the GIF every 8 quadwords of IMAGE data: a queued PATH1 packet runs, and the
/// PATH3 write returns there (the DMA retries the rest), so VIF1 can send a PATH2 packet in between ("PATH3
/// slices"); without IMT PATH3 keeps the GIF to the end of its packet.
/// </para>
/// <para>
/// A second SIGNAL before the EE clears CSR.SIGNAL stalls the GS (ps2autotests gs/signal); the GIF then stops taking
/// data until the EE clears it.
/// </para>
/// </remarks>
public sealed class Ps2Gif : IPs2Gif, IStateful
{
    public const uint RegCtrl = 0x00;
    public const uint RegMode = 0x10;
    public const uint RegStat = 0x20;
    public const uint RegTag0 = 0x40;
    public const uint RegTag1 = 0x50;
    public const uint RegTag2 = 0x60;
    public const uint RegTag3 = 0x70;
    public const uint RegCnt = 0x80;
    public const uint RegP3Cnt = 0x90;
    public const uint RegP3Tag = 0xA0;

    public const int Path3FifoSize = 16;

    private const int FlgPacked = 0;
    private const int FlgRegList = 1;

    // Ample room for one VU1 packet (16 KB) plus one queued behind it.
    private const int Path1Capacity = 2048;

    private static readonly StateTag Tag = StateTag.From("GIF ");

    private readonly Ps2Gs _gs;
    private readonly PathState[] _paths = [new(), new(), new(), new()];
    private readonly UInt128[] _fifo = new UInt128[Path3FifoSize];
    private readonly UInt128[] _path1 = new UInt128[Path1Capacity];
    private int _fifoHead;
    private int _fifoCount;
    private int _path1Read;
    private int _path1Count;

    // 0: idle, otherwise the path that holds the GIF.
    private int _owner;
    private bool _paused;
    private bool _modeMask;
    private bool _intermittent;
    private bool _path3Masked;
    private UInt128 _lastTag;

    // The last Feed stopped at an intermittent-mode slice.
    private bool _sliced;

    public Ps2Gif(Ps2Gs gs)
    {
        _gs = gs;
        gs.StallCleared = Resume;
    }

    private sealed class PathState
    {
        public bool InPacket;
        public bool HaveTag;
        public int Nloop;
        public bool Eop;
        public int Flg;
        public int Nreg;
        public ulong Regs;
        public int RegIndex;
        public float Q = 1;
        public int ImageQwords;
        public UInt128 Tag;

        public void Reset()
        {
            InPacket = HaveTag = Eop = false;
            Nloop = Flg = Nreg = RegIndex = ImageQwords = 0;
            Regs = 0;
            Q = 1;
            Tag = UInt128.Zero;
        }
    }

    /// <inheritdoc/>
    public bool Path3Masked
    {
        get => _path3Masked;
        set
        {
            _path3Masked = value;
            if (!value)
            {
                Resume();
            }
        }
    }

    /// <inheritdoc/>
    public bool Path2Active => _paths[2].InPacket;

    /// <inheritdoc/>
    public bool Path3Active => _paths[3].InPacket || (_fifoCount > 0 && !Path3Blocked);

    /// <summary>A PATH1 packet is waiting for the GIF.</summary>
    public bool Path1Queued => _path1Read < _path1Count;

    /// <summary>The path holding the GIF (0 when idle).</summary>
    public int ActivePath => _owner;

    /// <summary>Quadwords waiting in PATH3's FIFO.</summary>
    public int Path3FifoCount => _fifoCount;

    private bool Path3Blocked => (_path3Masked || _modeMask) && !_paths[3].InPacket;

    public void Reset()
    {
        foreach (var p in _paths)
        {
            p.Reset();
        }

        _fifoHead = _fifoCount = 0;
        _path1Read = _path1Count = 0;
        _owner = 0;
        _paused = false;
        _modeMask = false;
        _intermittent = false;
        _path3Masked = false;
        _lastTag = UInt128.Zero;
    }

    // ---- Paths ------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void KickPath1(byte[] vu1DataMemory, uint address)
    {
        // Copy the packet out of VU1 memory (its tags tell how long it is), wrapping at the end of the memory.
        var qwords = vu1DataMemory.Length / 16;
        var q = (int)(address % (uint)qwords);
        if (_path1Read == _path1Count)
        {
            _path1Read = _path1Count = 0;
        }

        for (var guard = 0; guard < qwords && _path1Count < Path1Capacity; guard++)
        {
            var tag = ReadQword(vu1DataMemory, q);
            _path1[_path1Count++] = tag;
            q = (q + 1) % qwords;
            var lo = (ulong)tag;
            var nloop = (int)(lo & 0x7FFF);
            var eop = (lo & 0x8000) != 0;
            var flg = (int)((lo >> 58) & 3);
            var nreg = (int)((lo >> 60) & 0xF);
            if (nreg == 0)
            {
                nreg = 16;
            }

            var length = flg switch
            {
                FlgPacked => nloop * nreg,
                FlgRegList => ((nloop * nreg) + 1) / 2,
                _ => nloop,
            };
            for (var i = 0; i < length && _path1Count < Path1Capacity; i++)
            {
                _path1[_path1Count++] = ReadQword(vu1DataMemory, q);
                q = (q + 1) % qwords;
            }

            if (eop)
            {
                break;
            }
        }

        RunPath1();
    }

    private static UInt128 ReadQword(byte[] memory, int index)
    {
        var lo = BitConverter.ToUInt64(memory, index * 16);
        var hi = BitConverter.ToUInt64(memory, (index * 16) + 8);
        return new UInt128(hi, lo);
    }

    /// <inheritdoc/>
    public int WritePath2(ReadOnlySpan<UInt128> data) => _paused ? 0 : Feed(2, data);

    /// <inheritdoc/>
    public int WritePath3(ReadOnlySpan<UInt128> data)
    {
        if (_paused)
        {
            return 0;
        }

        DrainFifo();
        var taken = 0;
        if (_fifoCount == 0 && !Path3Blocked)
        {
            taken = Feed(3, data);
            if (_sliced)
            {
                // The DMA retries the rest, after PATH1/PATH2 had their chance.
                return taken;
            }
        }

        // What PATH3 can't send now waits in its FIFO.
        while (taken < data.Length && _fifoCount < Path3FifoSize)
        {
            _fifo[(_fifoHead + _fifoCount) % Path3FifoSize] = data[taken++];
            _fifoCount++;
        }

        return taken;
    }

    /// <inheritdoc/>
    public int ReadDownload(Span<UInt128> destination) => _gs.ReadDownload(destination);

    private bool CanTake(int path) => !_gs.Stalled && (_owner == 0 || _owner == path);

    /// <summary>Feeds quadwords from one path while it may hold the GIF; returns how many were taken.</summary>
    private int Feed(int path, ReadOnlySpan<UInt128> data)
    {
        var taken = 0;
        _sliced = false;
        while (taken < data.Length)
        {
            if (path != 1 && Path1Queued && _owner == 0)
            {
                RunPath1();
            }

            if (!CanTake(path))
            {
                break;
            }

            _owner = path;
            var ended = Process(path, data[taken++]);
            if (ended)
            {
                _owner = 0;
                RunPath1();
            }
            else if (ReleasesSlice(path))
            {
                // An intermittent-mode slice: let PATH1 in, and return so the caller can run PATH2.
                _owner = 0;
                _sliced = true;
                RunPath1();
                break;
            }
        }

        if (taken > 0 && _owner == 0)
        {
            AfterRelease(path);
        }

        return taken;
    }

    private bool ReleasesSlice(int path)
    {
        var p = _paths[path];
        return path == 3 && _intermittent && p.HaveTag && p.Flg >= 2 && p.ImageQwords > 0 && (p.ImageQwords & 7) == 0;
    }

    private void AfterRelease(int path)
    {
        if (path != 3)
        {
            DrainFifo();
        }
    }

    private void RunPath1()
    {
        while (_path1Read < _path1Count && CanTake(1))
        {
            _owner = 1;
            if (Process(1, _path1[_path1Read++]))
            {
                _owner = 0;
            }
        }

        if (_path1Read == _path1Count)
        {
            _path1Read = _path1Count = 0;
            if (_owner == 1)
            {
                _owner = 0;
            }
        }

        if (_owner == 0)
        {
            DrainFifo();
        }
    }

    private void DrainFifo()
    {
        if (_paused)
        {
            return;
        }

        while (_fifoCount > 0 && !Path3Blocked && CanTake(3))
        {
            _owner = 3;
            var ended = Process(3, _fifo[_fifoHead]);
            _fifoHead = (_fifoHead + 1) % Path3FifoSize;
            _fifoCount--;
            if (ended || ReleasesSlice(3))
            {
                _owner = 0;
                if (Path1Queued)
                {
                    RunPath1();
                }
            }
        }
    }

    /// <summary>Carries on after a stall, a pause or a mask ends.</summary>
    private void Resume()
    {
        if (_paused)
        {
            return;
        }

        RunPath1();
        DrainFifo();
    }

    // ---- GIFtags and data -------------------------------------------------------------------------------------

    /// <summary>Processes one quadword of a path; returns true when it ended the path's packet.</summary>
    private bool Process(int path, UInt128 qword)
    {
        var p = _paths[path];
        var lo = (ulong)qword;
        var hi = (ulong)(qword >> 64);
        if (!p.HaveTag)
        {
            p.Tag = qword;
            _lastTag = qword;
            p.Nloop = (int)(lo & 0x7FFF);
            p.Eop = (lo & 0x8000) != 0;
            p.Flg = (int)((lo >> 58) & 3);
            p.Nreg = (int)((lo >> 60) & 0xF);
            if (p.Nreg == 0)
            {
                p.Nreg = 16;
            }

            p.Regs = hi;
            p.RegIndex = 0;
            p.ImageQwords = 0;
            p.Q = 1;
            p.InPacket = true;

            // PRE: the PRIM field goes to PRIM (in PACKED mode).
            if ((lo & (1UL << 46)) != 0 && p.Flg == FlgPacked)
            {
                _gs.WriteRegister(Ps2Gs.RegPrim, (lo >> 47) & 0x7FF);
            }

            if (p.Nloop == 0)
            {
                return EndTag(p);
            }

            p.HaveTag = true;
            return false;
        }

        switch (p.Flg)
        {
            case FlgPacked:
                WritePacked(p, (int)((p.Regs >> (p.RegIndex * 4)) & 0xF), lo, hi);
                if (++p.RegIndex == p.Nreg)
                {
                    p.RegIndex = 0;
                    p.Nloop--;
                }

                break;
            case FlgRegList:
                for (var half = 0; half < 2 && p.Nloop > 0; half++)
                {
                    var reg = (int)((p.Regs >> (p.RegIndex * 4)) & 0xF);
                    if (reg < 0xE)
                    {
                        _gs.WriteRegister(reg, half == 0 ? lo : hi);
                    }

                    if (++p.RegIndex == p.Nreg)
                    {
                        p.RegIndex = 0;
                        p.Nloop--;
                    }
                }

                break;
            default:
                _gs.WriteHwReg(lo);
                _gs.WriteHwReg(hi);
                p.ImageQwords++;
                p.Nloop--;
                break;
        }

        return p.Nloop == 0 && EndTag(p);
    }

    private static bool EndTag(PathState p)
    {
        p.HaveTag = false;
        if (p.Eop)
        {
            p.InPacket = false;
            return true;
        }

        return false;
    }

    /// <summary>One PACKED-mode register descriptor (ps2tek: "GIF Data Formats").</summary>
    private void WritePacked(PathState p, int reg, ulong lo, ulong hi)
    {
        switch (reg)
        {
            case 0x0: // PRIM
                _gs.WriteRegister(Ps2Gs.RegPrim, lo & 0x7FF);
                break;
            case 0x1: // RGBA, with Q from the last STQ
            {
                var rgba = (lo & 0xFF) | (((lo >> 32) & 0xFF) << 8) | ((hi & 0xFF) << 16) | (((hi >> 32) & 0xFF) << 24);
                _gs.WriteRegister(Ps2Gs.RegRgbaq, rgba | ((ulong)BitConverter.SingleToUInt32Bits(p.Q) << 32));
                break;
            }

            case 0x2: // STQ
                p.Q = BitConverter.UInt32BitsToSingle((uint)hi);
                _gs.WriteRegister(Ps2Gs.RegSt, lo);
                break;
            case 0x3: // UV
                _gs.WriteRegister(Ps2Gs.RegUv, (lo & 0x3FFF) | (((lo >> 32) & 0x3FFF) << 16));
                break;
            case 0x4: // XYZF2, or XYZF3 with ADC
            {
                var value = (lo & 0xFFFF) | (((lo >> 32) & 0xFFFF) << 16) | (((hi >> 4) & 0xFF_FFFF) << 32) | (((hi >> 36) & 0xFF) << 56);
                _gs.WriteRegister((hi & (1UL << 47)) != 0 ? Ps2Gs.RegXyzf3 : Ps2Gs.RegXyzf2, value);
                break;
            }

            case 0x5: // XYZ2, or XYZ3 with ADC
            {
                var value = (lo & 0xFFFF) | (((lo >> 32) & 0xFFFF) << 16) | ((hi & 0xFFFF_FFFF) << 32);
                _gs.WriteRegister((hi & (1UL << 47)) != 0 ? Ps2Gs.RegXyz3 : Ps2Gs.RegXyz2, value);
                break;
            }

            case 0xA: // FOG
                _gs.WriteRegister(Ps2Gs.RegFog, ((hi >> 36) & 0xFF) << 56);
                break;
            case 0xE: // A+D
                _gs.WriteRegister((int)(hi & 0x7F), lo);
                break;
            case 0xF: // NOP
                break;
            default:
                _gs.WriteRegister(reg, lo);
                break;
        }
    }

    // ---- Registers at $10003000 -------------------------------------------------------------------------------

    /// <summary>Reads a GIF register; <paramref name="offset"/> is from $10003000.</summary>
    public uint ReadRegister(uint offset)
    {
        var current = _paths[_owner];
        var p3 = _paths[3];
        switch (offset & 0xF0)
        {
            case RegStat:
            {
                var stat = 0u;
                stat |= _modeMask ? 1u : 0;
                stat |= _path3Masked ? 1u << 1 : 0;
                stat |= _intermittent ? 1u << 2 : 0;
                stat |= _paused ? 1u << 3 : 0;
                stat |= p3.InPacket && _owner != 3 ? 1u << 5 : 0;
                stat |= _fifoCount > 0 && _owner != 3 ? 1u << 6 : 0;
                stat |= Path1Queued ? 1u << 8 : 0;
                stat |= _owner != 0 ? 1u << 9 : 0;
                stat |= (uint)_owner << 10;
                stat |= _gs.BusDirLocalToHost ? 1u << 12 : 0;
                stat |= (uint)_fifoCount << 24;
                return stat;
            }

            case RegTag0:
                return (uint)_lastTag;
            case RegTag1:
                return (uint)(_lastTag >> 32);
            case RegTag2:
                return (uint)(_lastTag >> 64);
            case RegTag3:
                return (uint)(_lastTag >> 96);
            case RegCnt:
                return (uint)(current.Nloop & 0x7FFF) | ((uint)current.RegIndex << 16);
            case RegP3Cnt:
                return (uint)(p3.Nloop & 0x7FFF);
            case RegP3Tag:
                return (uint)p3.Tag;
            default:
                return 0;
        }
    }

    /// <summary>Writes GIF_CTRL (bit 0 resets the GIF, bit 3 pauses it) or GIF_MODE (M3R, IMT).</summary>
    public void WriteRegister(uint offset, uint value)
    {
        switch (offset & 0xF0)
        {
            case RegCtrl:
                if ((value & 1) != 0)
                {
                    var mask = _path3Masked;
                    Reset();
                    _path3Masked = mask;
                }

                _paused = (value & 8) != 0;
                if (!_paused)
                {
                    Resume();
                }

                break;
            case RegMode:
                _modeMask = (value & 1) != 0;
                _intermittent = (value & 4) != 0;
                Resume();
                break;
        }
    }

    // ---- Save states ------------------------------------------------------------------------------------------

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var p in _paths)
        {
            writer.WriteBool(p.InPacket);
            writer.WriteBool(p.HaveTag);
            writer.WriteInt32(p.Nloop);
            writer.WriteBool(p.Eop);
            writer.WriteInt32(p.Flg);
            writer.WriteInt32(p.Nreg);
            writer.WriteUInt64(p.Regs);
            writer.WriteInt32(p.RegIndex);
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(p.Q));
            writer.WriteInt32(p.ImageQwords);
            WriteQword(writer, p.Tag);
        }

        writer.WriteInt32(_fifoCount);
        for (var i = 0; i < _fifoCount; i++)
        {
            WriteQword(writer, _fifo[(_fifoHead + i) % Path3FifoSize]);
        }

        writer.WriteInt32(_path1Count - _path1Read);
        for (var i = _path1Read; i < _path1Count; i++)
        {
            WriteQword(writer, _path1[i]);
        }

        writer.WriteInt32(_owner);
        writer.WriteBool(_paused);
        writer.WriteBool(_modeMask);
        writer.WriteBool(_intermittent);
        writer.WriteBool(_path3Masked);
        WriteQword(writer, _lastTag);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        foreach (var p in _paths)
        {
            p.InPacket = reader.ReadBool();
            p.HaveTag = reader.ReadBool();
            p.Nloop = reader.ReadInt32();
            p.Eop = reader.ReadBool();
            p.Flg = reader.ReadInt32();
            p.Nreg = reader.ReadInt32();
            p.Regs = reader.ReadUInt64();
            p.RegIndex = reader.ReadInt32();
            p.Q = BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
            p.ImageQwords = reader.ReadInt32();
            p.Tag = ReadQword(reader);
        }

        _fifoHead = 0;
        _fifoCount = reader.ReadInt32();
        if ((uint)_fifoCount > Path3FifoSize)
        {
            throw new StateFormatException("The GIF's PATH3 FIFO is out of range.");
        }

        for (var i = 0; i < _fifoCount; i++)
        {
            _fifo[i] = ReadQword(reader);
        }

        _path1Read = 0;
        _path1Count = reader.ReadInt32();
        if ((uint)_path1Count > Path1Capacity)
        {
            throw new StateFormatException("The GIF's PATH1 queue is out of range.");
        }

        for (var i = 0; i < _path1Count; i++)
        {
            _path1[i] = ReadQword(reader);
        }

        _owner = reader.ReadInt32();
        _paused = reader.ReadBool();
        _modeMask = reader.ReadBool();
        _intermittent = reader.ReadBool();
        _path3Masked = reader.ReadBool();
        _lastTag = ReadQword(reader);
        reader.EndChunk();
    }

    private static void WriteQword(StateWriter writer, UInt128 value)
    {
        writer.WriteUInt64((ulong)value);
        writer.WriteUInt64((ulong)(value >> 64));
    }

    private static UInt128 ReadQword(StateReader reader)
    {
        var lo = reader.ReadUInt64();
        var hi = reader.ReadUInt64();
        return new UInt128(hi, lo);
    }
}
