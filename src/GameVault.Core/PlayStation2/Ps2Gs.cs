using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The Graphics Synthesizer (SPEC §7.11, Phase 17): 4 MB of local memory, the general registers the GIF writes, the
/// privileged registers the EE maps at $12000000, and a software rasterizer. Completed primitives are queued and drawn
/// in batches, perhaps on several cores, with the same result as drawing each at once (Ps2Gs.Batch.cs).
/// </summary>
/// <remarks>
/// References: ps2tek ("GS Reference" and its sub-pages), the GS User's Manual's register descriptions as the
/// emulators document them, and Play! (BSD-2-Clause) for the swizzle tables, transfers and CLUT loads. Mipmaps are
/// sampled by TEX1's LOD, up to six levels from MIPTBP1/2 or MTBA's packed layout (outstanding-work plan §12).
/// Anti-aliasing and the texture cache's timing are not modelled.
/// </remarks>
public sealed partial class Ps2Gs : IStateful
{
    // General register addresses (ps2tek: "GS Register List").
    public const int RegPrim = 0x00;
    public const int RegRgbaq = 0x01;
    public const int RegSt = 0x02;
    public const int RegUv = 0x03;
    public const int RegXyzf2 = 0x04;
    public const int RegXyz2 = 0x05;
    public const int RegTex0_1 = 0x06;
    public const int RegTex0_2 = 0x07;
    public const int RegClamp1 = 0x08;
    public const int RegClamp2 = 0x09;
    public const int RegFog = 0x0A;
    public const int RegXyzf3 = 0x0C;
    public const int RegXyz3 = 0x0D;
    public const int RegTex1_1 = 0x14;
    public const int RegTex1_2 = 0x15;
    public const int RegTex2_1 = 0x16;
    public const int RegTex2_2 = 0x17;
    public const int RegXyOffset1 = 0x18;
    public const int RegXyOffset2 = 0x19;
    public const int RegPrModeCont = 0x1A;
    public const int RegPrMode = 0x1B;
    public const int RegTexClut = 0x1C;
    public const int RegScanMsk = 0x22;
    public const int RegMipTbp1_1 = 0x34;
    public const int RegMipTbp1_2 = 0x35;
    public const int RegMipTbp2_1 = 0x36;
    public const int RegMipTbp2_2 = 0x37;
    public const int RegTexA = 0x3B;
    public const int RegFogCol = 0x3D;
    public const int RegTexFlush = 0x3F;
    public const int RegScissor1 = 0x40;
    public const int RegScissor2 = 0x41;
    public const int RegAlpha1 = 0x42;
    public const int RegAlpha2 = 0x43;
    public const int RegDimx = 0x44;
    public const int RegDthe = 0x45;
    public const int RegColClamp = 0x46;
    public const int RegTest1 = 0x47;
    public const int RegTest2 = 0x48;
    public const int RegPabe = 0x49;
    public const int RegFba1 = 0x4A;
    public const int RegFba2 = 0x4B;
    public const int RegFrame1 = 0x4C;
    public const int RegFrame2 = 0x4D;
    public const int RegZbuf1 = 0x4E;
    public const int RegZbuf2 = 0x4F;
    public const int RegBitBltBuf = 0x50;
    public const int RegTrxPos = 0x51;
    public const int RegTrxReg = 0x52;
    public const int RegTrxDir = 0x53;
    public const int RegHwReg = 0x54;
    public const int RegSignal = 0x60;
    public const int RegFinish = 0x61;
    public const int RegLabel = 0x62;

    // Privileged register offsets from $12000000.
    public const uint PrivPmode = 0x0000;
    public const uint PrivSmode1 = 0x0010;
    public const uint PrivSmode2 = 0x0020;
    public const uint PrivSrfsh = 0x0030;
    public const uint PrivSynch1 = 0x0040;
    public const uint PrivSynch2 = 0x0050;
    public const uint PrivSyncv = 0x0060;
    public const uint PrivDispFb1 = 0x0070;
    public const uint PrivDisplay1 = 0x0080;
    public const uint PrivDispFb2 = 0x0090;
    public const uint PrivDisplay2 = 0x00A0;
    public const uint PrivExtBuf = 0x00B0;
    public const uint PrivExtData = 0x00C0;
    public const uint PrivExtWrite = 0x00D0;
    public const uint PrivBgColor = 0x00E0;
    public const uint PrivCsr = 0x1000;
    public const uint PrivImr = 0x1010;
    public const uint PrivBusDir = 0x1040;
    public const uint PrivSigLblId = 0x1080;

    // CSR bits (the GS User's Manual; ps2tek lists the register).
    public const ulong CsrSignal = 1 << 0;
    public const ulong CsrFinish = 1 << 1;
    public const ulong CsrHsInt = 1 << 2;
    public const ulong CsrVsInt = 1 << 3;
    public const ulong CsrEdwInt = 1 << 4;
    public const ulong CsrFlush = 1 << 8;
    public const ulong CsrReset = 1 << 9;
    public const ulong CsrNField = 1 << 12;
    public const ulong CsrField = 1 << 13;
    public const ulong CsrFifoEmpty = 1 << 14;

    /// <summary>The revision (bits 16–23) and id (bits 24–31) CSR reads back, as retail consoles report them.</summary>
    public const ulong CsrRevisionId = 0x551BUL << 16;

    private const ulong EventMask = CsrSignal | CsrFinish | CsrHsInt | CsrVsInt | CsrEdwInt;

    private static readonly StateTag Tag = StateTag.From("GS  ");

    private readonly byte[] _memory = new byte[LocalMemorySize];
    private readonly ulong[] _regs = new ulong[0x80];

    // Privileged registers.
    private ulong _pmode;
    private ulong _smode1;
    private ulong _smode2;
    private ulong _srfsh;
    private ulong _synch1;
    private ulong _synch2;
    private ulong _syncv;
    private ulong _dispfb1;
    private ulong _display1;
    private ulong _dispfb2;
    private ulong _display2;
    private ulong _extbuf;
    private ulong _extdata;
    private ulong _extwrite;
    private ulong _bgcolor;
    private ulong _csr;
    private ulong _imr;
    private ulong _busdir;
    private ulong _siglblid;

    // A SIGNAL that arrived while CSR.SIGNAL was still set: the GS stalls the GIF until the EE clears the bit.
    private bool _signalStalled;
    private ulong _pendingSignal;

    public Ps2Gs()
    {
        _clut = new ushort[512];
        Reset();
    }

    /// <summary>The 4 MB local memory, little-endian, in the hardware's swizzled layout (with every queued primitive drawn).</summary>
    public byte[] LocalMemory
    {
        get
        {
            Flush();
            return _memory;
        }
    }

    /// <summary>The GS interrupt line to the INTC: an event bit in CSR that IMR doesn't mask.</summary>
    public bool InterruptPending => (_csr & ~(_imr >> 8) & EventMask) != 0;

    /// <summary>Raised when an unmasked event (SIGNAL, FINISH, HSYNC, VSYNC, EDW) is set.</summary>
    public Action? InterruptRaised { get; set; }

    /// <summary>The GS holds the GIF: a second SIGNAL arrived before the EE cleared CSR.SIGNAL.</summary>
    public bool Stalled => _signalStalled;

    /// <summary>Raised when a stall ends, so the GIF can carry on with the data it held back.</summary>
    internal Action? StallCleared { get; set; }

    /// <summary>The value of a general register as last written.</summary>
    public ulong ReadGeneralRegister(int register) => _regs[register & 0x7F];

    public ulong Csr => ReadCsr();

    public ulong Imr => _imr;

    public ulong SigLblId => _siglblid;

    /// <summary>BUSDIR's direction bit: 1 when local-to-host transfers go through the GIF.</summary>
    public bool BusDirLocalToHost => (_busdir & 1) != 0;

    public void Reset()
    {
        Array.Clear(_memory);
        Array.Clear(_regs);
        Array.Clear(_clut);
        _regs[RegPrModeCont] = 1;
        _pmode = _smode1 = _srfsh = _synch1 = _synch2 = _syncv = 0;
        _smode2 = 0;
        _dispfb1 = _dispfb2 = 0;
        _display1 = _display2 = 0;
        _extbuf = _extdata = _extwrite = _bgcolor = 0;
        _csr = 0;
        _imr = 0x7F00;
        _busdir = 0;
        _siglblid = 0;
        _signalStalled = false;
        _pendingSignal = 0;
        _cbp0 = _cbp1 = 0;
        ResetDrawing();
        ResetTransfer();
        Backend?.Resync();
    }

    // ---- General registers ------------------------------------------------------------------------------------

    /// <summary>Writes a general register (addresses $00–$62, as the GIF's A+D and REGLIST modes address them).</summary>
    public void WriteRegister(int register, ulong value)
    {
        register &= 0x7F;
        switch (register)
        {
            case RegPrim:
                _regs[RegPrim] = value & 0x7FF;
                _queueCount = 0;
                _drawStateDirty = true;
                break;
            case RegRgbaq:
            case RegSt:
            case RegUv:
                _regs[register] = value;
                break;
            case RegFog:
                _regs[RegFog] = value;
                break;
            case RegXyzf2:
                _regs[RegXyzf2] = value;
                _regs[RegFog] = value & 0xFF00_0000_0000_0000;
                VertexKick((int)(value & 0xFFFF), (int)((value >> 16) & 0xFFFF), (uint)((value >> 32) & 0xFFFFFF), true);
                break;
            case RegXyzf3:
                _regs[RegXyzf3] = value;
                _regs[RegFog] = value & 0xFF00_0000_0000_0000;
                VertexKick((int)(value & 0xFFFF), (int)((value >> 16) & 0xFFFF), (uint)((value >> 32) & 0xFFFFFF), false);
                break;
            case RegXyz2:
                _regs[RegXyz2] = value;
                VertexKick((int)(value & 0xFFFF), (int)((value >> 16) & 0xFFFF), (uint)(value >> 32), true);
                break;
            case RegXyz3:
                _regs[RegXyz3] = value;
                VertexKick((int)(value & 0xFFFF), (int)((value >> 16) & 0xFFFF), (uint)(value >> 32), false);
                break;
            case RegTex0_1:
            case RegTex0_2:
                _regs[register] = value;
                LoadClut(value);
                _drawStateDirty = true;
                break;
            case RegTex2_1:
            case RegTex2_2:
            {
                // TEX2 replaces TEX0's format and CLUT fields (ps2tek: "GS Textures").
                const ulong mask = 0xFFFF_FFE0_03F0_0000;
                var tex0 = RegTex0_1 + (register - RegTex2_1);
                _regs[register] = value;
                _regs[tex0] = (_regs[tex0] & ~mask) | (value & mask);
                LoadClut(_regs[tex0]);
                _drawStateDirty = true;
                break;
            }

            case RegTrxDir:
                _regs[RegTrxDir] = value;
                BeginTransfer();
                break;
            case RegHwReg:
                WriteHwReg(value);
                break;
            case RegSignal:
                WriteSignal(value);
                break;
            case RegFinish:
                // Everything before FINISH has completed: queued primitives count as drawn (only local memory can tell,
                // and reading it draws them).
                SetEvent(CsrFinish);
                break;
            case RegLabel:
            {
                var mask = value >> 32;
                var id = (_siglblid >> 32) & 0xFFFF_FFFF;
                id = (id & ~mask) | (value & mask & 0xFFFF_FFFF);
                _siglblid = (_siglblid & 0xFFFF_FFFF) | (id << 32);
                break;
            }

            case RegTexFlush:
                _regs[register] = value;
                break;
            default:
                _regs[register] = value;
                _drawStateDirty = true;
                break;
        }
    }

    private void WriteSignal(ulong value)
    {
        if ((_csr & CsrSignal) != 0)
        {
            // ps2autotests gs/signal: "Signal must be cleared to unstall GIF".
            _signalStalled = true;
            _pendingSignal = value;
            return;
        }

        ApplySignal(value);
    }

    private void ApplySignal(ulong value)
    {
        var mask = value >> 32;
        var id = _siglblid & 0xFFFF_FFFF;
        id = (id & ~mask) | (value & mask & 0xFFFF_FFFF);
        _siglblid = (_siglblid & 0xFFFF_FFFF_0000_0000) | id;
        SetEvent(CsrSignal);
    }

    /// <summary>Sets an event bit; each unmasked event raises the interrupt, whether or not the bit was already set.</summary>
    private void SetEvent(ulong bit)
    {
        _csr |= bit;
        if ((bit & ~(_imr >> 8) & EventMask) != 0)
        {
            InterruptRaised?.Invoke();
        }
    }

    // ---- Video timing -----------------------------------------------------------------------------------------

    /// <summary>The start of a line's horizontal blank: sets CSR.HSINT.</summary>
    public void HBlankStart() => SetEvent(CsrHsInt);

    /// <summary>The start of vertical blank: sets CSR.VSINT.</summary>
    public void VBlankStart() => SetEvent(CsrVsInt);

    /// <summary>The end of vertical blank: the next field starts, so CSR.FIELD toggles (NFIELD follows it).</summary>
    public void VBlankEnd()
    {
        _csr ^= CsrField;
        _csr = (_csr & ~CsrNField) | ((_csr & CsrField) != 0 ? CsrNField : 0);
    }

    /// <summary>SMODE2.INT: the display is interlaced.</summary>
    public bool Interlaced => (_smode2 & 1) != 0;

    /// <summary>CSR.FIELD: 0 while the even field is shown, 1 for the odd one.</summary>
    public bool OddField => (_csr & CsrField) != 0;

    /// <summary>SMODE1.CMOD (bits 13–14): 3 selects PAL timing, as the BIOS's SetGsCrt programs it.</summary>
    public bool PalMode => ((_smode1 >> 13) & 3) == 3;

    // ---- Privileged registers ---------------------------------------------------------------------------------

    private ulong ReadCsr() => (_csr & (EventMask | CsrField | CsrNField)) | CsrFifoEmpty | CsrRevisionId;

    /// <summary>A 64-bit read of a privileged register; <paramref name="offset"/> is from $12000000.</summary>
    public ulong ReadPrivileged(uint offset)
    {
        switch (offset & 0x13F0)
        {
            case PrivCsr:
                return ReadCsr();
            case PrivImr:
                return _imr;
            case PrivBusDir:
                return _busdir;
            case PrivSigLblId:
                return _siglblid;
            case PrivPmode:
                return _pmode;
            case PrivSmode1:
                return _smode1;
            case PrivSmode2:
                return _smode2;
            case PrivSrfsh:
                return _srfsh;
            case PrivSynch1:
                return _synch1;
            case PrivSynch2:
                return _synch2;
            case PrivSyncv:
                return _syncv;
            case PrivDispFb1:
                return _dispfb1;
            case PrivDisplay1:
                return _display1;
            case PrivDispFb2:
                return _dispfb2;
            case PrivDisplay2:
                return _display2;
            case PrivExtBuf:
                return _extbuf;
            case PrivExtData:
                return _extdata;
            case PrivExtWrite:
                return _extwrite;
            case PrivBgColor:
                return _bgcolor;
            default:
                return 0;
        }
    }

    /// <summary>A 32-bit read: offsets ending in 4 read the upper half of the 64-bit register.</summary>
    public uint ReadPrivileged32(uint offset)
    {
        var value = ReadPrivileged(offset & ~7u);
        return (offset & 4) != 0 ? (uint)(value >> 32) : (uint)value;
    }

    /// <summary>A 64-bit write of a privileged register.</summary>
    public void WritePrivileged(uint offset, ulong value)
    {
        switch (offset & 0x13F0)
        {
            case PrivCsr:
                WriteCsr(value);
                break;
            case PrivImr:
                WriteImr(value);
                break;
            case PrivBusDir:
                _busdir = value & 1;
                break;
            case PrivSigLblId:
                _siglblid = value;
                break;
            case PrivPmode:
                _pmode = value;
                break;
            case PrivSmode1:
                _smode1 = value;
                break;
            case PrivSmode2:
                _smode2 = value;
                break;
            case PrivSrfsh:
                _srfsh = value;
                break;
            case PrivSynch1:
                _synch1 = value;
                break;
            case PrivSynch2:
                _synch2 = value;
                break;
            case PrivSyncv:
                _syncv = value;
                break;
            case PrivDispFb1:
                _dispfb1 = value;
                break;
            case PrivDisplay1:
                _display1 = value;
                break;
            case PrivDispFb2:
                _dispfb2 = value;
                break;
            case PrivDisplay2:
                _display2 = value;
                break;
            case PrivExtBuf:
                _extbuf = value;
                break;
            case PrivExtData:
                _extdata = value;
                break;
            case PrivExtWrite:
                _extwrite = value;
                break;
            case PrivBgColor:
                _bgcolor = value;
                break;
        }
    }

    /// <summary>
    /// A 32-bit write: offsets ending in 4 replace the upper half. CSR and IMR act on their lower half only (their
    /// upper halves hold nothing writable), so a 32-bit write there is a whole write.
    /// </summary>
    public void WritePrivileged32(uint offset, uint value)
    {
        var aligned = offset & ~7u;
        var high = (offset & 4) != 0;
        switch (aligned & 0x13F0)
        {
            case PrivCsr:
            case PrivImr:
                if (!high)
                {
                    WritePrivileged(aligned, value);
                }

                return;
        }

        var current = ReadPrivileged(aligned);
        var merged = high
            ? (current & 0xFFFF_FFFF) | ((ulong)value << 32)
            : (current & 0xFFFF_FFFF_0000_0000) | value;
        WritePrivileged(aligned, merged);
    }

    private void WriteCsr(ulong value)
    {
        // Event bits are cleared by writing 1 (ps2tek: "GS Privileged Registers", CSR).
        _csr &= ~(value & EventMask);
        if ((value & CsrReset) != 0)
        {
            _pmode = 0;
            _dispfb1 = _dispfb2 = 0;
            _csr &= CsrField | CsrNField;
            _signalStalled = false;
            _queueCount = 0;
            ResetTransfer();
        }

        if ((value & CsrSignal) != 0 && _signalStalled)
        {
            _signalStalled = false;
            ApplySignal(_pendingSignal);
            StallCleared?.Invoke();
        }
    }

    private void WriteImr(ulong value)
    {
        var was = InterruptPending;
        _imr = value & 0x7F00;
        if (!was && InterruptPending)
        {
            InterruptRaised?.Invoke();
        }
    }

    // ---- Save states ------------------------------------------------------------------------------------------

    public void SaveState(StateWriter writer)
    {
        Flush();
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_memory);
        foreach (var r in _regs)
        {
            writer.WriteUInt64(r);
        }

        writer.WriteUInt64(_pmode);
        writer.WriteUInt64(_smode1);
        writer.WriteUInt64(_smode2);
        writer.WriteUInt64(_srfsh);
        writer.WriteUInt64(_synch1);
        writer.WriteUInt64(_synch2);
        writer.WriteUInt64(_syncv);
        writer.WriteUInt64(_dispfb1);
        writer.WriteUInt64(_display1);
        writer.WriteUInt64(_dispfb2);
        writer.WriteUInt64(_display2);
        writer.WriteUInt64(_extbuf);
        writer.WriteUInt64(_extdata);
        writer.WriteUInt64(_extwrite);
        writer.WriteUInt64(_bgcolor);
        writer.WriteUInt64(_csr);
        writer.WriteUInt64(_imr);
        writer.WriteUInt64(_busdir);
        writer.WriteUInt64(_siglblid);
        writer.WriteBool(_signalStalled);
        writer.WriteUInt64(_pendingSignal);
        foreach (var c in _clut)
        {
            writer.WriteUInt16(c);
        }

        writer.WriteInt32(_cbp0);
        writer.WriteInt32(_cbp1);
        SaveDrawing(writer);
        SaveTransfer(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        DiscardBatch();
        reader.BeginChunk(Tag);
        reader.ReadBytes(_memory);
        for (var i = 0; i < _regs.Length; i++)
        {
            _regs[i] = reader.ReadUInt64();
        }

        _pmode = reader.ReadUInt64();
        _smode1 = reader.ReadUInt64();
        _smode2 = reader.ReadUInt64();
        _srfsh = reader.ReadUInt64();
        _synch1 = reader.ReadUInt64();
        _synch2 = reader.ReadUInt64();
        _syncv = reader.ReadUInt64();
        _dispfb1 = reader.ReadUInt64();
        _display1 = reader.ReadUInt64();
        _dispfb2 = reader.ReadUInt64();
        _display2 = reader.ReadUInt64();
        _extbuf = reader.ReadUInt64();
        _extdata = reader.ReadUInt64();
        _extwrite = reader.ReadUInt64();
        _bgcolor = reader.ReadUInt64();
        _csr = reader.ReadUInt64();
        _imr = reader.ReadUInt64();
        _busdir = reader.ReadUInt64();
        _siglblid = reader.ReadUInt64();
        _signalStalled = reader.ReadBool();
        _pendingSignal = reader.ReadUInt64();
        for (var i = 0; i < _clut.Length; i++)
        {
            _clut[i] = reader.ReadUInt16();
        }

        _cbp0 = reader.ReadInt32();
        _cbp1 = reader.ReadInt32();
        LoadDrawing(reader);
        LoadTransfer(reader);
        reader.EndChunk();
        Backend?.Resync();
        _drawStateDirty = true;
    }
}
