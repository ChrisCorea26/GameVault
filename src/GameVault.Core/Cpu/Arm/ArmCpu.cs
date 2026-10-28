using GameVault.Core.State;

namespace GameVault.Core.Cpu.Arm;

/// <summary>An ARM bus: word accesses at word-aligned addresses, byte accesses at any (little-endian).</summary>
public interface IArmBus
{
    uint ReadWord(uint address);

    byte ReadByte(uint address);

    void WriteWord(uint address, uint value);

    void WriteByte(uint address, byte value);

    /// <summary>The CPU spent <paramref name="clocks"/> clocks (one a memory access or internal cycle).</summary>
    void Clock(int clocks);
}

/// <summary>
/// An ARM CPU in ARM state (snes-chips-3.md §4), as the ST018's ARM60 runs it: every ARM-state instruction class
/// (data processing with the barrel shifter, MUL/MLA and the long multiplies, single and halfword transfers, block
/// transfers with the user-bank and SPSR forms, SWP, MRS/MSR, branches, SWI, undefined), the seven modes with their
/// banked registers and SPSRs, and the condition codes. Coprocessor instructions are undefined (the ST018 has none).
/// Thumb isn't supported (the ARM60 has none).
/// </summary>
/// <remarks>
/// <para>R15 reads as the instruction's address + 8 (+ 12 as a register-shifted operand or a stored value), as the
/// pipeline makes it. Exceptions save the address of the next instruction in R14 of their mode. A clock is counted
/// for each memory access and internal cycle.</para>
/// <para>Behaviour follows ares' ARM7TDMI core in ARM state (ISC licence), which ares runs the ST018 on, and the ARM
/// Architecture Reference Manual. The halfword and long-multiply instructions come with that core (ARMv4); the ARM60
/// is an ARMv3, and programs written for it don't use them.</para>
/// </remarks>
public sealed class ArmCpu : IStateful
{
    public const int User = 0x10, Fiq = 0x11, Irq = 0x12, Supervisor = 0x13, Abort = 0x17, Undefined = 0x1B, System = 0x1F;

    private static readonly StateTag Tag = StateTag.From("ARM ");

    private readonly IArmBus _bus;
    private readonly uint[] _r = new uint[16];        // the user bank (R15 is the next instruction's address)
    private readonly uint[] _fiqBank = new uint[7];   // R8–R14 in FIQ mode
    private readonly uint[] _userHigh = new uint[7];  // R8–R14 of the other modes, while in FIQ mode
    private readonly uint[] _bank13 = new uint[6];    // R13 for FIQ, IRQ, SVC, ABT, UND (and unused)
    private readonly uint[] _bank14 = new uint[6];
    private readonly uint[] _spsr = new uint[6];
    private uint _cpsr;
    private bool _carry; // the shifter's carry out

    public ArmCpu(IArmBus bus) => _bus = bus;

    /// <summary>The address of the next instruction to run.</summary>
    public uint Pc
    {
        get => _r[15];
        set => _r[15] = value & ~3u;
    }

    public uint Cpsr => _cpsr;

    public int Mode => (int)(_cpsr & 0x1F);

    /// <summary>The value of the last instruction fetched (what reads of unmapped areas see).</summary>
    public uint LastFetch { get; private set; }

    public uint GetR(int index) => R(index);

    public void SetR(int index, uint value) => SetRegister(index, value);

    /// <summary>A reset: supervisor mode, IRQ and FIQ off, PC 0.</summary>
    public void Reset()
    {
        Array.Clear(_r);
        Array.Clear(_fiqBank);
        Array.Clear(_userHigh);
        Array.Clear(_bank13);
        Array.Clear(_bank14);
        Array.Clear(_spsr);
        _cpsr = Supervisor | 0xC0;
        LastFetch = 0;
    }

    // ---- Registers ----

    private static int BankIndex(int mode) => mode switch
    {
        Fiq => 0,
        Irq => 1,
        Supervisor => 2,
        Abort => 3,
        Undefined => 4,
        _ => 5,
    };

    private bool HasSpsr => Mode is Fiq or Irq or Supervisor or Abort or Undefined;

    private uint R(int index)
    {
        var mode = Mode;
        if (index == 13 && mode is not (User or System))
        {
            return _bank13[BankIndex(mode)];
        }

        if (index == 14 && mode is not (User or System))
        {
            return _bank14[BankIndex(mode)];
        }

        return _r[index];
    }

    private void SetRegister(int index, uint value)
    {
        var mode = Mode;
        if (index == 13 && mode is not (User or System))
        {
            _bank13[BankIndex(mode)] = value;
        }
        else if (index == 14 && mode is not (User or System))
        {
            _bank14[BankIndex(mode)] = value;
        }
        else if (index == 15)
        {
            _r[15] = value & ~3u;
        }
        else
        {
            _r[index] = value;
        }
    }

    /// <summary>Changes mode, swapping the FIQ bank of R8–R12 in or out (R13/R14 are looked up by mode).</summary>
    private void SetCpsr(uint value)
    {
        var from = Mode;
        var to = (int)(value & 0x1F) | 0x10;
        if (from == Fiq && to != Fiq)
        {
            for (var i = 0; i < 5; i++)
            {
                _fiqBank[i] = _r[8 + i];
                _r[8 + i] = _userHigh[i];
            }
        }
        else if (from != Fiq && to == Fiq)
        {
            for (var i = 0; i < 5; i++)
            {
                _userHigh[i] = _r[8 + i];
                _r[8 + i] = _fiqBank[i];
            }
        }

        // R13/R14 of USR and SYS are the same registers.
        _cpsr = (value & 0xF00000E0) | (uint)to;
    }

    private uint Spsr
    {
        get => HasSpsr ? _spsr[BankIndex(Mode)] : _cpsr;
        set
        {
            if (HasSpsr)
            {
                _spsr[BankIndex(Mode)] = value & 0xF00000FF;
            }
        }
    }

    private bool N { get => (_cpsr & 0x80000000) != 0; set => _cpsr = value ? _cpsr | 0x80000000 : _cpsr & ~0x80000000; }

    private bool Z { get => (_cpsr & 0x40000000) != 0; set => _cpsr = value ? _cpsr | 0x40000000 : _cpsr & ~0x40000000u; }

    private bool C { get => (_cpsr & 0x20000000) != 0; set => _cpsr = value ? _cpsr | 0x20000000 : _cpsr & ~0x20000000u; }

    private bool V { get => (_cpsr & 0x10000000) != 0; set => _cpsr = value ? _cpsr | 0x10000000 : _cpsr & ~0x10000000u; }

    // ---- Running ----

    /// <summary>Runs one instruction.</summary>
    public void Step()
    {
        var address = _r[15];
        var opcode = _bus.ReadWord(address & ~3u);
        LastFetch = opcode;
        _bus.Clock(1);
        _r[15] = address + 4;
        if (!Condition(opcode >> 28))
        {
            return;
        }

        Execute(opcode, address);
    }

    /// <summary>Takes an exception: the mode's SPSR keeps CPSR, R14 the return address, IRQs (and FIQs for FIQ) off.</summary>
    private void Exception(int mode, uint vector, uint returnAddress)
    {
        var saved = _cpsr;
        SetCpsr((_cpsr & ~0x3Fu) | (uint)mode | 0x80 | (mode == Fiq ? 0x40u : 0));
        Spsr = saved;
        SetRegister(14, returnAddress);
        SetRegister(15, vector);
    }

    private bool Condition(uint condition) => condition switch
    {
        0 => Z,
        1 => !Z,
        2 => C,
        3 => !C,
        4 => N,
        5 => !N,
        6 => V,
        7 => !V,
        8 => C && !Z,
        9 => !C || Z,
        10 => N == V,
        11 => N != V,
        12 => !Z && N == V,
        13 => Z || N != V,
        14 => true,
        _ => false,
    };

    private void Execute(uint op, uint address)
    {
        var pc8 = address + 8;
        switch ((op >> 25) & 7)
        {
            case 0:
                if ((op & 0x0FC000F0) == 0x00000090)
                {
                    Multiply(op);
                }
                else if ((op & 0x0F8000F0) == 0x00800090)
                {
                    MultiplyLong(op);
                }
                else if ((op & 0x0FB00FF0) == 0x01000090)
                {
                    Swap(op, pc8);
                }
                else if ((op & 0x0E000090) == 0x00000090 && (op & 0x60) != 0)
                {
                    Halfword(op, pc8);
                }
                else if ((op & 0x0FBF0FFF) == 0x010F0000)
                {
                    SetRegister((int)(op >> 12) & 15, (op & 0x400000) != 0 && HasSpsr ? Spsr : _cpsr); // MRS
                }
                else if ((op & 0x0FB0FFF0) == 0x0120F000)
                {
                    MoveToStatus(op, R((int)op & 15)); // MSR from a register
                }
                else
                {
                    DataProcessing(op, pc8);
                }

                break;
            case 1:
                if ((op & 0x0FB0F000) == 0x0320F000)
                {
                    var imm = op & 0xFF;
                    var rotate = (int)((op >> 8) & 15) * 2;
                    MoveToStatus(op, rotate == 0 ? imm : (imm >> rotate) | (imm << (32 - rotate))); // MSR immediate
                }
                else
                {
                    DataProcessing(op, pc8);
                }

                break;
            case 2:
                SingleTransfer(op, pc8, register: false);
                break;
            case 3:
                if ((op & 0x10) != 0)
                {
                    Exception(Undefined, 0x04, address + 4);
                }
                else
                {
                    SingleTransfer(op, pc8, register: true);
                }

                break;
            case 4:
                BlockTransfer(op, pc8);
                break;
            case 5:
                if ((op & 0x01000000) != 0)
                {
                    SetRegister(14, address + 4);
                }

                SetRegister(15, (uint)(pc8 + ((int)(op << 8) >> 6)));
                break;
            case 6:
                Exception(Undefined, 0x04, address + 4); // coprocessor transfers: no coprocessor
                break;
            default:
                if ((op & 0x01000000) != 0)
                {
                    Exception(Supervisor, 0x08, address + 4); // SWI
                }
                else
                {
                    Exception(Undefined, 0x04, address + 4);
                }

                break;
        }
    }

    // ---- The barrel shifter ----

    private uint Lsl(uint value, int shift)
    {
        _carry = C;
        if (shift == 0)
        {
            return value;
        }

        _carry = shift <= 32 && ((value >> (32 - shift)) & 1) != 0; // shift 32: bit 0
        return shift > 31 ? 0 : value << shift;
    }

    private uint Lsr(uint value, int shift)
    {
        _carry = C;
        if (shift == 0)
        {
            return value;
        }

        _carry = shift <= 32 && ((value >> (shift - 1)) & 1) != 0;
        return shift > 31 ? 0 : value >> shift;
    }

    private uint Asr(uint value, int shift)
    {
        _carry = C;
        if (shift == 0)
        {
            return value;
        }

        _carry = shift > 32 ? (value & 0x80000000) != 0 : ((value >> (shift - 1)) & 1) != 0;
        return shift > 31 ? (uint)((int)value >> 31) : (uint)((int)value >> shift);
    }

    private uint Ror(uint value, int shift)
    {
        _carry = C;
        if (shift == 0)
        {
            return value;
        }

        shift &= 31;
        if (shift != 0)
        {
            value = (value >> shift) | (value << (32 - shift));
        }

        _carry = (value & 0x80000000) != 0;
        return value;
    }

    private uint Rrx(uint value)
    {
        _carry = (value & 1) != 0;
        return (C ? 0x80000000 : 0) | (value >> 1);
    }

    /// <summary>A shift by an immediate amount: LSR/ASR #0 mean 32, ROR #0 is RRX.</summary>
    private uint ShiftImmediate(uint value, int type, int amount) => type switch
    {
        0 => Lsl(value, amount),
        1 => Lsr(value, amount == 0 ? 32 : amount),
        2 => Asr(value, amount == 0 ? 32 : amount),
        _ => amount == 0 ? Rrx(value) : Ror(value, amount),
    };

    // ---- Data processing ----

    private void DataProcessing(uint op, uint pc8)
    {
        var n = (int)(op >> 16) & 15;
        var d = (int)(op >> 12) & 15;
        var save = (op & 0x100000) != 0;
        var opcode = (int)(op >> 21) & 15;
        uint rn, operand;
        _carry = C;
        if ((op & 0x02000000) != 0)
        {
            rn = n == 15 ? pc8 : R(n);
            var imm = op & 0xFF;
            var rotate = (int)((op >> 8) & 15) * 2;
            operand = rotate == 0 ? imm : Ror(imm, rotate);
        }
        else if ((op & 0x10) == 0)
        {
            rn = n == 15 ? pc8 : R(n);
            var m = (int)op & 15;
            operand = ShiftImmediate(m == 15 ? pc8 : R(m), (int)(op >> 5) & 3, (int)(op >> 7) & 31);
        }
        else
        {
            // Shifted by a register: an extra internal cycle, and R15 reads 4 further on.
            var s = (int)(op >> 8) & 15;
            var amount = (int)(R(s) & 0xFF);
            rn = n == 15 ? pc8 + 4 : R(n);
            var m = (int)op & 15;
            var rm = m == 15 ? pc8 + 4 : R(m);
            _bus.Clock(1);
            operand = ((op >> 5) & 3) switch
            {
                0 => Lsl(rm, amount),
                1 => Lsr(rm, amount),
                2 => Asr(rm, amount),
                _ => Ror(rm, amount),
            };
        }

        uint result;
        switch (opcode)
        {
            case 0: result = Logic(rn & operand, save); SetRegister(d, result); break;
            case 1: result = Logic(rn ^ operand, save); SetRegister(d, result); break;
            case 2: SetRegister(d, Add(rn, ~operand, true, save)); break;
            case 3: SetRegister(d, Add(operand, ~rn, true, save)); break;
            case 4: SetRegister(d, Add(rn, operand, false, save)); break;
            case 5: SetRegister(d, Add(rn, operand, C, save)); break;
            case 6: SetRegister(d, Add(rn, ~operand, C, save)); break;
            case 7: SetRegister(d, Add(operand, ~rn, C, save)); break;
            case 8: Logic(rn & operand, true); break;
            case 9: Logic(rn ^ operand, true); break;
            case 10: Add(rn, ~operand, true, true); break;
            case 11: Add(rn, operand, false, true); break;
            case 12: SetRegister(d, Logic(rn | operand, save)); break;
            case 13: SetRegister(d, Logic(operand, save)); break;
            case 14: SetRegister(d, Logic(rn & ~operand, save)); break;
            default: SetRegister(d, Logic(~operand, save)); break;
        }

        // An S instruction with R15 as its destination in an exception mode returns from it: CPSR comes back from the
        // SPSR (for TSTP/TEQP/CMPP/CMNP too, as in ares).
        if (save && d == 15 && HasSpsr)
        {
            SetCpsr(Spsr);
        }
    }

    private uint Logic(uint result, bool save)
    {
        if (save)
        {
            C = _carry;
            Z = result == 0;
            N = (result & 0x80000000) != 0;
        }

        return result;
    }

    private uint Add(uint source, uint modify, bool carry, bool save)
    {
        var wide = (ulong)source + modify + (carry ? 1u : 0);
        var result = (uint)wide;
        if (save)
        {
            V = ((~(source ^ modify) & (source ^ result)) & 0x80000000) != 0;
            C = wide > 0xFFFFFFFF;
            Z = result == 0;
            N = (result & 0x80000000) != 0;
        }

        return result;
    }

    private void MoveToStatus(uint op, uint value)
    {
        var flags = (op & 0x80000) != 0;
        var control = (op & 0x10000) != 0;
        if ((op & 0x400000) != 0)
        {
            if (HasSpsr)
            {
                var spsr = Spsr;
                if (control)
                {
                    spsr = (spsr & ~0xFFu) | (value & 0xFF);
                }

                if (flags)
                {
                    spsr = (spsr & 0x0FFFFFFF) | (value & 0xF0000000);
                }

                Spsr = spsr;
            }

            return;
        }

        var cpsr = _cpsr;
        if (control && Mode != User)
        {
            cpsr = (cpsr & ~0xFFu) | (value & 0xDF) | 0x10; // the T bit stays clear
        }

        if (flags)
        {
            cpsr = (cpsr & 0x0FFFFFFF) | (value & 0xF0000000);
        }

        SetCpsr(cpsr);
    }

    // ---- Multiplies ----

    /// <summary>The multiplier's extra cycles: one for each byte of the multiplier beyond the first that isn't all 0s or 1s.</summary>
    private void MultiplyCycles(uint multiplier, bool signed)
    {
        _bus.Clock(1);
        for (var shift = 8; shift < 32; shift += 8)
        {
            var rest = multiplier >> shift;
            if (rest != 0 && (!signed || rest != (0xFFFFFFFF >> shift)))
            {
                _bus.Clock(1);
            }
        }
    }

    private void Multiply(uint op)
    {
        var d = (int)(op >> 16) & 15;
        var n = (int)(op >> 12) & 15;
        var s = (int)(op >> 8) & 15;
        var m = (int)op & 15;
        var accumulate = (op & 0x200000) != 0;
        if (accumulate)
        {
            _bus.Clock(1);
        }

        MultiplyCycles(R(s), signed: true);
        var product = R(m) * R(s) + (accumulate ? R(n) : 0);
        if ((op & 0x100000) != 0)
        {
            Z = product == 0;
            N = (product & 0x80000000) != 0;
        }

        if (d != 15)
        {
            SetRegister(d, product);
        }
    }

    private void MultiplyLong(uint op)
    {
        var high = (int)(op >> 16) & 15;
        var low = (int)(op >> 12) & 15;
        var s = (int)(op >> 8) & 15;
        var m = (int)op & 15;
        var signed = (op & 0x400000) != 0;
        var accumulate = (op & 0x200000) != 0;
        _bus.Clock(accumulate ? 2 : 1);
        MultiplyCycles(R(s), signed);
        var result = signed ? (ulong)((long)(int)R(m) * (int)R(s)) : (ulong)R(m) * R(s);
        if (accumulate)
        {
            result += (ulong)R(high) << 32 | R(low);
        }

        if (low != 15)
        {
            SetRegister(low, (uint)result);
        }

        if (high != 15)
        {
            SetRegister(high, (uint)(result >> 32));
        }

        if ((op & 0x100000) != 0)
        {
            Z = result == 0;
            N = (result & 0x8000000000000000) != 0;
        }
    }

    // ---- Memory ----

    /// <summary>A word load from an unaligned address rotates the word so the addressed byte is lowest.</summary>
    private uint LoadWord(uint address)
    {
        var word = _bus.ReadWord(address & ~3u);
        _bus.Clock(1);
        var rotate = (int)(address & 3) * 8;
        return rotate == 0 ? word : (word >> rotate) | (word << (32 - rotate));
    }

    private byte LoadByte(uint address)
    {
        var value = _bus.ReadByte(address);
        _bus.Clock(1);
        return value;
    }

    private void StoreWord(uint address, uint value)
    {
        _bus.WriteWord(address & ~3u, value);
        _bus.Clock(1);
    }

    private void StoreByte(uint address, byte value)
    {
        _bus.WriteByte(address, value);
        _bus.Clock(1);
    }

    private void SingleTransfer(uint op, uint pc8, bool register)
    {
        var n = (int)(op >> 16) & 15;
        var d = (int)(op >> 12) & 15;
        var load = (op & 0x100000) != 0;
        var writeback = (op & 0x200000) != 0;
        var byteSized = (op & 0x400000) != 0;
        var up = (op & 0x800000) != 0;
        var pre = (op & 0x1000000) != 0;
        _carry = C;
        var offset = register
            ? ShiftImmediate((op & 15) == 15 ? pc8 : R((int)op & 15), (int)(op >> 5) & 3, (int)(op >> 7) & 31)
            : op & 0xFFF;
        var rn = n == 15 ? pc8 : R(n);
        var rd = d == 15 ? pc8 + 4 : R(d);

        if (pre)
        {
            rn = up ? rn + offset : rn - offset;
        }

        uint loaded = 0;
        if (load)
        {
            loaded = byteSized ? LoadByte(rn) : LoadWord(rn);
        }
        else if (byteSized)
        {
            StoreByte(rn, (byte)rd);
        }
        else
        {
            StoreWord(rn, rd);
        }

        if (!pre)
        {
            rn = up ? rn + offset : rn - offset;
        }

        if (!pre || writeback)
        {
            SetRegister(n, rn);
        }

        if (load)
        {
            _bus.Clock(1);
            SetRegister(d, loaded);
        }
    }

    /// <summary>LDRH/STRH/LDRSB/LDRSH (ARMv4).</summary>
    private void Halfword(uint op, uint pc8)
    {
        var n = (int)(op >> 16) & 15;
        var d = (int)(op >> 12) & 15;
        var load = (op & 0x100000) != 0;
        var writeback = (op & 0x200000) != 0;
        var up = (op & 0x800000) != 0;
        var pre = (op & 0x1000000) != 0;
        var kind = (int)(op >> 5) & 3; // 1 halfword, 2 signed byte, 3 signed halfword
        var offset = (op & 0x400000) != 0 ? ((op >> 4) & 0xF0) | (op & 15) : R((int)op & 15);
        var rn = n == 15 ? pc8 : R(n);
        var rd = d == 15 ? pc8 + 4 : R(d);
        if (pre)
        {
            rn = up ? rn + offset : rn - offset;
        }

        uint loaded = 0;
        if (load)
        {
            if (kind == 2)
            {
                loaded = (uint)(sbyte)LoadByte(rn);
            }
            else
            {
                var word = _bus.ReadWord(rn & ~3u);
                _bus.Clock(1);
                var half = (ushort)(word >> (int)((rn & 2) * 8));
                loaded = kind == 3 ? (uint)(short)half : half;
                if ((rn & 1) != 0)
                {
                    // An odd address: the halfword rotates (LDRH) or the byte sign-extends (LDRSH), as the ARM7 does.
                    loaded = kind == 3 ? (uint)(sbyte)(byte)(half >> 8) : (loaded >> 8) | (loaded << 24);
                }
            }
        }
        else
        {
            StoreByte(rn & ~1u, (byte)rd);
            StoreByte(rn | 1, (byte)(rd >> 8));
        }

        if (!pre)
        {
            rn = up ? rn + offset : rn - offset;
        }

        if (!pre || writeback)
        {
            SetRegister(n, rn);
        }

        if (load)
        {
            _bus.Clock(1);
            SetRegister(d, loaded);
        }
    }

    private void Swap(uint op, uint pc8)
    {
        var n = (int)(op >> 16) & 15;
        var d = (int)(op >> 12) & 15;
        var m = (int)op & 15;
        var address = R(n);
        var source = m == 15 ? pc8 + 4 : R(m);
        uint loaded;
        if ((op & 0x400000) != 0)
        {
            loaded = LoadByte(address);
            StoreByte(address, (byte)source);
        }
        else
        {
            loaded = LoadWord(address);
            StoreWord(address, source);
        }

        _bus.Clock(1);
        SetRegister(d, loaded);
    }

    private void BlockTransfer(uint op, uint pc8)
    {
        var n = (int)(op >> 16) & 15;
        var list = (int)(op & 0xFFFF);
        var load = (op & 0x100000) != 0;
        var writeback = (op & 0x200000) != 0;
        var userBank = (op & 0x400000) != 0;
        var up = (op & 0x800000) != 0;
        var pre = (op & 0x1000000) != 0;

        var count = list == 0 ? 16 : global::System.Numerics.BitOperations.PopCount((uint)list);
        var start = R(n);
        var end = up ? start + (uint)count * 4 : start - (uint)count * 4;
        var address = (pre, up) switch
        {
            (false, true) => start,
            (true, true) => start + 4,
            (true, false) => start - (uint)count * 4,
            _ => start - (uint)count * 4 + 4,
        };

        // ^ without R15 in a load (or any store): the user registers.
        var mode = _cpsr;
        var usr = userBank && (!load || (list & 0x8000) == 0);
        if (usr)
        {
            SetCpsr((_cpsr & ~0x1Fu) | User);
        }

        if (writeback && load && (list & (1 << n)) == 0)
        {
            SetRegister(n, end);
        }

        if (list == 0)
        {
            list = 0x8000; // an empty list transfers R15
        }

        for (var m = 0; m < 16; m++)
        {
            if ((list & (1 << m)) == 0)
            {
                continue;
            }

            if (load)
            {
                var word = _bus.ReadWord(address & ~3u);
                _bus.Clock(1);
                SetRegister(m, word);
            }
            else
            {
                _bus.WriteWord(address & ~3u, m == 15 ? pc8 + 4 : R(m));
                _bus.Clock(1);
                if (writeback && n != 15)
                {
                    SetRegister(n, end); // after the first store
                }
            }

            address += 4;
        }

        if (usr)
        {
            SetCpsr(mode);
        }

        if (load)
        {
            _bus.Clock(1);
            if (userBank && (list & 0x8000) != 0 && HasSpsr)
            {
                SetCpsr(Spsr); // LDM with R15 and ^ returns from an exception
            }
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var bank in new[] { _r, _fiqBank, _userHigh, _bank13, _bank14, _spsr })
        {
            foreach (var value in bank)
            {
                writer.WriteUInt32(value);
            }
        }

        writer.WriteUInt32(_cpsr);
        writer.WriteUInt32(LastFetch);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        foreach (var bank in new[] { _r, _fiqBank, _userHigh, _bank13, _bank14, _spsr })
        {
            for (var i = 0; i < bank.Length; i++)
            {
                bank[i] = reader.ReadUInt32();
            }
        }

        _cpsr = reader.ReadUInt32();
        LastFetch = reader.ReadUInt32();
        reader.EndChunk();
    }
}
