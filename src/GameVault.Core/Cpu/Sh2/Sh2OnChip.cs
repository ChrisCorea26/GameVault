using GameVault.Core.State;

namespace GameVault.Core.Cpu.Sh2;

/// <summary>
/// The SH7604's on-chip modules at $FFFFFE00–$FFFFFFFF that 32X games use: the division unit (DIVU), the two DMA
/// channels (DMAC), the free-running timer (FRT), the watchdog timer in interval mode (WDT) and the interrupt
/// controller's priorities and vectors. The serial port, the bus controller and the cache controller keep what's
/// written to them.
/// </summary>
/// <remarks>Reference: SH7604 Hardware Manual (sections 5 interrupts, 9 DMAC, 10 DIVU, 11 FRT, 12 WDT).</remarks>
public sealed class Sh2OnChip : IStateful
{
    private static readonly StateTag Tag = StateTag.From("SH2P");
    private static readonly int[] FrtDivider = [8, 32, 128, 1];
    private static readonly int[] WdtDivider = [2, 64, 128, 256, 512, 1024, 4096, 8192];

    private readonly Sh2Cpu _cpu;
    private readonly byte[] _regs = new byte[0x200];
    private readonly uint[] _sar = new uint[2];
    private readonly uint[] _dar = new uint[2];
    private readonly uint[] _tcr = new uint[2];
    private readonly uint[] _chcr = new uint[2];
    private readonly uint[] _vcrDma = new uint[2];
    private uint _dmaor;
    private uint _dvsr;
    private uint _dvdnth;
    private uint _dvdntl;
    private uint _dvcr;
    private uint _vcrDiv;
    private ushort _frc;
    private ushort _ocra = 0xFFFF;
    private ushort _ocrb = 0xFFFF;
    private byte _frtTemp;
    private int _frtPrescale;
    private byte _wtcsr;
    private byte _wtcnt;
    private int _wdtPrescale;

    public Sh2OnChip(Sh2Cpu cpu) => _cpu = cpu;

    // Priorities (IPRA: DIVU 15–12, DMAC 11–8, WDT 7–4; IPRB: SCI 15–12, FRT 11–8) and vectors.
    private int Ipra => (_regs[0xE2] << 8) | _regs[0xE3];

    private int Iprb => (_regs[0x60] << 8) | _regs[0x61];

    /// <summary>The highest priority of the modules' pending, enabled interrupts (0 for none).</summary>
    public int InterruptLevel
    {
        get
        {
            var level = 0;
            if ((_dvcr & 3) == 3)
            {
                level = Math.Max(level, (Ipra >> 12) & 0x0F);
            }

            for (var c = 0; c < 2; c++)
            {
                if ((_chcr[c] & 0x06) == 0x06)
                {
                    level = Math.Max(level, (Ipra >> 8) & 0x0F); // IE and TE
                }
            }

            if ((_wtcsr & 0xE0) == 0xA0)
            {
                level = Math.Max(level, (Ipra >> 4) & 0x0F);
            }

            if ((FrtPending & Tier) != 0)
            {
                level = Math.Max(level, (Iprb >> 8) & 0x0F);
            }

            return level;
        }
    }

    private byte Tier => _regs[0x10];

    // FTCSR's OCFA (bit 3), OCFB (bit 2) and OVF (bit 1) match TIER's enables.
    private int FrtPending => _regs[0x11] & 0x0E;

    /// <summary>The vector of the interrupt <see cref="InterruptLevel"/> names (the highest-priority one).</summary>
    public int AcknowledgeInterrupt()
    {
        var level = InterruptLevel;
        if ((_dvcr & 3) == 3 && ((Ipra >> 12) & 0x0F) == level)
        {
            return (int)(_vcrDiv & 0x7F);
        }

        for (var c = 0; c < 2; c++)
        {
            if ((_chcr[c] & 0x06) == 0x06 && ((Ipra >> 8) & 0x0F) == level)
            {
                return (int)(_vcrDma[c] & 0x7F);
            }
        }

        if ((_wtcsr & 0xE0) == 0xA0 && ((Ipra >> 4) & 0x0F) == level)
        {
            return _regs[0xE4] & 0x7F; // VCRWDT's ITI vector
        }

        var pending = FrtPending & Tier;
        return (pending & 0x02) != 0 ? _regs[0x68] & 0x7F // VCRD: overflow
            : _regs[0x67] & 0x7F; // VCRC: output compare
    }

    public void Reset()
    {
        Array.Clear(_regs);
        Array.Clear(_sar);
        Array.Clear(_dar);
        Array.Clear(_tcr);
        Array.Clear(_chcr);
        Array.Clear(_vcrDma);
        _dmaor = 0;
        _dvsr = _dvdnth = _dvdntl = _dvcr = _vcrDiv = 0;
        _frc = 0;
        _ocra = _ocrb = 0xFFFF;
        _frtTemp = 0;
        _frtPrescale = 0;
        _wtcsr = 0x18;
        _wtcnt = 0;
        _wdtPrescale = 0;
        _regs[0x10] = 0x01; // TIER
    }

    /// <summary>Advances the timers by <paramref name="cycles"/> CPU clocks.</summary>
    public void Run(int cycles)
    {
        var tcr = _regs[0x16] & 3;
        if (tcr != 3)
        {
            _frtPrescale += cycles;
            var divider = FrtDivider[tcr];
            while (_frtPrescale >= divider)
            {
                _frtPrescale -= divider;
                FrtTick();
            }
        }

        if ((_wtcsr & 0x20) != 0)
        {
            _wdtPrescale += cycles;
            var divider = WdtDivider[_wtcsr & 7];
            while (_wdtPrescale >= divider)
            {
                _wdtPrescale -= divider;
                if (++_wtcnt == 0 && (_wtcsr & 0x40) == 0)
                {
                    _wtcsr |= 0x80; // interval timer overflow
                }
            }
        }
    }

    private void FrtTick()
    {
        _frc++;
        if (_frc == _ocra)
        {
            _regs[0x11] |= 0x08;
            if ((_regs[0x11] & 0x01) != 0)
            {
                _frc = 0; // CCLRA
            }
        }

        if (_frc == _ocrb)
        {
            _regs[0x11] |= 0x04;
        }

        if (_frc == 0)
        {
            _regs[0x11] |= 0x02;
        }
    }

    // ---- DMA ----

    private bool ChannelReady(int c) =>
        (_dmaor & 0x07) == 0x01 && (_chcr[c] & 0x03) == 0x01; // DME set, no NMIF/AE; DE set, TE clear

    private void TryAutoRequest(int c)
    {
        if (ChannelReady(c) && (_chcr[c] & 0x200) != 0)
        {
            while (ChannelReady(c))
            {
                TransferUnit(c);
            }
        }
    }

    /// <summary>A DREQ from outside (the 32X's FIFO on channel 0, PWM on channel 1): one unit, if the channel waits for it.</summary>
    public bool Dreq(int channel)
    {
        if (!ChannelReady(channel) || (_chcr[channel] & 0x200) != 0)
        {
            return false;
        }

        TransferUnit(channel);
        return true;
    }

    /// <summary>Channel <paramref name="channel"/> is enabled and waiting for DREQs.</summary>
    public bool WaitingForDreq(int channel) => ChannelReady(channel) && (_chcr[channel] & 0x200) == 0;

    private void TransferUnit(int c)
    {
        var chcr = _chcr[c];
        var size = (int)((chcr >> 10) & 3);
        var bytes = size switch { 0 => 1, 1 => 2, 2 => 4, _ => 16 };
        var source = _sar[c];
        var destination = _dar[c];
        for (var offset = 0; offset < bytes; offset += Math.Min(bytes, 4))
        {
            switch (bytes)
            {
                case 1:
                    _cpu.DmaWrite8(destination, _cpu.DmaRead8(source));
                    break;
                case 2:
                    _cpu.DmaWrite16(destination, _cpu.DmaRead16(source));
                    break;
                default:
                    _cpu.DmaWrite32(destination + (uint)(size == 3 ? offset : 0), _cpu.DmaRead32(source + (uint)(size == 3 ? offset : 0)));
                    break;
            }
        }

        var step = (uint)bytes;
        _sar[c] = ((chcr >> 12) & 3) switch { 1 => source + step, 2 => source - step, _ => source };
        _dar[c] = ((chcr >> 14) & 3) switch { 1 => destination + step, 2 => destination - step, _ => destination };
        var count = size == 3 ? 4u : 1u;
        _tcr[c] = (_tcr[c] - count) & 0xFFFFFF;
        if (_tcr[c] == 0 || (size == 3 && _tcr[c] > 0xFFFFF0))
        {
            _tcr[c] = 0;
            _chcr[c] |= 0x02; // TE
        }
    }

    // ---- DIVU ----

    private void Divide32()
    {
        var dividend = (int)_dvdntl;
        var divisor = (int)_dvsr;
        if (divisor == 0 || (dividend == int.MinValue && divisor == -1))
        {
            _dvcr |= 1;
            _dvdnth = (uint)(dividend >> 31);
            _dvdntl = dividend < 0 ? 0x80000000u : 0x7FFFFFFFu;
            return;
        }

        _dvdntl = (uint)(dividend / divisor);
        _dvdnth = (uint)(dividend % divisor);
    }

    private void Divide64()
    {
        var dividend = ((long)_dvdnth << 32) | _dvdntl;
        var divisor = (long)(int)_dvsr;
        if (divisor == 0)
        {
            _dvcr |= 1;
            _dvdntl = dividend < 0 ? 0x80000000u : 0x7FFFFFFFu;
            return;
        }

        var quotient = dividend / divisor;
        if (quotient is > int.MaxValue or < int.MinValue)
        {
            _dvcr |= 1;
            _dvdntl = (dividend < 0) != (divisor < 0) ? 0x80000000u : 0x7FFFFFFFu;
            return;
        }

        _dvdntl = (uint)quotient;
        _dvdnth = (uint)(dividend % divisor);
    }

    // ---- Registers ----

    public byte Read8(uint address)
    {
        var a = (int)(address & 0x1FF);
        switch (a)
        {
            case 0x12: _frtTemp = (byte)_frc; return (byte)(_frc >> 8);
            case 0x13: return _frtTemp;
            case 0x14: return (byte)(((_regs[0x17] & 0x10) != 0 ? _ocrb : _ocra) >> 8);
            case 0x15: return (byte)((_regs[0x17] & 0x10) != 0 ? _ocrb : _ocra);
            case 0x80: return _wtcsr;
            case 0x81: return _wtcnt;
            case 0x92: return _cpu.Ccr;
            case >= 0x100:
            {
                var word = Read32(address & ~3u);
                return (byte)(word >> ((3 - (a & 3)) * 8));
            }

            default: return _regs[a];
        }
    }

    public ushort Read16(uint address)
    {
        var a = (int)(address & 0x1FE);
        if (a >= 0x100)
        {
            var word = Read32(address & ~3u);
            return (a & 2) == 0 ? (ushort)(word >> 16) : (ushort)word;
        }

        return (ushort)((Read8(address) << 8) | Read8(address | 1));
    }

    public uint Read32(uint address)
    {
        var a = (int)(address & 0x1FC);
        if (a >= 0x100 && a < 0x140)
        {
            // DIVU (repeated every $20 bytes in $FF00–$FF3F).
            return (a & 0x1C) switch
            {
                0x00 => _dvsr,
                0x04 => _dvdntl,
                0x08 => _dvcr,
                0x0C => _vcrDiv,
                0x10 => _dvdnth,
                0x14 => _dvdntl,
                0x18 => _dvdnth,
                _ => _dvdntl,
            };
        }

        switch (a)
        {
            case 0x180: return _sar[0];
            case 0x184: return _dar[0];
            case 0x188: return _tcr[0];
            case 0x18C: return _chcr[0];
            case 0x190: return _sar[1];
            case 0x194: return _dar[1];
            case 0x198: return _tcr[1];
            case 0x19C: return _chcr[1];
            case 0x1A0: return _vcrDma[0];
            case 0x1A8: return _vcrDma[1];
            case 0x1B0: return _dmaor;
            default: return ((uint)Read16(address) << 16) | Read16(address + 2);
        }
    }

    public void Write8(uint address, byte value)
    {
        var a = (int)(address & 0x1FF);
        switch (a)
        {
            case 0x11:
                // FTCSR: flags clear by writing 0 after reading 1; CCLRA (bit 0) is written as is.
                _regs[0x11] = (byte)((_regs[0x11] & value & 0x8E) | (value & 0x01));
                return;
            case 0x12: _frtTemp = value; return;
            case 0x13: _frc = (ushort)((_frtTemp << 8) | value); return;
            case 0x14: _frtTemp = value; return;
            case 0x15:
                if ((_regs[0x17] & 0x10) != 0)
                {
                    _ocrb = (ushort)((_frtTemp << 8) | value);
                }
                else
                {
                    _ocra = (ushort)((_frtTemp << 8) | value);
                }

                return;
            case 0x92:
                _cpu.Ccr = value;
                return;
            case >= 0x100:
                return; // the 32-bit modules take long (or word) writes only
            default:
                _regs[a] = value;
                return;
        }
    }

    public void Write16(uint address, ushort value)
    {
        var a = (int)(address & 0x1FE);
        if (a == 0x80)
        {
            // The watchdog's guarded writes: $A5 to WTCSR, $5A to WTCNT.
            if (value >> 8 == 0xA5)
            {
                _wtcsr = (byte)((_wtcsr & value & 0x80) | (value & 0x7F));
            }
            else if (value >> 8 == 0x5A)
            {
                _wtcnt = (byte)value;
            }

            return;
        }

        if (a >= 0x100)
        {
            Write32(address & ~3u, (a & 2) == 0 ? (uint)value << 16 | (Read32(address & ~3u) & 0xFFFF) : (Read32(address & ~3u) & 0xFFFF0000) | value);
            return;
        }

        Write8(address, (byte)(value >> 8));
        Write8(address | 1, (byte)value);
    }

    public void Write32(uint address, uint value)
    {
        var a = (int)(address & 0x1FC);
        if (a >= 0x100 && a < 0x140)
        {
            switch (a & 0x1C)
            {
                case 0x00: _dvsr = value; break;
                case 0x04: _dvdntl = value; Divide32(); break;
                case 0x08: _dvcr = value & 3; break;
                case 0x0C: _vcrDiv = value & 0xFFFF; break;
                case 0x10: _dvdnth = value; break;
                case 0x14: _dvdntl = value; Divide64(); break;
                case 0x18: _dvdnth = value; break;
                default: _dvdntl = value; break;
            }

            return;
        }

        switch (a)
        {
            case 0x180: _sar[0] = value; return;
            case 0x184: _dar[0] = value; return;
            case 0x188: _tcr[0] = value & 0xFFFFFF; return;
            case 0x18C: _chcr[0] = (_chcr[0] & value & 0x02) | (value & ~0x02u & 0xFFFF); TryAutoRequest(0); return;
            case 0x190: _sar[1] = value; return;
            case 0x194: _dar[1] = value; return;
            case 0x198: _tcr[1] = value & 0xFFFFFF; return;
            case 0x19C: _chcr[1] = (_chcr[1] & value & 0x02) | (value & ~0x02u & 0xFFFF); TryAutoRequest(1); return;
            case 0x1A0: _vcrDma[0] = value & 0x7F; return;
            case 0x1A8: _vcrDma[1] = value & 0x7F; return;
            case 0x1B0:
                _dmaor = (_dmaor & value & 0x06) | (value & 0x09);
                TryAutoRequest(0);
                TryAutoRequest(1);
                return;
            default:
                Write16(address, (ushort)(value >> 16));
                Write16(address + 2, (ushort)value);
                return;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_regs);
        for (var c = 0; c < 2; c++)
        {
            writer.WriteUInt32(_sar[c]);
            writer.WriteUInt32(_dar[c]);
            writer.WriteUInt32(_tcr[c]);
            writer.WriteUInt32(_chcr[c]);
            writer.WriteUInt32(_vcrDma[c]);
        }

        writer.WriteUInt32(_dmaor);
        writer.WriteUInt32(_dvsr);
        writer.WriteUInt32(_dvdnth);
        writer.WriteUInt32(_dvdntl);
        writer.WriteUInt32(_dvcr);
        writer.WriteUInt32(_vcrDiv);
        writer.WriteUInt16(_frc);
        writer.WriteUInt16(_ocra);
        writer.WriteUInt16(_ocrb);
        writer.WriteByte(_frtTemp);
        writer.WriteInt32(_frtPrescale);
        writer.WriteByte(_wtcsr);
        writer.WriteByte(_wtcnt);
        writer.WriteInt32(_wdtPrescale);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_regs);
        for (var c = 0; c < 2; c++)
        {
            _sar[c] = reader.ReadUInt32();
            _dar[c] = reader.ReadUInt32();
            _tcr[c] = reader.ReadUInt32() & 0xFFFFFF;
            _chcr[c] = reader.ReadUInt32();
            _vcrDma[c] = reader.ReadUInt32();
        }

        _dmaor = reader.ReadUInt32();
        _dvsr = reader.ReadUInt32();
        _dvdnth = reader.ReadUInt32();
        _dvdntl = reader.ReadUInt32();
        _dvcr = reader.ReadUInt32();
        _vcrDiv = reader.ReadUInt32();
        _frc = reader.ReadUInt16();
        _ocra = reader.ReadUInt16();
        _ocrb = reader.ReadUInt16();
        _frtTemp = reader.ReadByte();
        _frtPrescale = Math.Max(0, reader.ReadInt32());
        _wtcsr = reader.ReadByte();
        _wtcnt = reader.ReadByte();
        _wdtPrescale = Math.Max(0, reader.ReadInt32());
        reader.EndChunk();
    }
}
