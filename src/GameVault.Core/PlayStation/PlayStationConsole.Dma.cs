using System.Buffers.Binary;

namespace GameVault.Core.PlayStation;

public sealed partial class PlayStationConsole
{
    // DMA registers (psx-spx: "DMA Channels"): MADR, BCR and CHCR for each of the seven channels, then DPCR and DICR.
    private readonly uint[] _dmaAddress = new uint[7];
    private readonly uint[] _dmaBlock = new uint[7];
    private readonly uint[] _dmaControl = new uint[7];
    private uint _dmaPriority = 0x07654321;
    private uint _dmaInterrupt;

    private const int DmaMdecIn = 0;
    private const int DmaMdecOut = 1;
    private const int DmaGpu = 2;
    private const int DmaCdRom = 3;
    private const int DmaSpu = 4;
    private const int DmaOtc = 6;

    private uint ReadDma(uint address)
    {
        var offset = (int)(address - 0x1F801080);
        var channel = offset >> 4;
        if (channel == 7)
        {
            return (offset & 0xF) == 0 ? _dmaPriority : (offset & 0xF) == 4 ? _dmaInterrupt : 0;
        }

        return (offset & 0xF) switch
        {
            0 => _dmaAddress[channel],
            4 => _dmaBlock[channel],
            8 => _dmaControl[channel],
            _ => 0,
        };
    }

    private void WriteDma(uint address, uint value)
    {
        var offset = (int)(address - 0x1F801080);
        var channel = offset >> 4;
        if (channel == 7)
        {
            if ((offset & 0xF) == 0)
            {
                _dmaPriority = value;
            }
            else if ((offset & 0xF) == 4)
            {
                // Bits 24–30 acknowledge on writing 1; the rest are written as given.
                var flags = (_dmaInterrupt & ~value) & 0x7F000000;
                _dmaInterrupt = (value & 0x00FF803F) | flags;
                UpdateDmaInterrupt();
            }

            return;
        }

        switch (offset & 0xF)
        {
            case 0:
                _dmaAddress[channel] = value & 0xFFFFFF;
                break;
            case 4:
                _dmaBlock[channel] = value;
                break;
            case 8:
                // OTC only has its start, trigger and the fixed backwards step; the others take every bit.
                _dmaControl[channel] = channel == DmaOtc ? (value & 0x51000000) | 0x2 : value & 0x71770703;
                TryStartDma(channel);
                break;
        }
    }

    /// <summary>Re-checks channels waiting on a device's request (the MDEC and the CD-ROM's data).</summary>
    private void PollDmaRequests()
    {
        for (var channel = 0; channel < 7; channel++)
        {
            if ((_dmaControl[channel] & 0x01000000) != 0)
            {
                TryStartDma(channel);
            }
        }
    }

    private void TryStartDma(int channel)
    {
        var control = _dmaControl[channel];
        var enabled = (_dmaPriority & (8u << (channel * 4))) != 0;
        var syncMode = (control >> 9) & 3;
        var started = (control & 0x01000000) != 0 && (syncMode != 0 || (control & 0x10000000) != 0);
        if (!enabled || !started || !DeviceReady(channel))
        {
            return;
        }

        if (syncMode == 2)
        {
            RunLinkedList(channel);
        }
        else
        {
            RunBlock(channel, syncMode);
        }

        _dmaControl[channel] &= ~0x11000000u;
        if ((_dmaInterrupt & (1u << (16 + channel))) != 0)
        {
            _dmaInterrupt |= 1u << (24 + channel);
            UpdateDmaInterrupt();
        }
    }

    private bool DeviceReady(int channel) => channel switch
    {
        DmaMdecIn => Mdec.DataInRequest,
        DmaMdecOut => Mdec.DataOutRequest,
        _ => true,
    };

    /// <summary>
    /// A block transfer, all at once: sync mode 0 moves BCR's word count (0 = $10000), mode 1 its blocks × block size.
    /// The CPU is charged a cycle per word for the bus time.
    /// </summary>
    private void RunBlock(int channel, uint syncMode)
    {
        var control = _dmaControl[channel];
        var toDevice = (control & 1) != 0;
        var step = (control & 2) != 0 ? -4 : 4;
        var block = _dmaBlock[channel];
        var words = syncMode == 0
            ? (int)((block & 0xFFFF) == 0 ? 0x10000 : block & 0xFFFF)
            : (int)((block & 0xFFFF) * (block >> 16));

        var address = _dmaAddress[channel];
        for (var i = 0; i < words; i++)
        {
            var ram = (int)(address & 0x1FFFFC);
            if (channel == DmaOtc)
            {
                // The ordering-table clear: each entry points at the one before; the last is the end marker.
                var entry = i == words - 1 ? 0xFFFFFFu : (address - 4) & 0x1FFFFF;
                BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan(ram), entry);
            }
            else if (toDevice)
            {
                DmaToDevice(channel, BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan(ram)));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan(ram), DmaFromDevice(channel));
            }

            address = (uint)(address + step) & 0xFFFFFF;
        }

        if (syncMode == 1)
        {
            // Request mode leaves the address after the last block and the block count at 0.
            _dmaAddress[channel] = address;
            _dmaBlock[channel] &= 0xFFFF;
        }

        Cpu.Cycles += words;
    }

    /// <summary>GPU linked-list DMA: packets of up to 255 words, each headed by its length and the next packet's address.</summary>
    private void RunLinkedList(int channel)
    {
        var address = _dmaAddress[channel] & 0x1FFFFC;
        var words = 0;
        for (var packets = 0; packets < 0x100000; packets++)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan((int)address));
            var count = (int)(header >> 24);
            for (var i = 1; i <= count; i++)
            {
                DmaToDevice(channel, BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan((int)((address + i * 4) & 0x1FFFFC))));
            }

            words += count + 1;
            if ((header & 0x800000) != 0)
            {
                break;
            }

            address = header & 0x1FFFFC;
        }

        _dmaAddress[channel] = 0xFFFFFF;
        Cpu.Cycles += words;
    }

    private void DmaToDevice(int channel, uint word)
    {
        switch (channel)
        {
            case DmaMdecIn:
                Mdec.WriteCommand(word);
                break;
            case DmaGpu:
                WriteGp0(word);
                break;
            case DmaSpu:
                Spu.DmaWrite(word);
                break;
        }
    }

    private uint DmaFromDevice(int channel) => channel switch
    {
        DmaMdecOut => Mdec.ReadData(),
        DmaGpu => Gpu.ReadData(),
        DmaCdRom => CdRom.DmaRead(),
        DmaSpu => Spu.DmaRead(),
        _ => 0,
    };

    /// <summary>DICR bit 31: forced (bit 15), or enabled (bit 23) with a flagged and enabled channel. Its rising edge raises IRQ 3.</summary>
    private void UpdateDmaInterrupt()
    {
        var was = (_dmaInterrupt & 0x80000000) != 0;
        var flags = (_dmaInterrupt >> 24) & (_dmaInterrupt >> 16) & 0x7F;
        var now = (_dmaInterrupt & 0x8000) != 0 || ((_dmaInterrupt & 0x800000) != 0 && flags != 0);
        _dmaInterrupt = now ? _dmaInterrupt | 0x80000000 : _dmaInterrupt & 0x7FFFFFFF;
        if (now && !was)
        {
            RaiseInterrupt(PsInterrupt.Dma);
        }
    }
}
