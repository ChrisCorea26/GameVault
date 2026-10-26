using System.Buffers.Binary;
using System.Runtime.InteropServices;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The IOP's DMA controller (ps2tek: "IOP DMA"): the PlayStation's channels 0–6 at $1F801080 (DPCR and DICR at
/// $1F8010F0) and the new channels 7–13 at $1F801500 (DPCR2, DICR2, DMACEN and DMACINTEN at $1F801570). Each channel
/// has MADR, BCR, CHCR and TADR; writing CHCR with bit 24 starts it when its DPCR enable bit is set.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>3, the CDVD: each sector the drive reads goes to MADR, until BCR's blocks × words are done.</item>
/// <item>4 and 7, the SPU2's two cores, through <see cref="IPs2Spu2.DmaWrite"/> and <see cref="IPs2Spu2.DmaRead"/>; a
/// transfer the SPU2 can't take at once (AutoDMA) continues as it frees room.</item>
/// <item>9, SIF0 (IOP → EE): IOP tags from TADR (word 0 the address with bit 30 IRQ and bit 31 end, word 1 the word
/// count); with CHCR bit 8 the four words after the tag's two (the EE's destination DMAtag in the first two, the
/// next IOP tag as padding) go into the SIF0 FIFO first, and TADR advances by 16 rather than 8. Each block is padded to
/// whole quadwords with junk words, as the EE reads quadwords.</item>
/// <item>10, SIF1 (EE → IOP): the IOP tags come through the FIFO, four words each (address and flags, word count,
/// two ignored), followed by their data.</item>
/// <item>11 and 12, SIO2's in and out FIFOs, a byte at a time; channel 11 tells SIO2 its block size.</item>
/// <item>6, OTC, clears an ordering table as on the PlayStation; the rest (MDEC, SIF2, PIO, DEV9, 13) have no device
/// and finish at once.</item>
/// </list>
/// A finished channel sets its flag in DICR (0–6) or DICR2 (7–12) when its mask bit is set (SIF0 and SIF1 always,
/// as PCSX2 notes they can't be masked); IRQ 3 is raised when DICR's master flag (bit 31: bit 15, or bit 23 with a
/// flag set) turns on. DMACEN and DMACINTEN are kept but gate nothing. The SIF's FIFO protocol follows PCSX2's
/// Sif0.cpp/Sif1.cpp and ps2tek, read for behaviour only.
/// </remarks>
public sealed partial class Ps2Iop
{
    public const int DmaChannels = 14;

    private const int DmaCdvd = 3;
    private const int DmaSpu2Core0 = 4;
    private const int DmaOtc = 6;
    private const int DmaSpu2Core1 = 7;
    private const int DmaSif0 = 9;
    private const int DmaSif1 = 10;
    private const int DmaSio2In = 11;
    private const int DmaSio2Out = 12;

    private const uint ChcrStart = 0x01000000;

    private readonly uint[] _madr = new uint[DmaChannels];
    private readonly uint[] _bcr = new uint[DmaChannels];
    private readonly uint[] _chcr = new uint[DmaChannels];
    private readonly uint[] _tadr = new uint[DmaChannels];
    private readonly long[] _dmaDoneAt = new long[DmaChannels];
    private readonly bool[] _dmaActive = new bool[DmaChannels];

    private uint _dpcr;
    private uint _dpcr2;
    private uint _dicr;
    private uint _dicr2;
    private uint _dmacen;
    private uint _dmacinten;
    private bool _dmaMaster;

    // Transfers in progress.
    private int _cdvdRemaining; // bytes
    private int _spuRemaining; // halfwords, for the active SPU2 channel waiting on room
    private int _spuChannel = -1;
    private uint _sif0Words;
    private int _sif0Junk;
    private bool _sif0End;
    private uint _sif1Words;
    private bool _sif1End;

    private void ResetDma()
    {
        Array.Clear(_madr);
        Array.Clear(_bcr);
        Array.Clear(_chcr);
        Array.Clear(_tadr);
        Array.Fill(_dmaDoneAt, long.MaxValue);
        Array.Clear(_dmaActive);
        _dpcr = 0x07777777;
        _dpcr2 = 0x07777777;
        _dicr = _dicr2 = 0;
        _dmacen = _dmacinten = 0;
        _dmaMaster = false;
        _cdvdRemaining = 0;
        _spuRemaining = 0;
        _spuChannel = -1;
        _sif0Words = _sif1Words = 0;
        _sif0Junk = 0;
        _sif0End = _sif1End = false;
    }

    /// <summary>The DMA registers' channel for an address in either block, or −1 for the control registers.</summary>
    private static int DmaChannel(uint address, out int register)
    {
        register = (int)(address & 0xF);
        if (address >= 0x1F801500)
        {
            var index = (int)((address - 0x1F801500) >> 4);
            return index < 7 ? 7 + index : -1;
        }

        var low = (int)((address - 0x1F801080) >> 4);
        return low < 7 ? low : -1;
    }

    private uint ReadDma(uint address)
    {
        var channel = DmaChannel(address, out var register);
        if (channel < 0)
        {
            return address switch
            {
                0x1F8010F0 => _dpcr,
                0x1F8010F4 => _dicr,
                0x1F801570 => _dpcr2,
                0x1F801574 => _dicr2,
                0x1F801578 => _dmacen,
                0x1F80157C => _dmacinten,
                _ => 0,
            };
        }

        return register switch
        {
            0 => _madr[channel],
            4 => _bcr[channel],
            8 => _chcr[channel],
            _ => _tadr[channel],
        };
    }

    private void WriteDma(uint address, uint value)
    {
        var channel = DmaChannel(address, out var register);
        if (channel < 0)
        {
            switch (address)
            {
                case 0x1F8010F0:
                    _dpcr = value;
                    StartEnabledChannels();
                    break;
                case 0x1F8010F4:
                    // Flags (24–30) clear on writing 1; the rest take the value.
                    _dicr = (value & 0x00FF807F) | (_dicr & ~value & 0x7F000000);
                    UpdateDmaInterrupt();
                    break;
                case 0x1F801570:
                    _dpcr2 = value;
                    StartEnabledChannels();
                    break;
                case 0x1F801574:
                    _dicr2 = (value & 0x003F1FFF) | (_dicr2 & ~value & 0x3F000000);
                    UpdateDmaInterrupt();
                    break;
                case 0x1F801578:
                    _dmacen = value;
                    break;
                case 0x1F80157C:
                    _dmacinten = value;
                    break;
            }

            return;
        }

        switch (register)
        {
            case 0:
                _madr[channel] = value & 0xFFFFFF;
                break;
            case 4:
                _bcr[channel] = value;
                break;
            case 8:
                _chcr[channel] = value;
                if ((value & ChcrStart) != 0)
                {
                    TryStartDma(channel);
                }

                break;
            default:
                _tadr[channel] = value & 0xFFFFFF;
                break;
        }
    }

    private bool DmaEnabled(int channel)
    {
        if (channel < 7)
        {
            return (_dpcr & (8u << (channel * 4))) != 0;
        }

        return (_dpcr2 & (8u << ((channel - 7) * 4))) != 0;
    }

    private void StartEnabledChannels()
    {
        for (var channel = 0; channel < DmaChannels; channel++)
        {
            if ((_chcr[channel] & ChcrStart) != 0 && !_dmaActive[channel])
            {
                TryStartDma(channel);
            }
        }
    }

    /// <summary>The transfer's size in BCR's terms: blocks × words (a block count of 0 is one block).</summary>
    private int DmaWords(int channel)
    {
        var bcr = _bcr[channel];
        var size = (int)(bcr & 0xFFFF);
        var blocks = (int)(bcr >> 16);
        return Math.Max(1, blocks) * (size == 0 ? 0x10000 : size);
    }

    private void TryStartDma(int channel)
    {
        if (_dmaActive[channel] || !DmaEnabled(channel))
        {
            return;
        }

        _dmaActive[channel] = true;
        var now = Cpu.Cycles;
        switch (channel)
        {
            case DmaCdvd:
                _cdvdRemaining = DmaWords(channel) * 4;
                ServiceCdvdDma(now);
                break;
            case DmaSpu2Core0:
            case DmaSpu2Core1:
                StartSpuDma(channel, now);
                break;
            case DmaSif0:
                _sif0Words = 0;
                _sif0Junk = 0;
                _sif0End = false;
                ServiceSif0();
                break;
            case DmaSif1:
                _sif1Words = 0;
                _sif1End = false;
                ServiceSif1();
                break;
            case DmaSio2In:
            {
                var blockBytes = (int)(_bcr[channel] & 0xFFFF) * 4;
                var bytes = DmaWords(channel) * 4;
                Sio2.BeginDmaIn(blockBytes);
                var madr = _madr[channel];
                for (var i = 0; i < bytes; i++)
                {
                    Sio2.WriteFifo(Ram[(madr + (uint)i) & (RamSize - 1)]);
                }

                _madr[channel] = (madr + (uint)bytes) & 0xFFFFFF;
                FinishDmaAt(channel, now + Math.Max(1, bytes / 4));
                break;
            }

            case DmaSio2Out:
            {
                var bytes = DmaWords(channel) * 4;
                var madr = _madr[channel];
                for (var i = 0; i < bytes; i++)
                {
                    Ram[(madr + (uint)i) & (RamSize - 1)] = Sio2.ReadFifo();
                }

                _madr[channel] = (madr + (uint)bytes) & 0xFFFFFF;
                FinishDmaAt(channel, now + Math.Max(1, bytes / 4));
                break;
            }

            case DmaOtc:
            {
                // The ordering table, built backwards from MADR: each entry points at the one before (psx-spx: DMA6).
                var address = _madr[channel] & 0x1FFFFC;
                var count = (int)(_bcr[channel] & 0xFFFF);
                count = count == 0 ? 0x10000 : count;
                for (var i = 0; i < count; i++)
                {
                    var value = i == count - 1 ? 0xFFFFFFu : (address - 4) & 0x1FFFFF;
                    BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan((int)address), value);
                    address = (address - 4) & 0x1FFFFC;
                }

                FinishDmaAt(channel, now + count);
                break;
            }

            default:
                FinishDmaAt(channel, now + 1);
                break;
        }

        Sync();
    }

    private void FinishDmaAt(int channel, long cycle) => _dmaDoneAt[channel] = Math.Min(_dmaDoneAt[channel], cycle);

    /// <summary>Ends a channel's transfer: CHCR's start bit clears and its interrupt flag is raised.</summary>
    private void FinishDma(int channel)
    {
        _dmaActive[channel] = false;
        _dmaDoneAt[channel] = long.MaxValue;
        _chcr[channel] &= ~0x11000000u;
        if (channel < 7)
        {
            _bcr[channel] &= 0xFFFF;
            if ((_dicr & (1u << (16 + channel))) != 0)
            {
                _dicr |= 1u << (24 + channel);
            }
        }
        else
        {
            var index = channel - 7;
            if (index < 6 && ((_dicr2 & (1u << (16 + index))) != 0 || channel is DmaSif0 or DmaSif1))
            {
                _dicr2 |= 1u << (24 + index);
            }
        }

        UpdateDmaInterrupt();
    }

    private void UpdateDmaInterrupt()
    {
        var flags = ((_dicr >> 24) & 0x7F) | ((_dicr2 >> 24) & 0x3F);
        var master = (_dicr & 0x8000) != 0 || ((_dicr & 0x800000) != 0 && flags != 0);
        _dicr = master ? _dicr | 0x80000000 : _dicr & 0x7FFFFFFF;
        if (master && !_dmaMaster)
        {
            RaiseInterrupt(Ps2IopInterrupt.Dma);
        }

        _dmaMaster = master;
    }

    private long NextDmaEvent()
    {
        var next = long.MaxValue;
        foreach (var at in _dmaDoneAt)
        {
            next = Math.Min(next, at);
        }

        return next;
    }

    /// <summary>Finishes channels whose time has come and lets waiting ones move data.</summary>
    private void ServiceDma(long now)
    {
        for (var channel = 0; channel < DmaChannels; channel++)
        {
            if (_dmaDoneAt[channel] <= now)
            {
                FinishDma(channel);
            }
        }

        if (_dmaActive[DmaCdvd] || Cdvd.SectorReady)
        {
            ServiceCdvdDma(now);
        }

        if (_spuChannel >= 0)
        {
            ContinueSpuDma(now);
        }

        if (_dmaActive[DmaSif0])
        {
            ServiceSif0();
        }

        if (_dmaActive[DmaSif1])
        {
            ServiceSif1();
        }
    }

    // ---- The CDVD (channel 3) ----

    private void ServiceCdvdDma(long now)
    {
        Cdvd.Advance(now);
        while (_dmaActive[DmaCdvd] && _dmaDoneAt[DmaCdvd] == long.MaxValue && Cdvd.SectorReady)
        {
            var sector = Cdvd.Sector;
            var length = Math.Min(sector.Length, _cdvdRemaining);
            CopyToRam(_madr[DmaCdvd], sector[..length]);
            _madr[DmaCdvd] = (_madr[DmaCdvd] + (uint)length) & 0xFFFFFF;
            _cdvdRemaining -= length;
            Cdvd.TakeSector(now);
            if (_cdvdRemaining <= 0)
            {
                FinishDmaAt(DmaCdvd, now + 1);
            }

            Cdvd.Advance(now);
        }
    }

    /// <summary>The drive's TOC goes straight to channel 3's address, ending the channel if it was started.</summary>
    private void CdvdDirectTransfer(ReadOnlySpan<byte> data)
    {
        CopyToRam(_madr[DmaCdvd], data);
        if (_dmaActive[DmaCdvd])
        {
            FinishDmaAt(DmaCdvd, Cpu.Cycles + 1);
        }
    }

    private void CopyToRam(uint address, ReadOnlySpan<byte> data)
    {
        var offset = (int)(address & (RamSize - 1));
        var first = Math.Min(data.Length, RamSize - offset);
        data[..first].CopyTo(Ram.AsSpan(offset));
        data[first..].CopyTo(Ram);
    }

    // ---- The SPU2 (channels 4 and 7) ----

    private void StartSpuDma(int channel, long now)
    {
        var core = channel == DmaSpu2Core0 ? 0 : 1;
        var halfwords = DmaWords(channel) * 2;
        if (Spu2 is not { } spu)
        {
            FinishDmaAt(channel, now + halfwords / 2);
            return;
        }

        if ((_chcr[channel] & 1) == 0)
        {
            // From the SPU2 into RAM.
            var span = RamHalfwords(_madr[channel], halfwords);
            var read = spu.DmaRead(core, span);
            _madr[channel] = (_madr[channel] + (uint)(span.Length * 2)) & 0xFFFFFF;
            FinishDmaAt(channel, now + Math.Max(1, read / 2));
            return;
        }

        _spuChannel = channel;
        _spuRemaining = halfwords;
        ContinueSpuDma(now);
    }

    /// <summary>Feeds the SPU2 what it can take of a write; the rest waits for room (AutoDMA's buffers).</summary>
    private void ContinueSpuDma(long now)
    {
        var channel = _spuChannel;
        if (channel < 0 || Spu2 is not { } spu)
        {
            return;
        }

        var core = channel == DmaSpu2Core0 ? 0 : 1;
        var span = RamHalfwords(_madr[channel], _spuRemaining);
        var taken = Math.Clamp(spu.DmaWrite(core, span), 0, span.Length);
        _madr[channel] = (_madr[channel] + (uint)(taken * 2)) & 0xFFFFFF;
        _spuRemaining -= taken;

        // A span cut short at the end of RAM continues from its start next time, as MADR wraps.
        if (_spuRemaining <= 0)
        {
            _spuChannel = -1;
            FinishDmaAt(channel, now + Math.Max(1, taken / 2));
        }
    }

    private Span<ushort> RamHalfwords(uint address, int count)
    {
        var offset = (int)(address & (RamSize - 2));
        var bytes = Math.Min(count * 2, RamSize - offset);
        return MemoryMarshal.Cast<byte, ushort>(Ram.AsSpan(offset, bytes));
    }

    // ---- SIF0 (channel 9) and SIF1 (channel 10) ----

    private uint RamWord(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan((int)(address & (RamSize - 4))));

    /// <summary>Moves channel 9's tags and data into the SIF0 FIFO while it has room.</summary>
    private void ServiceSif0()
    {
        var fifo = _sif.Sif0;
        while (_dmaActive[DmaSif0] && _dmaDoneAt[DmaSif0] == long.MaxValue)
        {
            if (_sif0Words == 0)
            {
                if (_sif0Junk > 0)
                {
                    if (fifo.Free < _sif0Junk)
                    {
                        return;
                    }

                    for (; _sif0Junk > 0; _sif0Junk--)
                    {
                        fifo.TryPush(0u);
                    }
                }

                if (_sif0End)
                {
                    FinishDmaAt(DmaSif0, Cpu.Cycles + 1);
                    return;
                }

                // The next IOP tag, and with CHCR bit 8 the EE's DMAtag after it.
                var tadr = _tadr[DmaSif0];
                var withEeTag = (_chcr[DmaSif0] & 0x100) != 0;
                if (withEeTag && fifo.Free < 4)
                {
                    return;
                }

                var tag = RamWord(tadr);
                var count = RamWord(tadr + 4);
                if (withEeTag)
                {
                    for (uint i = 0; i < 4; i++)
                    {
                        fifo.TryPush(RamWord(tadr + 8 + i * 4));
                    }
                }

                _tadr[DmaSif0] = (tadr + (withEeTag ? 16u : 8u)) & 0xFFFFFF;
                _madr[DmaSif0] = tag & 0xFFFFFF;
                _sif0Words = count & 0xFFFFF;
                _sif0Junk = (int)((4 - (_sif0Words & 3)) & 3);
                _sif0End = (tag & 0xC0000000) != 0;
                continue;
            }

            if (fifo.Free == 0)
            {
                return;
            }

            while (_sif0Words > 0 && fifo.Free > 0)
            {
                fifo.TryPush(RamWord(_madr[DmaSif0]));
                _madr[DmaSif0] = (_madr[DmaSif0] + 4) & 0xFFFFFF;
                _sif0Words--;
            }
        }
    }

    /// <summary>Takes channel 10's tags and data out of the SIF1 FIFO into RAM while it has words.</summary>
    private void ServiceSif1()
    {
        var fifo = _sif.Sif1;
        while (_dmaActive[DmaSif1] && _dmaDoneAt[DmaSif1] == long.MaxValue)
        {
            if (_sif1Words == 0)
            {
                if (_sif1End)
                {
                    FinishDmaAt(DmaSif1, Cpu.Cycles + 1);
                    return;
                }

                if (fifo.Count < 4)
                {
                    return;
                }

                fifo.TryPop(out uint tag);
                fifo.TryPop(out uint count);
                fifo.TryPop(out uint _);
                fifo.TryPop(out uint _);
                _madr[DmaSif1] = tag & 0xFFFFFF;
                _sif1Words = count & 0xFFFFC;
                _sif1End = (tag & 0xC0000000) != 0;
                continue;
            }

            if (fifo.Count == 0)
            {
                return;
            }

            while (_sif1Words > 0 && fifo.TryPop(out uint word))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan((int)(_madr[DmaSif1] & (RamSize - 4))), word);
                _madr[DmaSif1] = (_madr[DmaSif1] + 4) & 0xFFFFFF;
                _sif1Words--;
            }
        }
    }

    // ---- Save state ----

    private void SaveDma(StateWriter writer)
    {
        for (var i = 0; i < DmaChannels; i++)
        {
            writer.WriteUInt32(_madr[i]);
            writer.WriteUInt32(_bcr[i]);
            writer.WriteUInt32(_chcr[i]);
            writer.WriteUInt32(_tadr[i]);
            writer.WriteInt64(_dmaDoneAt[i]);
            writer.WriteBool(_dmaActive[i]);
        }

        writer.WriteUInt32(_dpcr);
        writer.WriteUInt32(_dpcr2);
        writer.WriteUInt32(_dicr);
        writer.WriteUInt32(_dicr2);
        writer.WriteUInt32(_dmacen);
        writer.WriteUInt32(_dmacinten);
        writer.WriteBool(_dmaMaster);
        writer.WriteInt32(_cdvdRemaining);
        writer.WriteInt32(_spuRemaining);
        writer.WriteInt32(_spuChannel);
        writer.WriteUInt32(_sif0Words);
        writer.WriteInt32(_sif0Junk);
        writer.WriteBool(_sif0End);
        writer.WriteUInt32(_sif1Words);
        writer.WriteBool(_sif1End);
    }

    private void LoadDma(StateReader reader)
    {
        for (var i = 0; i < DmaChannels; i++)
        {
            _madr[i] = reader.ReadUInt32();
            _bcr[i] = reader.ReadUInt32();
            _chcr[i] = reader.ReadUInt32();
            _tadr[i] = reader.ReadUInt32();
            _dmaDoneAt[i] = reader.ReadInt64();
            _dmaActive[i] = reader.ReadBool();
        }

        _dpcr = reader.ReadUInt32();
        _dpcr2 = reader.ReadUInt32();
        _dicr = reader.ReadUInt32();
        _dicr2 = reader.ReadUInt32();
        _dmacen = reader.ReadUInt32();
        _dmacinten = reader.ReadUInt32();
        _dmaMaster = reader.ReadBool();
        _cdvdRemaining = reader.ReadInt32();
        _spuRemaining = reader.ReadInt32();
        _spuChannel = reader.ReadInt32();
        if (_spuChannel is not (-1 or DmaSpu2Core0 or DmaSpu2Core1))
        {
            throw new StateFormatException("IOP DMA state out of range.");
        }

        _sif0Words = reader.ReadUInt32() & 0xFFFFF;
        _sif0Junk = reader.ReadInt32() & 3;
        _sif0End = reader.ReadBool();
        _sif1Words = reader.ReadUInt32() & 0xFFFFC;
        _sif1End = reader.ReadBool();
    }
}
