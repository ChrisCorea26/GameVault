namespace GameVault.Core.PlayStation2;

// VIFcode decoding (ps2tek: "VIF Commands"). Ported in part from Play! (BSD-2-Clause): Source/ee/Vif.cpp, Vif1.cpp.
public sealed partial class Ps2Vif
{
    private const uint CmdNop = 0x00;
    private const uint CmdStcycl = 0x01;
    private const uint CmdOffset = 0x02;
    private const uint CmdBase = 0x03;
    private const uint CmdItop = 0x04;
    private const uint CmdStmod = 0x05;
    private const uint CmdMskpath3 = 0x06;
    private const uint CmdMark = 0x07;
    private const uint CmdFlushe = 0x10;
    private const uint CmdFlush = 0x11;
    private const uint CmdFlusha = 0x13;
    private const uint CmdMscal = 0x14;
    private const uint CmdMscalf = 0x15;
    private const uint CmdMscnt = 0x17;
    private const uint CmdStmask = 0x20;
    private const uint CmdStrow = 0x30;
    private const uint CmdStcol = 0x31;
    private const uint CmdMpg = 0x4A;
    private const uint CmdDirect = 0x50;
    private const uint CmdDirecthl = 0x51;

    private const int DirectSlice = 16;

    private uint Command => (_code >> 24) & 0x7F;

    private uint Immediate => _code & 0xFFFF;

    private void Process()
    {
        while (true)
        {
            // A force break or STOP stalls at once; an invalid command stalls unless ERR.ME1 masks it.
            if ((_stat & (StatVfs | StatVss)) != 0 || ErrorStall)
            {
                return;
            }

            if (!_inCommand)
            {
                if (_stopRequested)
                {
                    _stopRequested = false;
                    _stat |= StatVss;
                    return;
                }

                // The I bit stalls the VIF before the next command (ps2tek), until FBRST.STC.
                if ((_stat & StatVis) != 0 || _fifoCount == 0)
                {
                    return;
                }

                BeginCommand(Pop());
            }

            if (!Execute())
            {
                return;
            }

            EndCommand();
        }
    }

    private void BeginCommand(uint code)
    {
        _code = code;
        _inCommand = true;
        _waitVu = _waitGif = false;
        _irqPending = (code & 0x8000_0000) != 0 && (_err & ErrMii) == 0;

        var cmd = Command;
        var num = (int)((code >> 16) & 0xFF);
        switch (cmd)
        {
            case CmdStmask:
                _num = 1;
                break;
            case CmdStrow:
            case CmdStcol:
                _num = 4;
                break;
            case CmdMpg:
                _num = num == 0 ? 256 : num;
                _mpgWordsDone = 0;
                break;
            case CmdDirect:
            case CmdDirecthl:
                _directRemaining = Immediate == 0 ? 65536 : (int)Immediate;
                _num = num;
                break;
            case >= 0x60:
                _num = num == 0 ? 256 : num;
                _readTick = _writeTick = 0;
                _unpackAddress = (Immediate & 0x3FF) + (IsVif1 && (Immediate & 0x8000) != 0 ? _tops : 0);
                break;
            default:
                _num = num;
                break;
        }
    }

    private void EndCommand()
    {
        _inCommand = false;
        _waitVu = _waitGif = false;
        if (_irqPending)
        {
            // The interrupt follows the command; the VIF then stalls before the next one, except after MARK
            // (ps2autotests: dma/vif/intstall).
            _irqPending = false;
            _stat |= StatInt;
            if (Command != CmdMark)
            {
                _stat |= StatVis;
            }

            _intc.Raise(IsVif1 ? Ps2Interrupt.Vif1 : Ps2Interrupt.Vif0);
        }

        if (_stopRequested)
        {
            _stopRequested = false;
            _stat |= StatVss;
        }
    }

    /// <summary>Runs the current command as far as the FIFO allows; true when it has finished.</summary>
    private bool Execute()
    {
        var cmd = Command;
        if (cmd >= 0x60)
        {
            return (cmd & 3) == 3 && (cmd & 0xF) != 0xF ? Invalid() : ExecuteUnpack();
        }

        switch (cmd)
        {
            case CmdNop:
                return true;
            case CmdStcycl:
                _cycle = Immediate;
                return true;
            case CmdOffset:
                if (IsVif1)
                {
                    _ofst = Immediate & 0x3FF;
                    _stat &= ~StatDbf;
                    _tops = _base;
                }

                return true;
            case CmdBase:
                if (IsVif1)
                {
                    _base = Immediate & 0x3FF;
                }

                return true;
            case CmdItop:
                _itops = Immediate & 0x3FF;
                return true;
            case CmdStmod:
                _mode = Immediate & 3;
                return true;
            case CmdMskpath3:
                if (IsVif1 && _gif is not null)
                {
                    _gif.Path3Masked = (Immediate & 0x8000) != 0;
                }

                return true;
            case CmdMark:
                _mark = Immediate;
                _stat |= StatMrk;
                return true;
            case CmdFlushe:
                return WaitForVu();
            case CmdFlush:
                return !IsVif1 || (WaitForVu() && WaitForGif(path3: false));
            case CmdFlusha:
                return !IsVif1 || (WaitForVu() && WaitForGif(path3: true));
            case CmdMscal:
                if (!WaitForVu())
                {
                    return false;
                }

                PrepareMicroProgram();
                _vu.StartMicro(Immediate * 8);
                return true;
            case CmdMscalf:
                if (!WaitForVu() || (IsVif1 && !WaitForGif(path3: false)))
                {
                    return false;
                }

                PrepareMicroProgram();
                _vu.StartMicro(Immediate * 8);
                return true;
            case CmdMscnt:
                if (!WaitForVu())
                {
                    return false;
                }

                PrepareMicroProgram();
                _vu.ContinueMicro();
                return true;
            case CmdStmask:
                if (_fifoCount == 0)
                {
                    return false;
                }

                _mask = Pop();
                _num = 0;
                return true;
            case CmdStrow:
            case CmdStcol:
            {
                var registers = cmd == CmdStrow ? _row : _col;
                while (_num > 0 && _fifoCount > 0)
                {
                    registers[4 - _num] = Pop();
                    _num--;
                }

                return _num == 0;
            }

            case CmdMpg:
                return ExecuteMpg();
            case CmdDirect:
            case CmdDirecthl:
                return IsVif1 ? ExecuteDirect(cmd == CmdDirecthl) : Invalid();
            default:
                return Invalid();
        }
    }

    // An unknown VIFcode sets STAT.ER1 and stalls the VIF until FBRST.STC, unless ERR.ME1 is set (ps2tek).
    private bool Invalid()
    {
        if ((_err & ErrMe1) == 0)
        {
            _stat |= StatEr1;
        }

        return true;
    }

    private bool WaitForVu()
    {
        _waitVu = _vu.Running;
        return !_waitVu;
    }

    // PATH1 (XGKICK) hands the GIF whole packets, so only PATH2 (and PATH3 for FLUSHA) can be mid-transfer here.
    private bool WaitForGif(bool path3)
    {
        _waitGif = _gif is not null && (_gif.Path2Active || (path3 && _gif.Path3Active));
        return !_waitGif;
    }

    // MSCAL/MSCALF/MSCNT: ITOP takes ITOPS; on VIF1 TOP takes TOPS and the double buffer swaps (ps2tek: DBF).
    private void PrepareMicroProgram()
    {
        _itop = _itops;
        if (!IsVif1)
        {
            return;
        }

        _top = _tops;
        _tops = ((_stat & StatDbf) != 0 ? _base : _base + _ofst) & 0x3FF;
        _stat ^= StatDbf;
    }

    // MPG: NUM × 8 bytes into micro memory at IMMEDIATE × 8, once the VU has stopped (ps2tek).
    private bool ExecuteMpg()
    {
        if (!WaitForVu())
        {
            return false;
        }

        var micro = _vu.MicroMemory;
        var total = _num * 2;
        var start = Immediate * 8;
        var mask = micro.Length - 1;
        while (_mpgWordsDone < total && _fifoCount > 0)
        {
            var address = (int)((start + ((uint)_mpgWordsDone * 4)) & (uint)mask);
            var word = Pop();
            micro[address] = (byte)word;
            micro[address + 1] = (byte)(word >> 8);
            micro[address + 2] = (byte)(word >> 16);
            micro[address + 3] = (byte)(word >> 24);
            _mpgWordsDone++;
        }

        if (_mpgWordsDone < total)
        {
            return false;
        }

        _num = 0;
        return true;
    }

    // DIRECT/DIRECTHL: IMMEDIATE quadwords to the GIF's PATH2. DIRECTHL also waits while PATH3 is mid-packet (ps2tek
    // says only while PATH3 is in IMAGE mode; the GIF interface doesn't say which mode, so any PATH3 packet holds it).
    private bool ExecuteDirect(bool waitForPath3)
    {
        if (waitForPath3 && _gif is not null && _gif.Path3Active && !_gif.Path2Active)
        {
            _waitGif = true;
            return false;
        }

        Span<UInt128> buffer = stackalloc UInt128[DirectSlice];
        while (_directRemaining > 0 && _fifoCount >= 4)
        {
            var n = Math.Min(Math.Min(_directRemaining, _fifoCount / 4), DirectSlice);
            for (var i = 0; i < n; i++)
            {
                buffer[i] = PeekQuadword(i);
            }

            var taken = _gif is null ? n : _gif.WritePath2(buffer[..n]);
            for (var i = 0; i < taken * 4; i++)
            {
                Pop();
            }

            _directRemaining -= taken;
            if (taken < n)
            {
                _waitGif = true;
                return false;
            }
        }

        _waitGif = false;
        return _directRemaining == 0;
    }

    private UInt128 PeekQuadword(int index)
    {
        var at = _fifoHead + (index * 4);
        var cap = _fifo.Length;
        return ((UInt128)_fifo[(at + 3) % cap] << 96) | ((UInt128)_fifo[(at + 2) % cap] << 64) |
               ((UInt128)_fifo[(at + 1) % cap] << 32) | _fifo[at % cap];
    }
}
