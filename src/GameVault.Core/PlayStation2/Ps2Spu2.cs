using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The PlayStation 2's SPU2 (Phase 17): two PlayStation-SPU-like cores of 24 voices each sharing 2 MB of sound RAM,
/// with SPU-ADPCM voices (Gaussian interpolation, ADSR, sweep volumes, pitch modulation, per-core noise), each core's
/// reverb in its own work area, the AutoDMA (ADMA) PCM input, core 0's output mixed into core 1, and the IRQ address
/// shared by both cores. One stereo 48 kHz sample comes out of core 1 per <see cref="Tick"/>.
/// </summary>
/// <remarks>
/// References: ps2tek "SPU2" (register base, ADMA and MEMIN), psx-spx for the parts shared with the PlayStation's SPU,
/// and the register map and behaviour Play! (Source/iop/Iop_Spu2_Core.cpp) and PCSX2 (pcsx2/SPU2, facts only) agree
/// on. Sound RAM addresses are in halfwords, as the hardware's registers give them. Transfers complete at once (STATX
/// never shows a busy transfer); the reverb runs both sides on alternate samples (24 kHz) without the hardware's
/// resampling filters; ATTR's mute and enable bits are kept but, as in PCSX2, have no effect.
/// </remarks>
public sealed partial class Ps2Spu2 : IPs2Spu2
{
    public const int SampleRate = 48000;

    /// <summary>Sound RAM in bytes (2 MB).</summary>
    public const int RamSize = 2 * 1024 * 1024;

    private const uint AddressMask = (RamSize / 2) - 1; // halfword addresses
    private const int VoiceCount = 24;
    private const int CoreCount = 2;

    // Per-core register offsets (core 1 adds $400).
    private const int RegPmon = 0x180;
    private const int RegNon = 0x184;
    private const int RegVmixL = 0x188;
    private const int RegVmixEL = 0x18C;
    private const int RegVmixR = 0x190;
    private const int RegVmixER = 0x194;
    private const int RegMmix = 0x198;
    private const int RegAttr = 0x19A;
    private const int RegIrqa = 0x19C;
    private const int RegKon = 0x1A0;
    private const int RegKoff = 0x1A4;
    private const int RegTsa = 0x1A8;
    private const int RegStd = 0x1AC;
    private const int RegAdmas = 0x1B0;
    private const int RegVoiceAddress = 0x1C0;
    private const int RegEsa = 0x2E0;
    private const int RegReverbAddress = 0x2E4;
    private const int RegEea = 0x33C;
    private const int RegEndx = 0x340;
    private const int RegStatx = 0x344;

    // The "different" area at $760 + core × $28.
    private const int ExtBase = 0x760;
    private const int ExtSize = 0x28;
    private const int ExtMvolL = 0x00;
    private const int ExtMvolR = 0x02;
    private const int ExtEvolL = 0x04;
    private const int ExtEvolR = 0x06;
    private const int ExtAvolL = 0x08;
    private const int ExtAvolR = 0x0A;
    private const int ExtBvolL = 0x0C;
    private const int ExtBvolR = 0x0E;
    private const int ExtMvolXL = 0x10;
    private const int ExtMvolXR = 0x12;
    private const int ExtReverbCoefficients = 0x14; // IIR, COMB1–4, WALL, APF1, APF2, IN_COEF_L, IN_COEF_R

    // Shared registers.
    private const int RegSpdifIrqInfo = 0x7C2;

    // ATTR bits.
    private const ushort AttrDmaMode = 3 << 4;
    private const ushort AttrIrqEnable = 1 << 6;
    private const ushort AttrEffectsEnable = 1 << 7;

    private static readonly StateTag Tag = StateTag.From("SPU2");

    private readonly byte[] _ram = new byte[RamSize];

    /// <summary>Every register as last written ($000–$7FE), for the ones read back as written.</summary>
    private readonly ushort[] _registers = new ushort[0x400];

    private readonly Core[] _cores = [new Core(), new Core()];

    /// <summary>The position in the 512-sample output/input buffers of sound RAM ($000–$1FF).</summary>
    private int _bufferPosition;

    /// <summary>SPDIF_IRQINFO: bit 2 for core 0's IRQ, bit 3 for core 1's.</summary>
    private ushort _irqInfo;

    public Ps2Spu2()
    {
        Reset();
    }

    public Action<short, short>? SampleReady { get; set; }

    public Action? Irq { get; set; }

    /// <summary>The 2 MB of sound RAM (halfwords, little-endian).</summary>
    public ReadOnlySpan<byte> Ram => _ram;

    /// <summary>The latest stereo output sample.</summary>
    public short Left { get; private set; }

    public short Right { get; private set; }

    /// <summary>
    /// Power-on state as PCSX2 documents the hardware's (V_Core::Init, "PS2 confirmed" values): every ENDX bit set,
    /// MMIX $FF0/$FFC, all voices mixed, the effects areas parked at the top of RAM, IRQA $800 with IRQs off.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_ram);
        Array.Clear(_registers);
        _bufferPosition = 0;
        _irqInfo = 0;
        Left = Right = 0;

        for (var c = 0; c < CoreCount; c++)
        {
            var core = _cores[c];
            core.Reset();
            var b = c * 0x200; // index of the core's registers in _registers
            _registers[b + (RegMmix >> 1)] = (ushort)(c == 0 ? 0xFF0 : 0xFFC);
            foreach (var mask in (ReadOnlySpan<int>)[RegVmixL, RegVmixEL, RegVmixR, RegVmixER])
            {
                _registers[b + (mask >> 1)] = 0xFFFF;
                _registers[b + (mask >> 1) + 1] = 0xFF;
            }

            _registers[b + (RegIrqa >> 1) + 1] = 0x800;
            _registers[b + (RegEsa >> 1)] = (ushort)(c == 0 ? 0xE : 0xF);
            _registers[b + (RegEsa >> 1) + 1] = 0xFFF8;
            _registers[b + (RegEea >> 1)] = (ushort)(c == 0 ? 0xE : 0xF);

            var e = (ExtBase + c * ExtSize) >> 1;
            _registers[e + (ExtAvolL >> 1)] = 0x7FFF;
            _registers[e + (ExtAvolR >> 1)] = 0x7FFF;
            _registers[e + (ExtBvolL >> 1)] = 0x7FFF;
            _registers[e + (ExtBvolR >> 1)] = 0x7FFF;

            for (var v = 0; v < VoiceCount; v++)
            {
                var voice = core.Voices[v];
                voice.Pitch = 0x3FFF;
                voice.StartAddress = voice.RepeatAddress = voice.CurrentAddress = 0x2800;
                _registers[b + ((v << 4) + 4 >> 1)] = 0x3FFF;
            }
        }
    }

    // ---- Register helpers ----

    private ushort Reg(int core, int offset) => _registers[(core * 0x400 + offset) >> 1];

    /// <summary>A 24-voice bit mask kept in two registers: voices 0–15 at the lower address, 16–23 above.</summary>
    private uint VoiceMask(int core, int offset) => Reg(core, offset) | (uint)(Reg(core, offset + 2) & 0xFF) << 16;

    /// <summary>An address kept in two registers: the high bits at the lower address, the low 16 above.</summary>
    private uint AddressPair(int core, int offset) => (uint)Reg(core, offset) << 16 | Reg(core, offset + 2);

    private short Ext(int core, int offset) => (short)_registers[(ExtBase + core * ExtSize + offset) >> 1];

    private uint IrqAddress(int core) => AddressPair(core, RegIrqa);

    private static uint SetHigh(uint address, ushort value) => (address & 0xFFFF) | (uint)(value & 0xF) << 16;

    private static uint SetLow(uint address, ushort value) => (address & 0xF0000) | value;

    // ---- Register reads ----

    public ushort Read16(uint offset)
    {
        offset &= 0x7FE;
        if (offset < ExtBase)
        {
            return ReadCore((int)(offset >> 10) & 1, (int)(offset & 0x3FF), offset);
        }

        if (offset < ExtBase + CoreCount * ExtSize)
        {
            var core = (int)(offset - ExtBase) / ExtSize;
            var local = (int)(offset - ExtBase) % ExtSize;
            return local switch
            {
                ExtMvolXL => (ushort)_cores[core].MasterLeft.Level,
                ExtMvolXR => (ushort)_cores[core].MasterRight.Level,
                _ => _registers[offset >> 1],
            };
        }

        return offset == RegSpdifIrqInfo ? _irqInfo : _registers[offset >> 1];
    }

    private ushort ReadCore(int c, int local, uint offset)
    {
        var core = _cores[c];
        if (local < RegPmon)
        {
            var voice = core.Voices[local >> 4];
            return (local & 0xF) switch
            {
                0xA => (ushort)voice.Envelope,
                0xC => (ushort)voice.VolumeLeft.Level,
                0xE => (ushort)voice.VolumeRight.Level,
                _ => _registers[offset >> 1],
            };
        }

        if (local is >= RegVoiceAddress and < RegEsa)
        {
            var voice = core.Voices[(local - RegVoiceAddress) / 12];
            var address = ((local - RegVoiceAddress) % 12) switch
            {
                0 or 2 => voice.StartAddress,
                4 or 6 => voice.RepeatAddress,
                _ => voice.NextAddress,
            };
            return (local & 2) == 0 ? (ushort)(address >> 16) : (ushort)address;
        }

        return local switch
        {
            RegTsa => (ushort)(core.TransferAddress >> 16),
            RegTsa + 2 => (ushort)core.TransferAddress,
            RegEndx => (ushort)core.Endx,
            RegEndx + 2 => (ushort)(core.Endx >> 16),
            RegStatx => core.Statx,
            _ => _registers[offset >> 1],
        };
    }

    // ---- Register writes ----

    public void Write16(uint offset, ushort value)
    {
        offset &= 0x7FE;
        if (offset < ExtBase)
        {
            WriteCore((int)(offset >> 10) & 1, (int)(offset & 0x3FF), offset, value);
            return;
        }

        if (offset < ExtBase + CoreCount * ExtSize)
        {
            var core = (int)(offset - ExtBase) / ExtSize;
            switch ((int)(offset - ExtBase) % ExtSize)
            {
                case ExtMvolL:
                    _cores[core].MasterLeft.Set(value);
                    break;
                case ExtMvolR:
                    _cores[core].MasterRight.Set(value);
                    break;
                case ExtMvolXL or ExtMvolXR:
                    return; // the current master volume is read-only (PCSX2: "MVOLX ... not allowed to be written")
            }

            _registers[offset >> 1] = value;
            return;
        }

        _registers[offset >> 1] = value;
        if (offset == RegSpdifIrqInfo)
        {
            _irqInfo = (ushort)(value & 0xC);
        }
    }

    private void WriteCore(int c, int local, uint offset, ushort value)
    {
        var core = _cores[c];
        if (local < RegPmon)
        {
            _registers[offset >> 1] = value;
            WriteVoice(core.Voices[local >> 4], local & 0xF, value);
            return;
        }

        if (local is >= RegVoiceAddress and < RegEsa)
        {
            _registers[offset >> 1] = value;
            WriteVoiceAddress(core.Voices[(local - RegVoiceAddress) / 12], (local - RegVoiceAddress) % 12, value);
            return;
        }

        switch (local)
        {
            case RegAttr:
                WriteAttr(c, value);
                break;
            case RegKon:
                core.PendingKeyOn |= value;
                break;
            case RegKon + 2:
                core.PendingKeyOn |= (uint)(value & 0xFF) << 16;
                break;
            case RegKoff:
                core.PendingKeyOff |= value;
                break;
            case RegKoff + 2:
                core.PendingKeyOff |= (uint)(value & 0xFF) << 16;
                break;
            case RegTsa:
                core.TransferAddress = SetHigh(core.TransferAddress, value);
                break;
            case RegTsa + 2:
                core.TransferAddress = SetLow(core.TransferAddress, value);
                break;
            case RegStd:
                // The data port: a write to sound RAM at TSA, which then advances (PCSX2: REG__1AC).
                CheckIrq(core.TransferAddress);
                WriteRam(core.TransferAddress, value);
                core.TransferAddress = (core.TransferAddress + 1) & AddressMask;
                break;
            case RegAdmas:
                WriteAdmas(c, value);
                break;
            case RegEndx:
                core.Endx &= 0xFF0000; // any write clears the voices' flags (PCSX2: REG_S_ENDX)
                break;
            case RegEndx + 2:
                core.Endx &= 0xFFFF;
                break;
            case RegStatx:
                return; // read-only
        }

        _registers[offset >> 1] = value;
    }

    private static void WriteVoice(Voice voice, int register, ushort value)
    {
        switch (register)
        {
            case 0x0:
                voice.VolumeLeft.Set(value);
                break;
            case 0x2:
                voice.VolumeRight.Set(value);
                break;
            case 0x4:
                voice.Pitch = value;
                break;
            case 0x6:
                voice.Adsr = (voice.Adsr & 0xFFFF0000) | value;
                break;
            case 0x8:
                voice.Adsr = (voice.Adsr & 0xFFFF) | (uint)value << 16;
                break;
            case 0xA:
                voice.Envelope = (short)value; // ENVX is writable; VOLXL/VOLXR are not (PCSX2: RegWrite_VoiceParams)
                break;
        }
    }

    /// <summary>SSA, LSAX and NAX: 20-bit halfword addresses, aligned to the 8-halfword ADPCM block.</summary>
    private void WriteVoiceAddress(Voice voice, int register, ushort value)
    {
        var low = (ushort)(value & 0xFFF8);
        switch (register)
        {
            case 0:
                voice.StartAddress = SetHigh(voice.StartAddress, value);
                break;
            case 2:
                voice.StartAddress = SetLow(voice.StartAddress, low);
                break;
            case 4:
                voice.RepeatAddress = SetHigh(voice.RepeatAddress, value);
                voice.IgnoreLoopStart = true;
                break;
            case 6:
                voice.RepeatAddress = SetLow(voice.RepeatAddress, low);
                voice.IgnoreLoopStart = true;
                break;
            case 8:
                // NAX is writable: the decoder moves to that block at once (PCSX2: RegWrite_VoiceAddr, FlatOut).
                voice.CurrentAddress = SetHigh(voice.CurrentAddress, value);
                DecodeBlock(voice, keepHistory: true);
                break;
            case 10:
                voice.CurrentAddress = SetLow(voice.CurrentAddress, low);
                DecodeBlock(voice, keepHistory: true);
                break;
        }
    }

    /// <summary>
    /// ATTR: bit 0 unknown, 1–3 DMA bits, 4–5 the DMA mode, 6 IRQ enable, 7 effects enable, 8–13 the noise clock,
    /// 14 mute, 15 core enable. Clearing the IRQ enable acknowledges the core's IRQ (PCSX2: REG_C_ATTR).
    /// </summary>
    private void WriteAttr(int c, ushort value)
    {
        var core = _cores[c];
        var old = Reg(c, RegAttr);
        if ((value & AttrIrqEnable) == 0)
        {
            _irqInfo &= (ushort)~(4 << c);
        }

        // STATX bit 7 (DMA ready) follows the DMA mode.
        if ((value & AttrDmaMode) == 0)
        {
            core.Statx &= unchecked((ushort)~0x80);
        }
        else if ((old & AttrDmaMode) == 0)
        {
            core.Statx |= 0x80;
        }
    }

    // ---- Sound RAM ----

    private short ReadRam(uint address)
    {
        var i = (int)(address & AddressMask) << 1;
        return (short)(_ram[i] | _ram[i + 1] << 8);
    }

    private void WriteRam(uint address, int value)
    {
        var i = (int)(address & AddressMask) << 1;
        _ram[i] = (byte)value;
        _ram[i + 1] = (byte)(value >> 8);
    }

    // ---- IRQ ----

    /// <summary>
    /// An access to <paramref name="address"/>: each core whose IRQ is enabled and whose IRQA matches raises its IRQ
    /// (IRQINFO bit 2 + core), once until acknowledged. Either core's accesses trigger either core's IRQ (PCSX2).
    /// </summary>
    private void CheckIrq(uint address)
    {
        for (var c = 0; c < CoreCount; c++)
        {
            if (IrqAddress(c) == address)
            {
                RaiseIrq(c);
            }
        }
    }

    /// <summary>A voice fetching an ADPCM block touches all eight of its halfwords.</summary>
    private void CheckBlockIrq(uint blockAddress)
    {
        for (var c = 0; c < CoreCount; c++)
        {
            if (IrqAddress(c) - blockAddress < 8)
            {
                RaiseIrq(c);
            }
        }
    }

    private void RaiseIrq(int c)
    {
        var bit = (ushort)(4 << c);
        if ((Reg(c, RegAttr) & AttrIrqEnable) == 0 || (_irqInfo & bit) != 0)
        {
            return;
        }

        _irqInfo |= bit;
        Irq?.Invoke();
    }

    // ---- Transfers ----

    private bool AdmaEnabled(int c) => (Reg(c, RegAdmas) & (1 << c)) != 0;

    /// <summary>
    /// ADMAS: bit 0 on core 0, bit 1 on core 1 turns AutoDMA on (ps2tek: "SPU2 AutoDMA"). The first block goes to the
    /// half buffer not being played; stopping AutoDMA clears the core's input buffers (as PCSX2 does).
    /// </summary>
    private void WriteAdmas(int c, ushort value)
    {
        var core = _cores[c];
        var wasOn = AdmaEnabled(c);
        var on = (value & (1 << c)) != 0;
        if (on && !wasOn)
        {
            core.AdmaHalf = 1 - (_bufferPosition >> 8);
            core.AdmaFill = 0;
        }
        else if (!on && wasOn)
        {
            core.AdmaFill = 0;
            Array.Clear(_ram, (0x2000 + c * 0x400) * 2, 0x400 * 2);
        }
    }

    public int DmaWrite(int core, ReadOnlySpan<ushort> data)
    {
        core &= 1;
        if (AdmaEnabled(core))
        {
            return AdmaWrite(core, data);
        }

        // An address strictly inside the transfer raises the IRQ (PCSX2: FinishDMAwrite, for Chaos Legion's buffers).
        var spu = _cores[core];
        var address = spu.TransferAddress;
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                CheckIrq(address);
            }

            WriteRam(address, data[i]);
            address = (address + 1) & AddressMask;
        }

        FinishTransfer(core, address);
        return data.Length;
    }

    public int DmaRead(int core, Span<ushort> data)
    {
        core &= 1;
        var spu = _cores[core];
        var address = spu.TransferAddress;
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                CheckIrq(address);
            }

            data[i] = (ushort)ReadRam(address);
            address = (address + 1) & AddressMask;
        }

        FinishTransfer(core, address);
        return data.Length;
    }

    private void FinishTransfer(int c, uint address)
    {
        var core = _cores[c];
        core.TransferAddress = address;
        if ((Reg(c, RegAttr) & AttrDmaMode) != 0)
        {
            core.Statx |= 0x80;
        }
    }

    /// <summary>
    /// AutoDMA: blocks of 512 halfwords (256 left samples, then 256 right) into the core's MEMIN double buffer — left
    /// at $2000/$2100, right at $2200/$2300, plus $400 for core 1 (ps2tek: "MEMIN and Data Format"). A block starts
    /// only in the half the mixer isn't playing, so the DMA waits (0 halfwords taken) until a half is free.
    /// </summary>
    private int AdmaWrite(int c, ReadOnlySpan<ushort> data)
    {
        var core = _cores[c];
        var taken = 0;
        var baseAddress = 0x2000u + (uint)c * 0x400;
        while (taken < data.Length)
        {
            if (core.AdmaFill == 0 && core.AdmaHalf == _bufferPosition >> 8)
            {
                break;
            }

            var count = Math.Min(data.Length - taken, 512 - core.AdmaFill);
            var half = (uint)core.AdmaHalf * 0x100;
            for (var i = 0; i < count; i++)
            {
                var fill = (uint)(core.AdmaFill + i);
                var address = fill < 256 ? baseAddress + half + fill : baseAddress + 0x200 + half + (fill - 256);
                WriteRam(address, data[taken + i]);
            }

            taken += count;
            core.AdmaFill += count;
            if (core.AdmaFill == 512)
            {
                core.AdmaFill = 0;
                core.AdmaHalf ^= 1;
            }
        }

        return taken;
    }

    // ---- Save state ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBytes(_ram);
        foreach (var register in _registers)
        {
            writer.WriteUInt16(register);
        }

        writer.WriteInt32(_bufferPosition);
        writer.WriteUInt16(_irqInfo);
        writer.WriteUInt16((ushort)Left);
        writer.WriteUInt16((ushort)Right);
        foreach (var core in _cores)
        {
            core.Save(writer);
        }

        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        reader.ReadBytes(_ram);
        for (var i = 0; i < _registers.Length; i++)
        {
            _registers[i] = reader.ReadUInt16();
        }

        _bufferPosition = reader.ReadInt32() & 0x1FF;
        _irqInfo = (ushort)(reader.ReadUInt16() & 0xC);
        Left = (short)reader.ReadUInt16();
        Right = (short)reader.ReadUInt16();
        foreach (var core in _cores)
        {
            core.Load(reader);
        }

        reader.EndChunk();
    }
}
