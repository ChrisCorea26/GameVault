using System.Runtime.InteropServices;

namespace GameVault.Core.PlayStation2;

// The channels' transfer engines (ps2tek: "DMAC Chain Mode", "DMAC MFIFO"). Ported in part from Play!
// (BSD-2-Clause): Source/ee/Dmac_Channel.cpp (ExecuteNormal, ExecuteInterleave, ExecuteSourceChain,
// ExecuteDestinationChain).
public sealed partial class Ps2Dmac
{
    /// <summary>The most quadwords a channel moves before the next active channel gets the bus.</summary>
    public const int SliceQuadwords = 16;

    // Source chain tag IDs.
    private const uint TagRefe = 0;
    private const uint TagCnt = 1;
    private const uint TagNext = 2;
    private const uint TagRef = 3;
    private const uint TagRefs = 4;
    private const uint TagCall = 5;
    private const uint TagRet = 6;
    private const uint TagEnd = 7;

    // Destination chain tag IDs (ps2autotests' dmatags.h; ps2tek's table swaps CNT and CNTS).
    private const uint DestTagCnts = 0;

    /// <summary>
    /// Runs the active channels for <paramref name="busCycles"/> bus cycles: about one quadword (or one DMAtag) per
    /// cycle, unused time is lost (the bus is idle).
    /// </summary>
    public void Advance(long busCycles)
    {
        if (!Running)
        {
            return;
        }

        var budget = busCycles;
        var progress = true;
        while (budget > 0 && progress)
        {
            progress = false;
            for (var ch = 0; ch < ChannelCount && budget > 0; ch++)
            {
                if (!IsActive(ch))
                {
                    continue;
                }

                var cost = Step(ch, (int)Math.Min(budget, SliceQuadwords));
                if (cost > 0)
                {
                    budget -= cost;
                    progress = true;
                }
            }
        }
    }

    private bool IsActive(int ch) =>
        (_chcr[ch] & ChcrStr) != 0 && ((_pcr & PcrPce) == 0 || (_pcr & (1u << (16 + ch))) != 0);

    private uint Mode(int ch) => (_chcr[ch] >> ChcrModShift) & 3;

    private uint TagId(int ch) => (_chcr[ch] >> 28) & 7;

    private static bool IsDestinationChain(int ch) => ch is Sif0Channel or SprFromChannel;

    private bool ToMemory(int ch) => ch switch
    {
        // DIR matters only to VIF1 and SIF2, and only in normal mode: chains always read memory (as Play! does).
        Vif1Channel or Sif2Channel => (_chcr[ch] & ChcrDir) == 0 && Mode(ch) == 0,
        IpuFromChannel or Sif0Channel or SprFromChannel => true,
        _ => false,
    };

    // D_CTRL.MFD: 2 = VIF1 drains the MFIFO, 3 = the GIF does; SPR_FROM fills it.
    private int MfifoDrain => ((_ctrl >> 2) & 3) switch
    {
        2 => Vif1Channel,
        3 => GifChannel,
        _ => -1,
    };

    // D_CTRL.STS: the channel whose writes move D_STADR.
    private int StallSource => ((_ctrl >> 4) & 3) switch
    {
        1 => Sif0Channel,
        2 => SprFromChannel,
        3 => IpuFromChannel,
        _ => -1,
    };

    // D_CTRL.STD: the channel that may not read past D_STADR (with REFS tags).
    private int StallDrain => ((_ctrl >> 6) & 3) switch
    {
        1 => Vif1Channel,
        2 => GifChannel,
        3 => Sif1Channel,
        _ => -1,
    };

    // The MFIFO ring: D_RBOR's base with the offset bits D_RBSR selects.
    private uint Ring(uint address) => (_rbor & ~_rbsr) | (address & _rbsr);

    /// <summary>One turn of a channel; returns the bus cycles used (0 when it could do nothing).</summary>
    private int Step(int ch, int max)
    {
        var mode = Mode(ch);
        if (mode is 1 or 3 && ch is not (IpuFromChannel or Sif2Channel))
        {
            return IsDestinationChain(ch) ? StepDestinationChain(ch, max) : StepSourceChain(ch, max);
        }

        if (mode == 2 && ch >= SprFromChannel && (_sqwc & 0xFF) != 0 && ((_sqwc >> 16) & 0xFF) != 0)
        {
            return StepInterleave(ch, max);
        }

        return StepNormal(ch, max);
    }

    private void Finish(int ch)
    {
        _chcr[ch] &= ~ChcrStr;
        _stat |= 1u << ch;
    }

    private int StepNormal(int ch, int max)
    {
        if (_qwc[ch] == 0)
        {
            // Nothing to move (ps2autotests' spr/normal finds zero-size transfers inconsistent; this ends them).
            Finish(ch);
            return 1;
        }

        var ring = ch == SprFromChannel && MfifoDrain >= 0;
        var moved = Move(ch, (int)Math.Min(_qwc[ch], (uint)max), ring);
        if (_qwc[ch] == 0)
        {
            Finish(ch);
        }

        return moved;
    }

    // Interleave (SPR channels): TQWC quadwords, then SQWC skipped in memory, the scratchpad side contiguous.
    private int StepInterleave(int ch, int max)
    {
        var skip = _sqwc & 0xFF;
        var block = (_sqwc >> 16) & 0xFF;
        var cost = 0;
        while (cost < max && (_chcr[ch] & ChcrStr) != 0)
        {
            if (_qwc[ch] == 0)
            {
                Finish(ch);
                return Math.Max(cost, 1);
            }

            var n = (int)Math.Min(Math.Min(block - _interleaveCount[ch], _qwc[ch]), (uint)(max - cost));
            var moved = Move(ch, n, ring: false);
            cost += moved;
            _interleaveCount[ch] += (uint)moved;
            if (_interleaveCount[ch] >= block)
            {
                _madr[ch] += skip * 16;
                _interleaveCount[ch] = 0;
            }

            if (moved == 0)
            {
                break;
            }
        }

        return cost;
    }

    private int StepSourceChain(int ch, int max)
    {
        var cost = 0;
        var drain = MfifoDrain == ch;
        while (cost < max && (_chcr[ch] & ChcrStr) != 0)
        {
            if (_qwc[ch] > 0)
            {
                var id = TagId(ch);
                var limit = (int)Math.Min(_qwc[ch], (uint)(max - cost));
                if (StallDrain == ch && id == TagRefs)
                {
                    // Stall control: a REFS transfer may not read past what the stall source has written.
                    var available = _stadr > _madr[ch] ? (int)((_stadr - _madr[ch]) / 16) : 0;
                    if (available == 0)
                    {
                        if (!_stallReported[ch])
                        {
                            _stallReported[ch] = true;
                            _stat |= StatSis;
                        }

                        return cost;
                    }

                    _stallReported[ch] = false;
                    limit = Math.Min(limit, available);
                }

                var moved = Move(ch, limit, ring: drain && id is not (TagRef or TagRefs or TagRefe));
                cost += moved;
                if (moved == 0)
                {
                    return cost;
                }

                continue;
            }

            if (_initTransfer[ch])
            {
                _initTransfer[ch] = false;
            }
            else if (_endAfter[ch])
            {
                Finish(ch);
                return Math.Max(cost, 1);
            }

            if (drain)
            {
                // MFIFO: the tag is read from the ring, and the drain waits while it has caught up with SPR_FROM.
                _tadr[ch] = Ring(_tadr[ch]);
                if (_tadr[ch] == _madr[SprFromChannel])
                {
                    if (!_stallReported[ch])
                    {
                        _stallReported[ch] = true;
                        _stat |= StatMeis;
                    }

                    return cost;
                }

                _stallReported[ch] = false;
            }

            var tag = ReadQuadword(_tadr[ch]);
            if ((_chcr[ch] & ChcrTte) != 0 && !SendTag(ch, tag))
            {
                return cost;
            }

            ApplySourceTag(ch, (ulong)tag, drain);
            cost++;
        }

        return cost;
    }

    private void ApplySourceTag(int ch, ulong tag, bool drain)
    {
        var qwc = (uint)tag & 0xFFFF;
        var id = (uint)(tag >> 28) & 7;
        var address = (uint)(tag >> 32) & 0xFFFF_FFF0;
        var irq = (tag & 0x8000_0000) != 0;
        var tadr = _tadr[ch];
        var end = false;

        _chcr[ch] = (_chcr[ch] & 0xFFFF) | ((uint)tag & 0xFFFF_0000);
        ApplyPriorityControl(tag);

        switch (id)
        {
            case TagRefe:
                _madr[ch] = address;
                _tadr[ch] = tadr + 16;
                end = true;
                break;
            case TagCnt:
                _madr[ch] = tadr + 16;
                _tadr[ch] = tadr + 16 + (qwc * 16);
                break;
            case TagNext:
                _madr[ch] = tadr + 16;
                _tadr[ch] = address;
                break;
            case TagRef:
            case TagRefs:
                _madr[ch] = address;
                _tadr[ch] = tadr + 16;
                break;
            case TagCall:
            {
                _madr[ch] = tadr + 16;
                var asp = (_chcr[ch] & ChcrAsp) >> ChcrAspShift;
                if (asp >= 2)
                {
                    // The address stack holds two; a third CALL ends the transfer instead (undefined on hardware).
                    end = true;
                    break;
                }

                if (asp == 0)
                {
                    _asr0[ch] = tadr + 16 + (qwc * 16);
                }
                else
                {
                    _asr1[ch] = tadr + 16 + (qwc * 16);
                }

                _chcr[ch] = (_chcr[ch] & ~ChcrAsp) | ((asp + 1) << ChcrAspShift);
                _tadr[ch] = address;
                break;
            }

            case TagRet:
            {
                _madr[ch] = tadr + 16;
                var asp = (_chcr[ch] & ChcrAsp) >> ChcrAspShift;
                if (asp == 0)
                {
                    end = true;
                }
                else
                {
                    _tadr[ch] = asp == 2 ? _asr1[ch] : _asr0[ch];
                    _chcr[ch] = (_chcr[ch] & ~ChcrAsp) | ((asp - 1) << ChcrAspShift);
                }

                break;
            }

            default: // END
                _madr[ch] = tadr + 16;
                end = true;
                break;
        }

        if (drain && id is not (TagRef or TagRefs or TagRefe))
        {
            _madr[ch] = Ring(_madr[ch]);
        }

        _qwc[ch] = qwc;
        _endAfter[ch] = end || (irq && (_chcr[ch] & ChcrTie) != 0);
    }

    private int StepDestinationChain(int ch, int max)
    {
        var cost = 0;
        while (cost < max && (_chcr[ch] & ChcrStr) != 0)
        {
            if (_qwc[ch] > 0)
            {
                var ring = ch == SprFromChannel && MfifoDrain >= 0;
                var moved = Move(ch, (int)Math.Min(_qwc[ch], (uint)(max - cost)), ring);
                cost += moved;
                if (moved == 0)
                {
                    return cost;
                }

                continue;
            }

            if (_initTransfer[ch])
            {
                _initTransfer[ch] = false;
            }
            else if (_endAfter[ch])
            {
                Finish(ch);
                return Math.Max(cost, 1);
            }

            // The tag comes from the source: SIF0's FIFO (the IOP sends it ahead of the data, as a whole quadword with
            // the tag in its low 64 bits) or the scratchpad at SADR.
            ulong tag;
            if (ch == Sif0Channel)
            {
                if (!_sif.Sif0.TryPop(out UInt128 quadword))
                {
                    return cost;
                }

                tag = (ulong)quadword;
            }
            else
            {
                tag = (ulong)ReadScratchpad(_sadr[ch]);
                _sadr[ch] = (_sadr[ch] + 16) & SadrMask;
            }

            var id = (uint)(tag >> 28) & 7;
            _chcr[ch] = (_chcr[ch] & 0xFFFF) | ((uint)tag & 0xFFFF_0000);
            ApplyPriorityControl(tag);
            _madr[ch] = (uint)(tag >> 32) & 0xFFFF_FFF0;
            _qwc[ch] = (uint)tag & 0xFFFF;
            _endAfter[ch] = id == TagEnd || ((tag & 0x8000_0000) != 0 && (_chcr[ch] & ChcrTie) != 0);
            cost++;
        }

        return cost;
    }

    // A tag's PCE field: 2 clears D_PCR.PCE, 3 sets it (ps2tek).
    private void ApplyPriorityControl(ulong tag)
    {
        switch ((tag >> 26) & 3)
        {
            case 2:
                _pcr &= ~PcrPce;
                break;
            case 3:
                _pcr |= PcrPce;
                break;
        }
    }

    // TTE: the tag goes to the peer before its data. VIF0/VIF1 take its upper 64 bits as two VIFcodes and SIF1 as two
    // words (the IOP's tag); SPR_TO stores the whole quadword; the GIF and IPU don't take it.
    private bool SendTag(int ch, UInt128 tag)
    {
        switch (ch)
        {
            case Vif0Channel:
                return _vif0.WriteTag(tag);
            case Vif1Channel:
                return _vif1.WriteTag(tag);
            case Sif1Channel:
                if (_sif.Sif1.Free < 2)
                {
                    return false;
                }

                _sif.Sif1.TryPush((uint)(tag >> 64));
                _sif.Sif1.TryPush((uint)(tag >> 96));
                return true;
            case SprToChannel:
                WriteScratchpad(_sadr[ch], tag);
                _sadr[ch] = (_sadr[ch] + 16) & SadrMask;
                return true;
            default:
                return true;
        }
    }

    /// <summary>Moves up to <paramref name="count"/> quadwords between MADR and the channel's peer; returns how many.</summary>
    private int Move(int ch, int count, bool ring)
    {
        count = Math.Min(count, SliceQuadwords);
        if (count <= 0)
        {
            return 0;
        }

        Span<UInt128> buffer = stackalloc UInt128[SliceQuadwords];
        var data = buffer[..count];
        var madr = _madr[ch];
        int moved;
        if (ToMemory(ch))
        {
            moved = Receive(ch, data);
            for (var i = 0; i < moved; i++)
            {
                var address = madr + (uint)(i * 16);
                WriteQuadword(ring ? Ring(address) : address, data[i]);
            }

            if (StallSource == ch && (Mode(ch) is 0 or 2 || TagId(ch) == DestTagCnts))
            {
                _stadr = madr + (uint)(moved * 16);
            }
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var address = madr + (uint)(i * 16);
                data[i] = ReadQuadword(ring ? Ring(address) : address);
            }

            moved = Send(ch, data);
        }

        madr += (uint)(moved * 16);
        _madr[ch] = ring ? Ring(madr) : madr;
        _qwc[ch] -= (uint)moved;
        return moved;
    }

    private int Send(int ch, ReadOnlySpan<UInt128> data)
    {
        switch (ch)
        {
            case Vif0Channel:
                return _vif0.Write(data);
            case Vif1Channel:
                return _vif1.Write(data);
            case GifChannel:
                return _gif.WritePath3(data);
            case IpuToChannel:
                return Ipu?.WriteIn(data) ?? 0;
            case Sif1Channel:
            {
                var n = 0;
                while (n < data.Length && _sif.Sif1.TryPush(data[n]))
                {
                    n++;
                }

                return n;
            }

            case SprToChannel:
                for (var i = 0; i < data.Length; i++)
                {
                    WriteScratchpad(_sadr[ch], data[i]);
                    _sadr[ch] = (_sadr[ch] + 16) & SadrMask;
                }

                return data.Length;
            default:
                // SIF2 (PS1 mode and debugging) has no peer here.
                return 0;
        }
    }

    private int Receive(int ch, Span<UInt128> data)
    {
        switch (ch)
        {
            case Vif1Channel:
                return _vif1.Read(data);
            case IpuFromChannel:
                return Ipu?.ReadOut(data) ?? 0;
            case Sif0Channel:
            {
                var n = 0;
                while (n < data.Length && _sif.Sif0.TryPop(out data[n]))
                {
                    n++;
                }

                return n;
            }

            case SprFromChannel:
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = ReadScratchpad(_sadr[ch]);
                    _sadr[ch] = (_sadr[ch] + 16) & SadrMask;
                }

                return data.Length;
            default:
                return 0;
        }
    }

    // ---- Memory: RDRAM, or the scratchpad when bit 31 is set ----

    private Span<byte> QuadwordAt(uint address) =>
        (address & ScratchpadBit) != 0
            ? _scratchpad.AsSpan((int)(address & SadrMask), 16)
            : _rdram.AsSpan((int)(address & _ramMask & ~0xFu), 16);

    private UInt128 ReadQuadword(uint address) => MemoryMarshal.Read<UInt128>(QuadwordAt(address));

    private void WriteQuadword(uint address, UInt128 value) => MemoryMarshal.Write(QuadwordAt(address), in value);

    private UInt128 ReadScratchpad(uint sadr) => MemoryMarshal.Read<UInt128>(_scratchpad.AsSpan((int)(sadr & SadrMask), 16));

    private void WriteScratchpad(uint sadr, UInt128 value) =>
        MemoryMarshal.Write(_scratchpad.AsSpan((int)(sadr & SadrMask), 16), in value);
}
