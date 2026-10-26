using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// VIF0 or VIF1, the vector units' interfaces (ps2tek: "VIF I/O Registers", "VIF Commands", "VIF UNPACK"): a FIFO fed by
/// DMA channel 0/1 or by direct writes to $10004000/$10005000, decoding VIFcodes that unpack vectors into VU data memory,
/// upload micro programs, start them, and (VIF1) pass GIF packets to PATH2. The registers are at $10003800 (VIF0) and
/// $10003C00 (VIF1).
/// </summary>
/// <remarks>
/// The FIFO holds 8 quadwords on VIF0 and 16 on VIF1 (ps2tek's FQC) as 32-bit words, behind the decoder's own buffer of
/// two quadwords (<see cref="DecodeBufferWords"/>), which STAT.FQC doesn't count. Commands run as soon as their data
/// is in the FIFO (no per-word timing). A command that has to wait — for its data, for the VU to finish (FLUSHE, MSCAL,
/// MPG…), for the GIF (DIRECT, FLUSH…) — leaves the rest of the FIFO in place; <see cref="Write"/> then accepts only what
/// fits and the DMAC retries later. The console calls <see cref="Resume"/> when a VU stops or the GIF frees PATH2 so
/// that data written directly to the FIFO goes on. Behaviour follows ps2tek and Play! (BSD-2-Clause: Source/ee/Vif.cpp,
/// Vif1.cpp), with the details ps2autotests' dma/vif programs recorded on hardware (V2's z/w, STMOD mode 3, the I bit).
/// </remarks>
public sealed partial class Ps2Vif : IStateful
{
    // VIFn_STAT bits (ps2tek).
    private const uint StatVew = 1 << 2;
    private const uint StatVgw = 1 << 3;
    private const uint StatMrk = 1 << 6;
    private const uint StatDbf = 1 << 7;
    private const uint StatVss = 1 << 8;
    private const uint StatVfs = 1 << 9;
    private const uint StatVis = 1 << 10;
    private const uint StatInt = 1 << 11;
    private const uint StatEr0 = 1 << 12;
    private const uint StatEr1 = 1 << 13;
    private const uint StatFdr = 1 << 23;

    // VIFn_FBRST bits.
    private const uint FbrstRst = 1 << 0;
    private const uint FbrstFbk = 1 << 1;
    private const uint FbrstStp = 1 << 2;
    private const uint FbrstStc = 1 << 3;

    // VIFn_ERR bits.
    private const uint ErrMii = 1 << 0;
    private const uint ErrMe1 = 1 << 2;

    /// <summary>
    /// The decoder's input buffer in front of the FIFO: 8 words, refilled from the FIFO as the decoder takes words, and not
    /// counted by FQC. ps2autotests (dma/vif/intstall) recorded FQC = 0 on hardware after a 3-quadword packet stalled on
    /// an I bit in its first quadword, its transfer complete: the 9 words left fit in this buffer and the FIFO's one
    /// word isn't a whole quadword. (The size is DobieStation's model of the VIF, read for facts only; it is the one that
    /// matches the recorded result.)
    /// </summary>
    public const int DecodeBufferWords = 8;

    private readonly int _index;
    private readonly IPs2VectorUnit _vu;
    private readonly Ps2Intc _intc;
    private readonly IPs2Gif? _gif;
    private readonly StateTag _tag;

    // The FIFO, as a ring of 32-bit words. _byteOffset is UNPACK's position inside the head word.
    private readonly uint[] _fifo;
    private int _fifoHead;
    private int _fifoCount;
    private int _byteOffset;

    // Registers.
    private uint _stat;
    private uint _err;
    private uint _mark;
    private uint _cycle;
    private uint _mode;
    private int _num;
    private uint _mask;
    private uint _code;
    private uint _itops;
    private uint _itop;
    private uint _base;
    private uint _ofst;
    private uint _tops;
    private uint _top;
    private readonly uint[] _row = new uint[4];
    private readonly uint[] _col = new uint[4];

    // The command being executed.
    private bool _inCommand;
    private bool _waitVu;
    private bool _waitGif;
    private bool _stopRequested;
    private bool _irqPending;
    private uint _readTick;
    private uint _writeTick;
    private uint _unpackAddress;
    private int _directRemaining;
    private int _mpgWordsDone;

    /// <param name="index">0 for VIF0, 1 for VIF1.</param>
    /// <param name="vu">The vector unit this VIF feeds.</param>
    /// <param name="intc">Where the VIF0/VIF1 interrupt goes.</param>
    /// <param name="gif">VIF1's GIF, for DIRECT/DIRECTHL (PATH2), MSKPATH3, FLUSH/FLUSHA and downloads.</param>
    public Ps2Vif(int index, IPs2VectorUnit vu, Ps2Intc intc, IPs2Gif? gif = null)
    {
        if (index is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _index = index;
        _vu = vu;
        _intc = intc;
        _gif = gif;
        _tag = StateTag.From(index == 0 ? "VIF0" : "VIF1");
        _fifo = new uint[FifoQuadwords * 4 + DecodeBufferWords];
        Reset();
    }

    /// <summary>The FIFO's size in quadwords: 8 on VIF0, 16 on VIF1.</summary>
    public int FifoQuadwords => _index == 0 ? 8 : 16;

    /// <summary>VIF1's TOP, for VU1's XTOP (0 on VIF0).</summary>
    public uint Top => _top;

    /// <summary>ITOP, for the VU's XITOP.</summary>
    public uint Itop => _itop;

    /// <summary>The quadwords waiting in the FIFO and the decoder's buffer (rounded up).</summary>
    public int FifoCount => (_fifoCount + 3) / 4;

    /// <summary>STAT.FQC: the whole quadwords in the FIFO behind the decoder's buffer.</summary>
    public int FifoQuadwordCount => Math.Max(0, _fifoCount - DecodeBufferWords) / 4;

    /// <summary>
    /// The VIF can't go on by itself: it is waiting for the VU or the GIF, or is stalled (I bit, STOP, force break, an
    /// invalid command) — data may still sit in the FIFO.
    /// </summary>
    public bool Stalled => _waitVu || _waitGif || (_stat & (StatVis | StatVss | StatVfs)) != 0 || ErrorStall;

    private bool IsVif1 => _index == 1;

    private bool ErrorStall => (_stat & StatEr1) != 0 && (_err & ErrMe1) == 0;

    public void Reset()
    {
        ResetEngine();
        _err = _mark = _mode = _mask = _code = 0;
        _cycle = 0x0101;
        _itops = _itop = _base = _ofst = _tops = _top = 0;
        Array.Clear(_row);
        Array.Clear(_col);
    }

    // FBRST.RST: the FIFO, STAT and the command in progress (ps2tek: "Reset VIF including contents of FIFO").
    private void ResetEngine()
    {
        _fifoHead = _fifoCount = _byteOffset = 0;
        _stat = 0;
        _num = 0;
        _inCommand = _waitVu = _waitGif = _stopRequested = _irqPending = false;
        _readTick = _writeTick = _unpackAddress = 0;
        _directRemaining = _mpgWordsDone = 0;
    }

    // ---- FIFO input ----

    /// <summary>
    /// Feeds quadwords into the FIFO (from DMA, or a direct write to $10004000/$10005000) and runs the commands they
    /// complete. Returns how many quadwords were taken: fewer than given when the FIFO fills because the VIF waits.
    /// </summary>
    public int Write(ReadOnlySpan<UInt128> data)
    {
        Process();
        var accepted = 0;
        while (accepted < data.Length)
        {
            var room = (_fifo.Length - _fifoCount) / 4;
            if (room == 0)
            {
                break;
            }

            var n = Math.Min(room, data.Length - accepted);
            for (var i = 0; i < n; i++)
            {
                var q = data[accepted + i];
                Push((uint)q);
                Push((uint)(q >> 32));
                Push((uint)(q >> 64));
                Push((uint)(q >> 96));
            }

            accepted += n;
            Process();
        }

        return accepted;
    }

    /// <summary>
    /// A DMAtag sent with the channel's TTE bit: its upper 64 bits go into the FIFO as two VIFcodes (ps2tek: "DMAC Chain
    /// Mode"). Returns false, taking nothing, when the FIFO has no room.
    /// </summary>
    public bool WriteTag(UInt128 tag)
    {
        Process();
        if (_fifo.Length - _fifoCount < 2)
        {
            return false;
        }

        Push((uint)(tag >> 64));
        Push((uint)(tag >> 96));
        Process();
        return true;
    }

    /// <summary>
    /// VIF1 with STAT.FDR set (local-to-host): reads quadwords the GS has ready through the GIF for VIF1's DMA (channel 1
    /// with CHCR.DIR = 0). Returns how many were read.
    /// </summary>
    public int Read(Span<UInt128> destination)
    {
        if (!IsVif1 || (_stat & StatFdr) == 0 || _gif is null)
        {
            return 0;
        }

        return _gif.ReadDownload(destination);
    }

    /// <summary>Runs whatever the FIFO holds again, after the VU stopped or the GIF freed a path.</summary>
    public void Resume() => Process();

    private void Push(uint word)
    {
        _fifo[(_fifoHead + _fifoCount) % _fifo.Length] = word;
        _fifoCount++;
    }

    private uint Pop()
    {
        var word = _fifo[_fifoHead];
        _fifoHead = (_fifoHead + 1) % _fifo.Length;
        _fifoCount--;
        return word;
    }

    // ---- Registers ----

    /// <summary>Reads a register; <paramref name="offset"/> is from $10003800 (VIF0) or $10003C00 (VIF1).</summary>
    public uint ReadRegister(uint offset)
    {
        switch (offset & 0x3F0)
        {
            case 0x00:
                return ReadStat();
            case 0x20:
                return _err;
            case 0x30:
                return _mark;
            case 0x40:
                return _cycle;
            case 0x50:
                return _mode;
            case 0x60:
                // ps2autotests (dma/vif/unpack): with 256 left VIF0 reads 0 and VIF1 reads 256.
                return IsVif1 ? (uint)_num : (uint)_num & 0xFF;
            case 0x70:
                return _mask;
            case 0x80:
                return _code;
            case 0x90:
                return _itops;
            case 0xA0:
                return IsVif1 ? _base : 0;
            case 0xB0:
                return IsVif1 ? _ofst : 0;
            case 0xC0:
                return IsVif1 ? _tops : 0;
            case 0xD0:
                return _itop;
            case 0xE0:
                return IsVif1 ? _top : 0;
            case >= 0x100 and < 0x140:
                return _row[((offset & 0x3F0) - 0x100) >> 4];
            case >= 0x140 and < 0x180:
                return _col[((offset & 0x3F0) - 0x140) >> 4];
            default:
                return 0;
        }
    }

    public void WriteRegister(uint offset, uint value)
    {
        switch (offset & 0x3F0)
        {
            case 0x00:
                // Only VIF1's FDR (the FIFO's direction) is writable.
                if (IsVif1)
                {
                    _stat = (_stat & ~StatFdr) | (value & StatFdr);
                }

                break;
            case 0x10:
                WriteFbrst(value);
                break;
            case 0x20:
                _err = value & 7;
                Process();
                break;
            case 0x30:
                _mark = value & 0xFFFF;
                _stat &= ~StatMrk;
                break;
        }
    }

    private uint ReadStat()
    {
        uint vps;
        if (_inCommand)
        {
            vps = _waitVu || _waitGif ? 2u : 1u;
        }
        else
        {
            // ps2autotests (dma/vif/intstall): stalled on an I bit, VPS reads "decoding".
            vps = (_stat & StatVis) != 0 ? 2u : 0u;
        }

        var value = _stat | vps;
        if (_vu.Running)
        {
            value |= StatVew;
        }

        if (_waitGif)
        {
            value |= StatVgw;
        }

        if (!IsVif1)
        {
            value &= ~(StatDbf | StatFdr);
        }

        return value | ((uint)FifoQuadwordCount << 24);
    }

    private void WriteFbrst(uint value)
    {
        if ((value & FbrstRst) != 0)
        {
            ResetEngine();
        }

        if ((value & FbrstFbk) != 0)
        {
            _stat |= StatVfs;
        }

        if ((value & FbrstStp) != 0)
        {
            // Stops after the current command (at once if idle).
            if (_inCommand)
            {
                _stopRequested = true;
            }
            else
            {
                _stat |= StatVss;
            }
        }

        if ((value & FbrstStc) != 0)
        {
            _stat &= ~(StatVss | StatVfs | StatVis | StatInt | StatEr0 | StatEr1);
            _stopRequested = false;
            Process();
        }
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(_tag, 1);
        writer.WriteInt32(_fifoCount);
        for (var i = 0; i < _fifoCount; i++)
        {
            writer.WriteUInt32(_fifo[(_fifoHead + i) % _fifo.Length]);
        }

        writer.WriteInt32(_byteOffset);
        writer.WriteUInt32(_stat);
        writer.WriteUInt32(_err);
        writer.WriteUInt32(_mark);
        writer.WriteUInt32(_cycle);
        writer.WriteUInt32(_mode);
        writer.WriteInt32(_num);
        writer.WriteUInt32(_mask);
        writer.WriteUInt32(_code);
        writer.WriteUInt32(_itops);
        writer.WriteUInt32(_itop);
        writer.WriteUInt32(_base);
        writer.WriteUInt32(_ofst);
        writer.WriteUInt32(_tops);
        writer.WriteUInt32(_top);
        for (var i = 0; i < 4; i++)
        {
            writer.WriteUInt32(_row[i]);
            writer.WriteUInt32(_col[i]);
        }

        writer.WriteBool(_inCommand);
        writer.WriteBool(_waitVu);
        writer.WriteBool(_waitGif);
        writer.WriteBool(_stopRequested);
        writer.WriteBool(_irqPending);
        writer.WriteUInt32(_readTick);
        writer.WriteUInt32(_writeTick);
        writer.WriteUInt32(_unpackAddress);
        writer.WriteInt32(_directRemaining);
        writer.WriteInt32(_mpgWordsDone);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(_tag) != 1)
        {
            throw new StateFormatException("Unsupported VIF state version.");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > _fifo.Length)
        {
            throw new StateFormatException("VIF FIFO count out of range.");
        }

        _fifoHead = 0;
        _fifoCount = 0;
        for (var i = 0; i < count; i++)
        {
            Push(reader.ReadUInt32());
        }

        _byteOffset = reader.ReadInt32() & 3;
        _stat = reader.ReadUInt32();
        _err = reader.ReadUInt32();
        _mark = reader.ReadUInt32();
        _cycle = reader.ReadUInt32();
        _mode = reader.ReadUInt32();
        _num = reader.ReadInt32();
        _mask = reader.ReadUInt32();
        _code = reader.ReadUInt32();
        _itops = reader.ReadUInt32();
        _itop = reader.ReadUInt32();
        _base = reader.ReadUInt32();
        _ofst = reader.ReadUInt32();
        _tops = reader.ReadUInt32();
        _top = reader.ReadUInt32();
        for (var i = 0; i < 4; i++)
        {
            _row[i] = reader.ReadUInt32();
            _col[i] = reader.ReadUInt32();
        }

        _inCommand = reader.ReadBool();
        _waitVu = reader.ReadBool();
        _waitGif = reader.ReadBool();
        _stopRequested = reader.ReadBool();
        _irqPending = reader.ReadBool();
        _readTick = reader.ReadUInt32();
        _writeTick = reader.ReadUInt32();
        _unpackAddress = reader.ReadUInt32();
        _directRemaining = reader.ReadInt32();
        _mpgWordsDone = reader.ReadInt32();
        reader.EndChunk();
    }
}
