using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The EE's DMA controller (ps2tek: "DMAC I/O", "DMAC Chain Mode", "DMAC MFIFO", "DMAC Interrupts"): ten channels
/// between RDRAM or the scratchpad and the VIFs, the GIF, the IPU, the SIF and the scratchpad, with the control registers
/// at $1000E000 and D_ENABLER/D_ENABLEW at $1000F520/$1000F590. <see cref="Int1"/> is the EE's INT1 line and
/// <see cref="Cpcond0"/> its COP0 condition (BC0F/BC0T).
/// </summary>
/// <remarks>
/// Transfers run in <see cref="Advance"/>, which the console calls with the bus cycles that passed: about one quadword
/// moves per bus cycle (a DMAtag read also costs one), in slices of up to 16 quadwords per channel, the active channels
/// taking turns from channel 0 up. Cycle stealing (D_CTRL's RELE/RCYC) and D_PCR's priority order aren't modelled;
/// D_PCR's channel enables (with PCE) are. A transfer pauses when its peer doesn't take or supply data (the GIF refuses
/// PATH3, a VIF's FIFO is full, a SIF FIFO is full or empty) and resumes on a later <see cref="Advance"/>. As on hardware
/// nothing moves until D_CTRL.DMAE is set (the BIOS sets it; a high-level kernel must too). Channel logic follows ps2tek
/// and Play! (BSD-2-Clause): Source/ee/DMAC.cpp, Dmac_Channel.cpp, with ps2autotests' dma/dmac and dma/spr results.
/// </remarks>
public sealed partial class Ps2Dmac : IStateful
{
    public const int ChannelCount = 10;

    public const int Vif0Channel = 0;
    public const int Vif1Channel = 1;
    public const int GifChannel = 2;
    public const int IpuFromChannel = 3;
    public const int IpuToChannel = 4;
    public const int Sif0Channel = 5;
    public const int Sif1Channel = 6;
    public const int Sif2Channel = 7;
    public const int SprFromChannel = 8;
    public const int SprToChannel = 9;

    public const int ScratchpadSize = 16 * 1024;

    // Dn_CHCR bits.
    private const uint ChcrDir = 1 << 0;
    private const int ChcrModShift = 2;
    private const int ChcrAspShift = 4;
    private const uint ChcrAsp = 3 << ChcrAspShift;
    private const uint ChcrTte = 1 << 6;
    private const uint ChcrTie = 1 << 7;
    private const uint ChcrStr = 1 << 8;

    // D_STAT bits.
    private const uint StatSis = 1 << 13;
    private const uint StatMeis = 1 << 14;
    private const uint StatBeis = 1 << 15;
    private const uint StatSim = 1 << 29;
    private const uint StatMeim = 1 << 30;

    // D_PCR.
    private const uint PcrPce = 1u << 31;

    // D_ENABLER/W: CPND suspends every channel.
    private const uint EnableCpnd = 1 << 16;

    private const uint SadrMask = (ScratchpadSize - 1) & ~0xFu;
    private const uint ScratchpadBit = 0x8000_0000;

    private static readonly StateTag Tag = StateTag.From("EDMA");
    private static readonly uint[] ChannelBases = [0x8000, 0x9000, 0xA000, 0xB000, 0xB400, 0xC000, 0xC400, 0xC800, 0xD000, 0xD400];

    private readonly byte[] _rdram;
    private readonly byte[] _scratchpad;
    private readonly uint _ramMask;
    private readonly Ps2Vif _vif0;
    private readonly Ps2Vif _vif1;
    private readonly IPs2Gif _gif;
    private readonly Ps2Sif _sif;

    // Channel registers.
    private readonly uint[] _chcr = new uint[ChannelCount];
    private readonly uint[] _madr = new uint[ChannelCount];
    private readonly uint[] _qwc = new uint[ChannelCount];
    private readonly uint[] _tadr = new uint[ChannelCount];
    private readonly uint[] _asr0 = new uint[ChannelCount];
    private readonly uint[] _asr1 = new uint[ChannelCount];
    private readonly uint[] _sadr = new uint[ChannelCount];

    // Channel progress: a chain started with QWC = 0 reads its first tag before checking for the end; _endAfter ends the
    // transfer once QWC runs out; the interleave block's position; whether a stall interrupt was already raised.
    private readonly bool[] _initTransfer = new bool[ChannelCount];
    private readonly bool[] _endAfter = new bool[ChannelCount];
    private readonly uint[] _interleaveCount = new uint[ChannelCount];
    private readonly bool[] _stallReported = new bool[ChannelCount];

    // Control registers.
    private uint _ctrl;
    private uint _stat;
    private uint _pcr;
    private uint _sqwc;
    private uint _rbsr;
    private uint _rbor;
    private uint _stadr;
    private uint _enable;

    /// <param name="rdram">Main memory (32 MB; its length must be a power of two).</param>
    /// <param name="scratchpad">The 16 KB scratchpad (addresses with bit 31 set, and the SPR channels' far end).</param>
    /// <param name="vif0">Channel 0's peer.</param>
    /// <param name="vif1">Channel 1's peer (and the MFIFO/stall-control drain it can be).</param>
    /// <param name="gif">Channel 2's peer (PATH3).</param>
    /// <param name="sif">Channels 5 (SIF0, from the IOP) and 6 (SIF1, to the IOP) use its FIFOs.</param>
    /// <param name="intc">The EE's INTC. The DMAC drives INT1 itself (<see cref="Int1"/>); this is for the wiring's sake.</param>
    public Ps2Dmac(byte[] rdram, byte[] scratchpad, Ps2Vif vif0, Ps2Vif vif1, IPs2Gif gif, Ps2Sif sif, Ps2Intc intc)
    {
        ArgumentNullException.ThrowIfNull(intc);
        if (rdram.Length < 16 || (rdram.Length & (rdram.Length - 1)) != 0)
        {
            throw new ArgumentException("RDRAM's size must be a power of two.", nameof(rdram));
        }

        if (scratchpad.Length != ScratchpadSize)
        {
            throw new ArgumentException("The scratchpad is 16 KB.", nameof(scratchpad));
        }

        _rdram = rdram;
        _scratchpad = scratchpad;
        _ramMask = (uint)rdram.Length - 1;
        _vif0 = vif0;
        _vif1 = vif1;
        _gif = gif;
        _sif = sif;
        Reset();
    }

    /// <summary>The IPU's FIFOs for channels 3 and 4; until set, those channels wait.</summary>
    public IPs2DmaIpu? Ipu { get; set; }

    /// <summary>
    /// The DMAC's interrupt line (INT1): a channel's D_STAT.CIS with its CIM, the stall (SIS/SIM) or MFIFO-empty
    /// (MEIS/MEIM) interrupt, or a bus error (BEIS, not maskable).
    /// </summary>
    public bool Int1 =>
        ((_stat & (_stat >> 16) & 0x3FF) != 0) ||
        ((_stat & StatSis) != 0 && (_stat & StatSim) != 0) ||
        ((_stat & StatMeis) != 0 && (_stat & StatMeim) != 0) ||
        (_stat & StatBeis) != 0;

    /// <summary>
    /// COP0's CPCOND0: every channel selected in D_PCR's CPC bits has its D_STAT.CIS set (ps2tek; ps2autotests
    /// dma/dmac/cpcond).
    /// </summary>
    public bool Cpcond0 => ((~_pcr | _stat) & 0x3FF) == 0x3FF;

    /// <summary>A channel has a transfer under way (and the DMAC isn't disabled or suspended).</summary>
    public bool Busy
    {
        get
        {
            if (!Running)
            {
                return false;
            }

            for (var ch = 0; ch < ChannelCount; ch++)
            {
                if (IsActive(ch))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>D_STAT, for tests and the debugger.</summary>
    public uint Stat => _stat;

    private bool Running => (_ctrl & 1) != 0 && (_enable & EnableCpnd) == 0;

    /// <summary>Power-on state: every channel stopped, D_CTRL = 0 (DMA disabled), D_ENABLER = $1201 (ps2tek).</summary>
    public void Reset()
    {
        Array.Clear(_chcr);
        Array.Clear(_madr);
        Array.Clear(_qwc);
        Array.Clear(_tadr);
        Array.Clear(_asr0);
        Array.Clear(_asr1);
        Array.Clear(_sadr);
        Array.Clear(_initTransfer);
        Array.Clear(_endAfter);
        Array.Clear(_interleaveCount);
        Array.Clear(_stallReported);
        _ctrl = _stat = _pcr = _sqwc = _rbsr = _rbor = _stadr = 0;
        _enable = 0x1201;
    }

    // ---- Registers ----

    /// <summary>Reads a DMAC register ($10008000–$1000EFFF, or D_ENABLER at $1000F520); others read 0.</summary>
    public uint ReadRegister(uint address)
    {
        address &= 0x1FFF_FFFF;
        if ((address & 0xF) != 0)
        {
            return 0;
        }

        if (address == 0x1000F520)
        {
            return _enable;
        }

        if (address is >= 0x1000E000 and < 0x1000F000)
        {
            return (address & 0xFFF) switch
            {
                0x000 => _ctrl,
                0x010 => _stat,
                0x020 => _pcr,
                0x030 => _sqwc,
                0x040 => _rbsr,
                0x050 => _rbor,
                0x060 => _stadr,
                _ => 0,
            };
        }

        var ch = ChannelAt(address);
        if (ch < 0)
        {
            return 0;
        }

        return (address & 0xF0) switch
        {
            0x00 => _chcr[ch],
            0x10 => _madr[ch],
            0x20 => _qwc[ch],
            0x30 => _tadr[ch],
            0x40 => _asr0[ch],
            0x50 => _asr1[ch],
            0x80 when ch >= SprFromChannel => _sadr[ch],
            _ => 0,
        };
    }

    public void WriteRegister(uint address, uint value)
    {
        address &= 0x1FFF_FFFF;
        if ((address & 0xF) != 0)
        {
            return;
        }

        if (address == 0x1000F590)
        {
            _enable = value;
            return;
        }

        if (address is >= 0x1000E000 and < 0x1000F000)
        {
            WriteControl(address & 0xFFF, value);
            return;
        }

        var ch = ChannelAt(address);
        if (ch < 0)
        {
            return;
        }

        switch (address & 0xF0)
        {
            case 0x00:
                WriteChcr(ch, value);
                break;
            case 0x10:
                // The SPR channels' MADR has no scratchpad bit (ps2autotests: dma/spr/normal).
                _madr[ch] = value & (ch >= SprFromChannel ? 0x7FFF_FFF0u : 0xFFFF_FFF0u);
                break;
            case 0x20:
                _qwc[ch] = value & 0xFFFF;
                break;
            case 0x30:
                _tadr[ch] = value & 0xFFFF_FFF0;
                break;
            case 0x40:
                _asr0[ch] = value & 0xFFFF_FFF0;
                break;
            case 0x50:
                _asr1[ch] = value & 0xFFFF_FFF0;
                break;
            case 0x80 when ch >= SprFromChannel:
                _sadr[ch] = value & SadrMask;
                break;
        }
    }

    private static int ChannelAt(uint address)
    {
        if ((address & 0xFFFF_0000) != 0x1000_0000)
        {
            return -1;
        }

        var block = address & 0xFF00;
        for (var ch = 0; ch < ChannelCount; ch++)
        {
            if (ChannelBases[ch] == block)
            {
                return ch;
            }
        }

        return -1;
    }

    private void WriteControl(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x000:
                _ctrl = value;
                break;
            case 0x010:
                // Status bits: write 1 to clear. Mask bits: write 1 to toggle (ps2tek).
                _stat &= ~(value & 0xFFFF);
                _stat ^= value & 0xFFFF_0000;
                break;
            case 0x020:
                _pcr = value;
                break;
            case 0x030:
                _sqwc = value & 0x00FF_00FF;
                break;
            case 0x040:
                _rbsr = value & 0x7FFF_FFF0;
                break;
            case 0x050:
                _rbor = value & 0x7FFF_FFF0;
                break;
            case 0x060:
                _stadr = value & 0x7FFF_FFF0;
                break;
        }
    }

    private void WriteChcr(int ch, uint value)
    {
        if ((_chcr[ch] & ChcrStr) != 0)
        {
            // While a transfer runs only STR can change: writing 0 suspends it (Play!).
            if ((value & ChcrStr) == 0)
            {
                _chcr[ch] &= ~ChcrStr;
            }

            return;
        }

        _chcr[ch] = value;
        if ((value & ChcrStr) != 0)
        {
            Start(ch);
        }
    }

    private void Start(int ch)
    {
        _interleaveCount[ch] = 0;
        _stallReported[ch] = false;

        // A chain started with QWC > 0 resumes: CHCR.TAG is taken as the last tag read (ps2tek: "DMA Resuming").
        _initTransfer[ch] = _qwc[ch] == 0;
        var tag = _chcr[ch] >> 16;
        var id = (tag >> 12) & 7;
        var irqEnd = (tag & 0x8000) != 0 && (_chcr[ch] & ChcrTie) != 0;
        _endAfter[ch] = !_initTransfer[ch] && (irqEnd || (IsDestinationChain(ch) ? id == 7 : id is 0 or 7));
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var ch = 0; ch < ChannelCount; ch++)
        {
            writer.WriteUInt32(_chcr[ch]);
            writer.WriteUInt32(_madr[ch]);
            writer.WriteUInt32(_qwc[ch]);
            writer.WriteUInt32(_tadr[ch]);
            writer.WriteUInt32(_asr0[ch]);
            writer.WriteUInt32(_asr1[ch]);
            writer.WriteUInt32(_sadr[ch]);
            writer.WriteBool(_initTransfer[ch]);
            writer.WriteBool(_endAfter[ch]);
            writer.WriteUInt32(_interleaveCount[ch]);
            writer.WriteBool(_stallReported[ch]);
        }

        writer.WriteUInt32(_ctrl);
        writer.WriteUInt32(_stat);
        writer.WriteUInt32(_pcr);
        writer.WriteUInt32(_sqwc);
        writer.WriteUInt32(_rbsr);
        writer.WriteUInt32(_rbor);
        writer.WriteUInt32(_stadr);
        writer.WriteUInt32(_enable);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported EE DMAC state version.");
        }

        for (var ch = 0; ch < ChannelCount; ch++)
        {
            _chcr[ch] = reader.ReadUInt32();
            _madr[ch] = reader.ReadUInt32();
            _qwc[ch] = reader.ReadUInt32() & 0xFFFF;
            _tadr[ch] = reader.ReadUInt32();
            _asr0[ch] = reader.ReadUInt32();
            _asr1[ch] = reader.ReadUInt32();
            _sadr[ch] = reader.ReadUInt32() & SadrMask;
            _initTransfer[ch] = reader.ReadBool();
            _endAfter[ch] = reader.ReadBool();
            _interleaveCount[ch] = reader.ReadUInt32();
            _stallReported[ch] = reader.ReadBool();
        }

        _ctrl = reader.ReadUInt32();
        _stat = reader.ReadUInt32();
        _pcr = reader.ReadUInt32();
        _sqwc = reader.ReadUInt32();
        _rbsr = reader.ReadUInt32();
        _rbor = reader.ReadUInt32();
        _stadr = reader.ReadUInt32();
        _enable = reader.ReadUInt32();
        reader.EndChunk();
    }
}
