using System.Buffers.Binary;
using GameVault.Core.State;

namespace GameVault.Core.Nintendo64;

/// <summary>The physical memory map and the RCP's interfaces (n64brew: "Memory map" and each interface's page).</summary>
public sealed partial class N64Console
{
    // VI registers, by index (n64brew: "Video Interface").
    private const int ViControl = 0;
    private const int ViOrigin = 1;
    private const int ViWidth = 2;
    private const int ViVInterrupt = 3;
    private const int ViVCurrent = 4;
    private const int ViBurst = 5;
    private const int ViVSync = 6;
    private const int ViHSync = 7;
    private const int ViLeap = 8;
    private const int ViHVideo = 9;
    private const int ViVVideo = 10;
    private const int ViVBurst = 11;
    private const int ViXScale = 12;
    private const int ViYScale = 13;

    // How long the serial interface takes to move 64 bytes to or from the PIF, in CPU cycles (approximate).
    private const int SiDmaCycles = 4000;

    private readonly uint[] _vi = new uint[14];
    private readonly uint[] _ri = new uint[8];
    private readonly uint[] _rdramRegisters = new uint[10];
    private readonly uint[] _piTiming = new uint[8];

    // VI timing.
    private int _viLine;
    private int _viField;
    private long _viNextLineCycle;
    private double _viLineFraction;

    // PI.
    private uint _piDramAddress;
    private uint _piCartAddress;
    private bool _piBusy;
    private long _piDoneCycle;
    private bool _piError;

    // SI.
    private uint _siDramAddress;
    private bool _siBusy;
    private long _siDoneCycle;

    // AI: the buffer playing and the one queued behind it.
    private readonly uint[] _aiAddress = new uint[2];
    private readonly uint[] _aiLength = new uint[2];
    private int _aiCount;
    private uint _aiDramAddress;
    private uint _aiControl;
    private uint _aiDacRate;
    private uint _aiBitRate;
    private long _aiNextSampleCycle;
    private double _aiSampleFraction;
    private long _aiBufferEndCycle;
    private float _aiLastLevel;

    // FlashRAM (n64brew: "FlashRAM").
    private FlashMode _flashMode;
    private uint _flashStatus;
    private uint _flashOffset;
    private readonly byte[] _flashPage = new byte[128];

    // The IS-Viewer's buffer.
    private readonly byte[] _isViewer = new byte[0x200];

    private enum FlashMode
    {
        Read,
        Status,
        Erase,
        Write,
    }

    private void ResetIo()
    {
        _miMode = 0;
        _miInterrupts = 0;
        _miMask = 0;
        Array.Clear(_vi);
        Array.Clear(_rdramRegisters);
        _ri[0] = 0x0E; // RI_MODE
        _ri[1] = 0x40; // RI_CONFIG
        _ri[2] = 0x00; // RI_CURRENT_LOAD
        _ri[3] = 0x14; // RI_SELECT: RDRAM already initialized, so IPL3 skips that
        _ri[4] = 0x0006_3634; // RI_REFRESH
        _ri[5] = _ri[6] = _ri[7] = 0;
        _viLine = _viField = 0;
        _viLineFraction = 0;
        _piDramAddress = _piCartAddress = 0;
        _piBusy = _piError = false;
        _siDramAddress = 0;
        _siBusy = false;
        _aiCount = 0;
        _aiDramAddress = _aiControl = _aiDacRate = _aiBitRate = 0;
        _aiSampleFraction = 0;
        _aiLastLevel = 0;
        _flashMode = FlashMode.Read;
        _flashStatus = 0;
        _flashOffset = 0;
        _rcpReads = 0;

        // The PIF sets domain 1's timing from the ROM's first word (n64brew: "ROM Header").
        var rom = Cartridge.Rom;
        Array.Clear(_piTiming);
        _piTiming[0] = rom[3];
        _piTiming[1] = rom[2];
        _piTiming[2] = (uint)(rom[1] & 0x0F);
        _piTiming[3] = (uint)(rom[1] >> 4);
        Cpu.InterruptLine = false;
    }

    // ---- The CPU's bus ----

    /// <summary>
    /// A read that leaves the CPU for the RCP's bus stalls it: about 22 cycles for a register or the RSP's memories,
    /// as PeterLemon's timing programs measure a VI register read (approximate).
    /// </summary>
    private const int RcpReadCycles = 22;

    /// <summary>
    /// Every fifth read over the RCP's bus waits one more cycle: on average the extra fifth of a cycle PeterLemon's
    /// timing programs measure, which comes from the bus being shared (with the VI's framebuffer reads, among others).
    /// </summary>
    private int _rcpReads;

    public uint Read32(uint address)
    {
        Cpu.Cycles += RcpReadCycles;
        if (++_rcpReads == 5)
        {
            _rcpReads = 0;
            Cpu.Cycles++;
        }
        if ((address & 3) != 0 && address is < 0x1000_0000 or >= 0x1FC0_0000)
        {
            address &= ~3u; // only the cartridge bus reads from a halfword address
        }

        if (address < 0x0400_0000)
        {
            if (address is >= 0x03F0_0000)
            {
                return _rdramRegisters[(address >> 2) % (uint)_rdramRegisters.Length];
            }

            return 0; // past the end of RDRAM
        }

        if (address < 0x0404_0000)
        {
            CatchUpRsp();
            var memory = (address & 0x1000) != 0 ? Rsp.Imem : Rsp.Dmem;
            return BinaryPrimitives.ReadUInt32BigEndian(memory.AsSpan((int)(address & 0xFFC)));
        }

        if (address < 0x0410_0000)
        {
            CatchUpRsp();
            return Rsp.ReadRegister(address);
        }

        if (address < 0x0420_0000)
        {
            return Rdp.ReadRegister(address);
        }

        switch (address >> 20)
        {
            case 0x043:
                return ReadMi(address);
            case 0x044:
                return ReadVi(address);
            case 0x045:
                return ReadAi(address);
            case 0x046:
                return ReadPi(address);
            case 0x047:
                return _ri[(address >> 2) & 7];
            case 0x048:
                return ReadSi(address);
        }

        if (address is >= 0x0800_0000 and < 0x1000_0000)
        {
            return ReadCartRam(address);
        }

        if (address is >= 0x1000_0000 and < 0x1FC0_0000)
        {
            if (DebugOutput is not null && address is >= 0x13FF_0000 and < 0x1400_0000)
            {
                return address == 0x13FF_0000 ? 0x4953_3634u : ReadIsViewer(address); // "IS64"
            }

            _piCartAddress = address + 4; // the PI's address register follows the CPU's reads (PeterLemon's DMAAlignment-PI)
            return ReadRom(address - 0x1000_0000);
        }

        if (address is >= 0x1FC0_07C0 and < 0x1FC0_0800)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(Pif.Ram.AsSpan((int)(address & 0x3C)));
        }

        return 0;
    }

    public ulong Read64(uint address) => ((ulong)Read32(address) << 32) | Read32(address + 4);

    public void Write8(uint address, byte value)
    {
        if (address is >= 0x0400_0000 and < 0x0404_0000)
        {
            CatchUpRsp();
            var memory = (address & 0x1000) != 0 ? Rsp.Imem : Rsp.Dmem;
            memory[address & 0xFFF] = value;
            return;
        }

        // Narrow writes to registers reach them as a 32-bit write, shifted into place.
        var shift = (int)(24 - ((address & 3) << 3));
        Write32(address & ~3u, (uint)value << shift);
    }

    public void Write16(uint address, ushort value)
    {
        if (address is >= 0x0400_0000 and < 0x0404_0000)
        {
            CatchUpRsp();
            var memory = (address & 0x1000) != 0 ? Rsp.Imem : Rsp.Dmem;
            BinaryPrimitives.WriteUInt16BigEndian(memory.AsSpan((int)(address & 0xFFE)), value);
            return;
        }

        var shift = (int)(16 - ((address & 2) << 3));
        Write32(address & ~3u, (uint)value << shift);
    }

    public void Write32(uint address, uint value)
    {
        if (address < 0x0400_0000)
        {
            if (address >= 0x03F0_0000)
            {
                _rdramRegisters[(address >> 2) % (uint)_rdramRegisters.Length] = value;
            }

            return;
        }

        if (address < 0x0404_0000)
        {
            CatchUpRsp();
            var memory = (address & 0x1000) != 0 ? Rsp.Imem : Rsp.Dmem;
            BinaryPrimitives.WriteUInt32BigEndian(memory.AsSpan((int)(address & 0xFFC)), value);
            return;
        }

        if (address < 0x0410_0000)
        {
            CatchUpRsp();
            Rsp.WriteRegister(address, value);
            UpdateNextEvent(); // the RSP may have started
            return;
        }

        if (address < 0x0420_0000)
        {
            Rdp.WriteRegister(address, value);
            return;
        }

        switch (address >> 20)
        {
            case 0x043:
                WriteMi(address, value);
                return;
            case 0x044:
                WriteVi(address, value);
                return;
            case 0x045:
                WriteAi(address, value);
                return;
            case 0x046:
                WritePi(address, value);
                return;
            case 0x047:
                _ri[(address >> 2) & 7] = value;
                return;
            case 0x048:
                WriteSi(address, value);
                return;
        }

        if (address is >= 0x0800_0000 and < 0x1000_0000)
        {
            WriteCartRam(address, value);
            return;
        }

        if (DebugOutput is not null && address is >= 0x13FF_0000 and < 0x1400_0000)
        {
            WriteIsViewer(address, value);
            return;
        }

        if (address is >= 0x1FC0_07C0 and < 0x1FC0_0800)
        {
            BinaryPrimitives.WriteUInt32BigEndian(Pif.Ram.AsSpan((int)(address & 0x3C)), value);
            Pif.Written();
        }
    }

    public void Write64(uint address, ulong value)
    {
        Write32(address, (uint)(value >> 32));
        Write32(address + 4, (uint)value);
    }

    // ---- MI ----

    private uint ReadMi(uint address) => (address & 0xF) switch
    {
        0x0 => _miMode,
        0x4 => 0x0202_0102, // MI_VERSION
        0x8 => _miInterrupts,
        0xC => _miMask,
        _ => 0,
    };

    private void WriteMi(uint address, uint value)
    {
        switch (address & 0xF)
        {
            case 0x0: // MI_MODE: init length, and set/clear bits for init, ebus and RDRAM register modes
                _miMode = (_miMode & ~0x7Fu) | (value & 0x7F);
                if ((value & 0x80) != 0) _miMode &= ~0x80u;
                if ((value & 0x100) != 0) _miMode |= 0x80;
                if ((value & 0x200) != 0) _miMode &= ~0x100u;
                if ((value & 0x400) != 0) _miMode |= 0x100;
                if ((value & 0x800) != 0) ClearInterrupt(N64Interrupt.Dp);
                if ((value & 0x1000) != 0) _miMode &= ~0x200u;
                if ((value & 0x2000) != 0) _miMode |= 0x200;
                break;
            case 0xC: // MI_MASK: a clear and a set bit for each interrupt
                for (var i = 0; i < 6; i++)
                {
                    if ((value & (1u << (i * 2))) != 0)
                    {
                        _miMask &= ~(1u << i);
                    }

                    if ((value & (2u << (i * 2))) != 0)
                    {
                        _miMask |= 1u << i;
                    }
                }

                Cpu.InterruptLine = (_miInterrupts & _miMask) != 0;
                break;
        }
    }

    // ---- VI ----

    private uint ReadVi(uint address)
    {
        var index = (int)((address & 0x3F) >> 2);
        if (index >= _vi.Length)
        {
            return 0;
        }

        if (index == ViVCurrent)
        {
            return (uint)((_viLine << 1) | ((_vi[ViControl] & 0x40) != 0 ? _viField : 0));
        }

        return _vi[index];
    }

    private void WriteVi(uint address, uint value)
    {
        var index = (int)((address & 0x3F) >> 2);
        if (index >= _vi.Length)
        {
            return;
        }

        if (index == ViVCurrent)
        {
            ClearInterrupt(N64Interrupt.Vi);
            return;
        }

        _vi[index] = value;
        if (index is ViHSync or ViVSync)
        {
            ScheduleVi();
            UpdateNextEvent();
        }
    }

    /// <summary>A line's length in CPU cycles: H_SYNC is the line's length in VI clocks, less one.</summary>
    private double ViLineCycles
    {
        get
        {
            var hSync = (_vi[ViHSync] & 0xFFF) + 1;
            return (hSync <= 1 ? 3094 : hSync) * CpuClock / ViClock;
        }
    }

    /// <summary>Lines in a field: V_SYNC counts half-lines, less one.</summary>
    private int ViLines
    {
        get
        {
            var halfLines = (int)(_vi[ViVSync] & 0x3FF) + 1;
            return Math.Max((halfLines <= 1 ? 526 : halfLines) / 2, 16);
        }
    }

    private void ScheduleVi()
    {
        var cycles = ViLineCycles + _viLineFraction;
        var whole = (long)cycles;
        _viLineFraction = cycles - whole;
        _viNextLineCycle = Cpu.Cycles + whole;
    }

    /// <summary>The end of a line: the next one starts, with the V interrupt on its line and a new field after the last.</summary>
    private void ViLine()
    {
        _viLine++;
        if (_viLine >= ViLines)
        {
            _viLine = 0;
            if ((_vi[ViControl] & 0x40) != 0)
            {
                _viField ^= 1;
            }

            CaptureFrame();
            _frameDone = true;
        }

        if (_viLine == (int)((_vi[ViVInterrupt] & 0x3FF) >> 1))
        {
            RaiseInterrupt(N64Interrupt.Vi);
        }

        var cycles = ViLineCycles + _viLineFraction;
        var whole = (long)cycles;
        _viLineFraction = cycles - whole;
        _viNextLineCycle += whole;
    }

    // ---- AI ----

    private uint ReadAi(uint address)
    {
        CatchUpAudio(Cpu.Cycles);
        return (address & 0x1F) switch
        {
            0x04 => _aiCount > 0 ? RemainingAudioBytes() : 0, // AI_LENGTH: what's left of the playing buffer
            0x0C => (_aiCount > 1 ? 0x8000_0001u : 0) | (_aiCount > 0 ? 0x4000_0000u : 0) | 0x0110_0000, // AI_STATUS
            _ => 0,
        };
    }

    private void WriteAi(uint address, uint value)
    {
        CatchUpAudio(Cpu.Cycles);
        switch (address & 0x1F)
        {
            case 0x00:
                _aiDramAddress = value & 0x00FF_FFF8;
                break;
            case 0x04:
            {
                var length = value & 0x3_FFF8;
                if (length == 0 || _aiCount == 2)
                {
                    break;
                }

                _aiAddress[_aiCount] = _aiDramAddress;
                _aiLength[_aiCount] = length;
                _aiCount++;
                if (_aiCount == 1)
                {
                    StartAudioBuffer();
                }

                break;
            }

            case 0x08:
                _aiControl = value & 1;
                break;
            case 0x0C:
                ClearInterrupt(N64Interrupt.Ai);
                break;
            case 0x10:
                _aiDacRate = value & 0x3FFF;
                break;
            case 0x14:
                _aiBitRate = value & 0xF;
                break;
        }

        UpdateNextEvent();
    }

    /// <summary>CPU cycles between two samples: the DAC runs at the VI clock divided by AI_DACRATE + 1.</summary>
    private double AudioSampleCycles => CpuClock * (_aiDacRate + 1) / ViClock;

    /// <summary>The buffer at the front of the queue starts playing, and the AI interrupt asks for the next one.</summary>
    private void StartAudioBuffer()
    {
        _aiSampleFraction = 0;
        _aiNextSampleCycle = Cpu.Cycles;
        _aiBufferEndCycle = Cpu.Cycles + (long)(_aiLength[0] / 4 * AudioSampleCycles);
        RaiseInterrupt(N64Interrupt.Ai);
    }

    private uint RemainingAudioBytes()
    {
        var left = (_aiBufferEndCycle - Cpu.Cycles) / AudioSampleCycles;
        return Math.Min(_aiLength[0], (uint)Math.Max(0, Math.Ceiling(left)) * 4);
    }

    /// <summary>Plays the samples due up to <paramref name="until"/>, moving to the queued buffer when one ends.</summary>
    private void CatchUpAudio(long until)
    {
        var audio = Audio;
        while (_aiCount > 0)
        {
            var sampleCycles = AudioSampleCycles;
            var samples = (int)(_aiLength[0] / 4);
            var end = _aiBufferEndCycle;
            while (_aiNextSampleCycle < until && _aiNextSampleCycle < end && samples > 0)
            {
                if (audio is not null && (_aiControl & 1) != 0)
                {
                    var at = (int)(_aiAddress[0] & (uint)(_rdram.Length - 1));
                    var left = (short)((_rdram[at] << 8) | _rdram[at + 1]);
                    var right = (short)((_rdram[at + 2] << 8) | _rdram[at + 3]);
                    var level = (left + right) / 65536f;
                    if (level != _aiLastLevel)
                    {
                        var clock = (int)((_aiNextSampleCycle - _frameStartCycle) / AudioClockDivider);
                        audio.AddDelta(Math.Max(clock, 0), level - _aiLastLevel);
                        _aiLastLevel = level;
                    }
                }

                _aiAddress[0] += 4;
                _aiLength[0] -= 4;
                samples--;
                var next = sampleCycles + _aiSampleFraction;
                var whole = (long)next;
                _aiSampleFraction = next - whole;
                _aiNextSampleCycle += whole;
            }

            if (until < end)
            {
                return;
            }

            // The buffer is done: the queued one starts.
            _aiAddress[0] = _aiAddress[1];
            _aiLength[0] = _aiLength[1];
            _aiCount--;
            if (_aiCount > 0)
            {
                var remainder = _aiNextSampleCycle;
                StartAudioBuffer();
                _aiNextSampleCycle = remainder;
                _aiBufferEndCycle = end + (long)(_aiLength[0] / 4 * sampleCycles);
            }
        }
    }

    private void EndAudioFrame()
    {
        var now = Cpu.Cycles;
        CatchUpAudio(now);
        Audio?.EndFrame((int)((now - _frameStartCycle) / AudioClockDivider));
        _frameStartCycle = now - (now - _frameStartCycle) % AudioClockDivider;
    }

    // ---- PI ----

    private uint ReadPi(uint address)
    {
        var offset = address & 0x3F;
        switch (offset)
        {
            case 0x00:
                return _piDramAddress;
            case 0x04:
                return _piCartAddress;
            case 0x08 or 0x0C: // the length registers read back as $7F (n64brew: "Peripheral Interface")
                return 0x7F;
            case 0x10:
                return (_piBusy ? 1u : 0u) | (_piError ? 4u : 0u) | ((_miInterrupts & (uint)N64Interrupt.Pi) != 0 ? 8u : 0u);
            case >= 0x14 and < 0x34:
                return _piTiming[(offset - 0x14) >> 2];
            default:
                return 0;
        }
    }

    private void WritePi(uint address, uint value)
    {
        var offset = address & 0x3F;
        switch (offset)
        {
            case 0x00:
                _piDramAddress = value & 0x00FF_FFFE;
                break;
            case 0x04:
                _piCartAddress = value & 0xFFFF_FFFE;
                break;
            case 0x08: // PI_RD_LEN: RDRAM to the cartridge
                PiDma(toRdram: false, (value & 0x00FF_FFFF) + 1);
                break;
            case 0x0C: // PI_WR_LEN: the cartridge to RDRAM
                PiDma(toRdram: true, (value & 0x00FF_FFFF) + 1);
                break;
            case 0x10:
                if ((value & 2) != 0)
                {
                    ClearInterrupt(N64Interrupt.Pi);
                }

                if ((value & 1) != 0)
                {
                    _piBusy = false;
                    _piError = false;
                }

                break;
            case >= 0x14 and < 0x34:
                _piTiming[(offset - 0x14) >> 2] = value & 0xFF;
                break;
        }
    }

    /// <summary>
    /// A PI DMA. The data moves at once; the busy flag and the interrupt follow the time the domain's timing registers
    /// give: a latency per page, then a pulse and release per halfword (n64brew: "Peripheral Interface").
    /// </summary>
    private void PiDma(bool toRdram, uint length)
    {
        if (_piBusy)
        {
            _piError = true;
            return;
        }

        var cart = _piCartAddress;
        var dram = _piDramAddress;
        var rdram = _rdram;
        if (toRdram)
        {
            // The PI moves blocks of up to 128 bytes, each ending on an 8-byte boundary in RDRAM, reading the
            // cartridge a halfword at a time. When RDRAM isn't 8-byte aligned, the first block drops as many bytes from
            // its end as the misalignment (n64brew: "Peripheral Interface", unaligned DMA; PeterLemon's
            // DMAAlignment-PI programs, recorded on hardware).
            var flashRead = Cartridge.SaveType == N64SaveType.FlashRam && _flashMode == FlashMode.Read
                && cart is >= 0x0800_0000 and < 0x1000_0000;
            var remaining = (int)length;
            var source = cart;
            var first = true;
            while (remaining > 0)
            {
                var misalign = (int)(dram & 7);
                var blockLength = 128 - misalign;
                var count = Math.Min(remaining, blockLength);
                remaining -= count;
                var written = count;
                if (first)
                {
                    if (count == blockLength - 1)
                    {
                        written++;
                    }

                    written = Math.Max(written - misalign, 0);
                }

                for (uint i = 0; i < written; i++)
                {
                    var target = (dram + i) & 0x00FF_FFFF;
                    if (target < (uint)rdram.Length)
                    {
                        rdram[target] = flashRead ? ReadFlashArray(cart, source - cart + i) : ReadCartByte(source + i);
                    }
                }

                source += (uint)(count + 1) & ~1u; // the cartridge is read a halfword at a time
                dram = (dram + (uint)written + 7) & 0x00FF_FFF8;
                first = false;
            }

            _piDramAddress = dram;
        }
        else
        {
            for (uint i = 0; i < length; i++)
            {
                var source = (dram + i) & 0x00FF_FFFF;
                WriteCartByte(cart + i, source < (uint)rdram.Length ? rdram[source] : (byte)0);
            }

            if (_flashMode == FlashMode.Write && Cartridge.SaveType == N64SaveType.FlashRam)
            {
                // RDRAM to FlashRAM fills the page buffer
                for (uint i = 0; i < Math.Min(length, 128u); i++)
                {
                    var source = (dram + i) & 0x00FF_FFFF;
                    _flashPage[i] = source < (uint)rdram.Length ? rdram[source] : (byte)0;
                }
            }
        }

        if (!toRdram)
        {
            _piDramAddress = (dram + length + 7) & 0x00FF_FFF8;
        }

        _piCartAddress = (cart + length + 1) & 0xFFFF_FFFE;

        var domain2 = cart is >= 0x0500_0000 and < 0x0600_0000 or >= 0x0800_0000 and < 0x1000_0000;
        var timing = domain2 ? 4 : 0;
        var latency = _piTiming[timing] + 1;
        var pulse = _piTiming[timing + 1] + 1;
        var pageSize = 1u << (int)(_piTiming[timing + 2] + 2);
        var release = _piTiming[timing + 3] + 1;
        var pages = (length + pageSize - 1) / pageSize;
        var rcpCycles = pages * latency + (length + 1) / 2 * (pulse + release);
        _piBusy = true;
        _piDoneCycle = Cpu.Cycles + (long)(rcpCycles * 3 / 2);
        UpdateNextEvent();
    }

    private void PiDmaDone()
    {
        _piBusy = false;
        RaiseInterrupt(N64Interrupt.Pi);
    }

    // ---- The cartridge ----

    private uint ReadRom(uint offset)
    {
        var rom = Cartridge.Rom;
        if (offset + 4 <= (uint)rom.Length)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan((int)offset)); // offset may be a halfword's
        }

        // Past the end of the ROM, the bus reads back the address's low halfword (n64brew: "Peripheral Interface").
        var low = offset & 0xFFFF;
        return (low << 16) | ((low + 2) & 0xFFFF);
    }

    private byte ReadCartByte(uint address)
    {
        if (address is >= 0x1000_0000 and < 0x1FC0_0000)
        {
            var offset = address - 0x1000_0000;
            var rom = Cartridge.Rom;
            return offset < (uint)rom.Length ? rom[offset] : (byte)(ReadRom(offset & ~3u) >> (int)(24 - ((offset & 3) << 3)));
        }

        if (address is >= 0x0800_0000 and < 0x1000_0000)
        {
            switch (Cartridge.SaveType)
            {
                case N64SaveType.Sram:
                {
                    var save = Cartridge.Save;
                    return save[(address - 0x0800_0000) % (uint)save.Length];
                }

                case N64SaveType.FlashRam:
                    return ReadFlashByte(address - 0x0800_0000);
            }
        }

        return 0;
    }

    private void WriteCartByte(uint address, byte value)
    {
        if (address is >= 0x0800_0000 and < 0x1000_0000 && Cartridge.SaveType == N64SaveType.Sram)
        {
            var save = Cartridge.Save;
            save[(address - 0x0800_0000) % (uint)save.Length] = value;
        }
    }

    private uint ReadCartRam(uint address)
    {
        if (Cartridge.SaveType == N64SaveType.FlashRam)
        {
            return _flashMode == FlashMode.Status || address == 0x0800_0000 ? _flashStatus : 0;
        }

        return ((uint)ReadCartByte(address & ~3u) << 24) | ((uint)ReadCartByte((address & ~3u) + 1) << 16)
            | ((uint)ReadCartByte((address & ~3u) + 2) << 8) | ReadCartByte((address & ~3u) + 3);
    }

    private void WriteCartRam(uint address, uint value)
    {
        if (Cartridge.SaveType == N64SaveType.Sram)
        {
            for (var i = 0; i < 4; i++)
            {
                WriteCartByte((address & ~3u) + (uint)i, (byte)(value >> (24 - i * 8)));
            }

            return;
        }

        if (Cartridge.SaveType == N64SaveType.FlashRam && address == 0x0801_0000)
        {
            FlashCommand(value);
        }
    }

    /// <summary>
    /// A FlashRAM command (n64brew: "FlashRAM"; as Mupen64Plus implements it): erase a 128-byte page or the chip,
    /// program a page from the buffer, and the status and read modes.
    /// </summary>
    private void FlashCommand(uint command)
    {
        var save = Cartridge.Save;
        switch (command >> 24)
        {
            case 0x4B: // sector erase: choose the page
                _flashMode = FlashMode.Erase;
                _flashOffset = (command & 0xFFFF) * 128;
                break;
            case 0x78: // chip erase
                _flashMode = FlashMode.Erase;
                _flashOffset = uint.MaxValue;
                _flashStatus = 0x1111_8008;
                break;
            case 0xA5: // page program: choose the page
                _flashOffset = (command & 0xFFFF) * 128;
                _flashStatus = 0x1111_8004;
                break;
            case 0xB4: // write mode: DMA fills the page buffer
                _flashMode = FlashMode.Write;
                break;
            case 0xD2: // execute
                if (_flashMode == FlashMode.Erase)
                {
                    if (_flashOffset == uint.MaxValue)
                    {
                        Array.Fill(save, (byte)0xFF);
                    }
                    else if (_flashOffset + 128 <= (uint)save.Length)
                    {
                        save.AsSpan((int)_flashOffset, 128).Fill(0xFF);
                    }
                }
                else if (_flashMode == FlashMode.Write && _flashOffset + 128 <= (uint)save.Length)
                {
                    _flashPage.CopyTo(save, (int)_flashOffset);
                }

                break;
            case 0xE1: // status mode
                _flashMode = FlashMode.Status;
                _flashStatus = 0x1111_8001;
                break;
            case 0xF0: // read mode
                _flashMode = FlashMode.Read;
                _flashStatus = 0x1111_8004;
                break;
        }
    }

    private byte ReadFlashByte(uint offset)
    {
        if (_flashMode == FlashMode.Status)
        {
            // The status word, then the silicon id of a Macronix MX29L1100.
            var id = (offset & 7) < 4 ? _flashStatus : 0x00C2_001Eu;
            return (byte)(id >> (int)(24 - ((offset & 3) << 3)));
        }

        // In read mode the cartridge address counts halfwords (libultra's osFlashReadArray), handled in PiDma.
        var save = Cartridge.Save;
        return save[offset % (uint)save.Length];
    }

    /// <summary>FlashRAM in read mode: the cartridge address counts halfwords (libultra's osFlashReadArray).</summary>
    private byte ReadFlashArray(uint start, uint index)
    {
        var save = Cartridge.Save;
        return save[((start - 0x0800_0000) * 2 + index) % (uint)save.Length];
    }

    // ---- The IS-Viewer ----

    private uint ReadIsViewer(uint address)
    {
        var offset = (int)(address - 0x13FF_0020);
        return offset >= 0 && offset + 4 <= _isViewer.Length ? BinaryPrimitives.ReadUInt32BigEndian(_isViewer.AsSpan(offset)) : 0;
    }

    private void WriteIsViewer(uint address, uint value)
    {
        if (address == 0x13FF_0014)
        {
            var length = Math.Min((int)value, _isViewer.Length);
            for (var i = 0; i < length; i++)
            {
                DebugOutput?.Invoke((char)_isViewer[i]);
            }

            return;
        }

        var offset = (int)(address - 0x13FF_0020);
        if (offset >= 0 && offset + 4 <= _isViewer.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_isViewer.AsSpan(offset), value);
        }
    }

    // ---- SI ----

    private uint ReadSi(uint address) => (address & 0x1F) switch
    {
        0x00 => _siDramAddress,
        0x18 => (_siBusy ? 1u : 0u) | ((_miInterrupts & (uint)N64Interrupt.Si) != 0 ? 0x1000u : 0u),
        _ => 0,
    };

    private void WriteSi(uint address, uint value)
    {
        switch (address & 0x1F)
        {
            case 0x00:
                _siDramAddress = value & 0x00FF_FFF8;
                break;
            case 0x04: // SI_PIF_AD_RD64B: PIF RAM to RDRAM
                if ((_siDramAddress & 0x00FF_FFFF) + 64 <= (uint)_rdram.Length)
                {
                    Pif.Ram.CopyTo(_rdram, (int)_siDramAddress);
                }

                StartSiDma();
                break;
            case 0x10: // SI_PIF_AD_WR64B: RDRAM to PIF RAM
                if ((_siDramAddress & 0x00FF_FFFF) + 64 <= (uint)_rdram.Length)
                {
                    _rdram.AsSpan((int)_siDramAddress, 64).CopyTo(Pif.Ram);
                }

                Pif.Written();
                StartSiDma();
                break;
            case 0x18:
                ClearInterrupt(N64Interrupt.Si);
                break;
        }
    }

    private void StartSiDma()
    {
        _siBusy = true;
        _siDoneCycle = Cpu.Cycles + SiDmaCycles;
        UpdateNextEvent();
    }

    private void SiDmaDone()
    {
        _siBusy = false;
        RaiseInterrupt(N64Interrupt.Si);
    }

    // ---- State ----

    private void SaveIo(StateWriter writer)
    {
        foreach (var value in _vi)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _ri)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _rdramRegisters)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _piTiming)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteInt32(_viLine);
        writer.WriteInt32(_viField);
        writer.WriteInt64(_viNextLineCycle);
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_viLineFraction));
        writer.WriteUInt32(_piDramAddress);
        writer.WriteUInt32(_piCartAddress);
        writer.WriteBool(_piBusy);
        writer.WriteInt64(_piDoneCycle);
        writer.WriteBool(_piError);
        writer.WriteUInt32(_siDramAddress);
        writer.WriteBool(_siBusy);
        writer.WriteInt64(_siDoneCycle);
        for (var i = 0; i < 2; i++)
        {
            writer.WriteUInt32(_aiAddress[i]);
            writer.WriteUInt32(_aiLength[i]);
        }

        writer.WriteInt32(_aiCount);
        writer.WriteUInt32(_aiDramAddress);
        writer.WriteUInt32(_aiControl);
        writer.WriteUInt32(_aiDacRate);
        writer.WriteUInt32(_aiBitRate);
        writer.WriteInt64(_aiNextSampleCycle);
        writer.WriteInt64(BitConverter.DoubleToInt64Bits(_aiSampleFraction));
        writer.WriteInt64(_aiBufferEndCycle);
        writer.WriteInt32(BitConverter.SingleToInt32Bits(_aiLastLevel));
        writer.WriteByte((byte)_flashMode);
        writer.WriteUInt32(_flashStatus);
        writer.WriteUInt32(_flashOffset);
        writer.WriteBytes(_flashPage);
        writer.WriteInt32(_rcpReads);
    }

    private void LoadIo(StateReader reader)
    {
        for (var i = 0; i < _vi.Length; i++)
        {
            _vi[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < _ri.Length; i++)
        {
            _ri[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < _rdramRegisters.Length; i++)
        {
            _rdramRegisters[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < _piTiming.Length; i++)
        {
            _piTiming[i] = reader.ReadUInt32();
        }

        _viLine = reader.ReadInt32();
        _viField = reader.ReadInt32();
        _viNextLineCycle = reader.ReadInt64();
        _viLineFraction = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _piDramAddress = reader.ReadUInt32();
        _piCartAddress = reader.ReadUInt32();
        _piBusy = reader.ReadBool();
        _piDoneCycle = reader.ReadInt64();
        _piError = reader.ReadBool();
        _siDramAddress = reader.ReadUInt32();
        _siBusy = reader.ReadBool();
        _siDoneCycle = reader.ReadInt64();
        for (var i = 0; i < 2; i++)
        {
            _aiAddress[i] = reader.ReadUInt32();
            _aiLength[i] = reader.ReadUInt32();
        }

        _aiCount = reader.ReadInt32();
        _aiDramAddress = reader.ReadUInt32();
        _aiControl = reader.ReadUInt32();
        _aiDacRate = reader.ReadUInt32();
        _aiBitRate = reader.ReadUInt32();
        _aiNextSampleCycle = reader.ReadInt64();
        _aiSampleFraction = BitConverter.Int64BitsToDouble(reader.ReadInt64());
        _aiBufferEndCycle = reader.ReadInt64();
        _aiLastLevel = BitConverter.Int32BitsToSingle(reader.ReadInt32());
        _flashMode = (FlashMode)reader.ReadByte();
        _flashStatus = reader.ReadUInt32();
        _flashOffset = reader.ReadUInt32();
        reader.ReadBytes(_flashPage);
        _rcpReads = reader.ReadInt32();
    }
}
