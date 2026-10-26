using GameVault.Core.Cpu.R5900;

namespace GameVault.Core.PlayStation2;

// VU0 as the EE's coprocessor 2 (macro mode) and the control registers CFC2/CTC2 reach (ps2tek: "COP2").
public sealed partial class Ps2Vu
{
    /// <summary>What WaitForMicro runs at most before giving up on a program that never ends.</summary>
    private const long WaitLimit = 1L << 26;

    // Control register numbers (CFC2/CTC2).
    public const int ControlStatus = 16;
    public const int ControlMac = 17;
    public const int ControlClipping = 18;
    public const int ControlR = 20;
    public const int ControlI = 21;
    public const int ControlQ = 22;
    public const int ControlTpc = 26;
    public const int ControlCmsar0 = 27;
    public const int ControlFbrst = 28;
    public const int ControlVpuStat = 29;
    public const int ControlCmsar1 = 31;

    /// <inheritdoc/>
    public bool MicroRunning => _running;

    /// <inheritdoc/>
    public UInt128 ReadVf(int index)
    {
        var b = (index & 31) << 2;
        return new UInt128(((ulong)_vf[b + 3] << 32) | _vf[b + 2], ((ulong)_vf[b + 1] << 32) | _vf[b]);
    }

    /// <inheritdoc/>
    public void WriteVf(int index, UInt128 value)
    {
        index &= 31;
        if (index == 0)
        {
            return;
        }

        var b = index << 2;
        var low = (ulong)value;
        var high = (ulong)(value >> 64);
        _vf[b] = (uint)low;
        _vf[b + 1] = (uint)(low >> 32);
        _vf[b + 2] = (uint)high;
        _vf[b + 3] = (uint)(high >> 32);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The reserved registers read as ps2autotests' cpu/vu0_macro/transfer recorded them: 19 reads $2E30, 24 keeps
    /// bits $0C0C of what was written, the others read 0.
    /// </remarks>
    public uint ReadControl(int index)
    {
        if (index < 16)
        {
            return _vi[index & 15];
        }

        var vu1 = _host.Vu1 as Ps2Vu;
        return index switch
        {
            ControlStatus => (uint)_status,
            ControlMac => (uint)_mac,
            ControlClipping => (uint)_clip,
            19 => 0x2E30,
            ControlR => _r,
            ControlI => _i,
            ControlQ => _q,
            24 => _reserved24,
            ControlTpc => _pc >> 3,
            ControlCmsar0 => _cmsar0,
            ControlFbrst => (DebugEnable ? 0x4u : 0) | (TrapEnable ? 0x8u : 0)
                            | (vu1 is { DebugEnable: true } ? 0x400u : 0) | (vu1 is { TrapEnable: true } ? 0x800u : 0),
            ControlVpuStat => (uint)(StatBits | ((vu1?.StatBits ?? 0) << 8)),
            ControlCmsar1 => _cmsar1,
            _ => 0,
        };
    }

    /// <inheritdoc/>
    public void WriteControl(int index, uint value)
    {
        if (index < 16)
        {
            SetVi(index, (ushort)value);
            return;
        }

        switch (index)
        {
            case ControlStatus: // Only the sticky bits are writable.
                _status = (_status & 0x3F) | (int)(value & 0xFC0);
                break;
            case ControlClipping:
                _clip = (int)(value & 0xFFFFFF);
                break;
            case ControlR:
                _r = value & 0x7FFFFF;
                break;
            case ControlI:
                _i = value;
                break;
            case ControlQ:
                _q = value;
                break;
            case 24:
                _reserved24 = value & 0x0C0C;
                break;
            case ControlCmsar0:
                _cmsar0 = value & 0xFFFF;
                break;
            case ControlFbrst:
                WriteFbrst(value);
                break;
            case ControlCmsar1: // Starts VU1's micro program there.
                _cmsar1 = value & 0xFFFF;
                _host.Vu1.StartMicro(_cmsar1 << 3);
                break;
        }
    }

    /// <summary>FBRST: FB/RS/DE/TE for VU0 in bits 0–3 and VU1 in bits 8–11 (force break, reset, D and T enables).</summary>
    private void WriteFbrst(uint value)
    {
        var vu1 = _host.Vu1 as Ps2Vu;
        DebugEnable = (value & 0x4) != 0;
        TrapEnable = (value & 0x8) != 0;
        if (vu1 is not null)
        {
            vu1.DebugEnable = (value & 0x400) != 0;
            vu1.TrapEnable = (value & 0x800) != 0;
        }

        if ((value & 0x1) != 0)
        {
            ForceBreak();
        }

        if ((value & 0x2) != 0)
        {
            Reset();
        }

        if ((value & 0x100) != 0)
        {
            vu1?.ForceBreak();
        }

        if ((value & 0x200) != 0)
        {
            vu1?.Reset();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The operation completes at once, flags, Q and all: the EE's interlocks keep a program from seeing the pipeline
    /// in macro mode in any way the tests can tell. A macro instruction while VU0 runs a micro program waits for it
    /// to end, as the EE does.
    /// </remarks>
    public void ExecuteMacro(uint instruction)
    {
        if (_running)
        {
            WaitForMicro();
        }

        _inMacro = true;
        var function = instruction & 0x3F;
        if (function < 0x30)
        {
            RunMacroUpper(instruction);
        }
        else if (function < 0x3C)
        {
            switch (function)
            {
                case 0x38: // VCALLMS
                    _inMacro = false;
                    StartMicro(((instruction >> 6) & 0x7FFF) << 3);
                    return;
                case 0x39: // VCALLMSR
                    _inMacro = false;
                    StartMicro(_cmsar0 << 3);
                    return;
                default: // VIADD, VISUB, VIADDI, VIAND, VIOR: the lower encodings with bit 31 set
                    RunMacroLower(instruction);
                    break;
            }
        }
        else if (LowerSpecial(instruction) < 0x30)
        {
            RunMacroUpper(instruction);
        }
        else
        {
            RunMacroLower(instruction);
        }

        CompletePending();
        _intWriteReg = -1;
        _inMacro = false;
    }

    private void RunMacroUpper(uint instruction)
    {
        var d = default(DecodedPair);
        DecodeUpper(ref d, instruction & 0x01FFFFFF);
        _stageReg = -1;
        ExecuteUpper(ref d);
        CommitStage();
    }

    private void RunMacroLower(uint instruction)
    {
        var d = default(DecodedPair);
        DecodeLower(ref d, 0x80000000 | (instruction & 0x01FFFFFF));
        ExecuteLower(ref d, _pc);
    }

    /// <inheritdoc/>
    public void WaitForMicro()
    {
        if (_running)
        {
            Run(WaitLimit);
        }
    }
}
