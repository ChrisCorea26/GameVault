namespace GameVault.Core.Cpu.M68000;

/// <summary>The 68000's instructions, decoded by the top four bits of the opcode.</summary>
public sealed partial class M68000Cpu
{
    private static int SizeField(int bits) => bits switch
    {
        0 => 1,
        1 => 2,
        2 => 4,
        _ => 0,
    };

    // ---- 0000: bit operations, MOVEP, immediate arithmetic ----

    private void Group0()
    {
        var op = _ir;
        var mode = (op >> 3) & 7;
        var reg = op & 7;
        if ((op & 0x0100) != 0)
        {
            if (mode == 1)
            {
                Movep();
                return;
            }

            BitOperation((op >> 6) & 3, _d[(op >> 9) & 7], mode, reg, immediate: false);
            return;
        }

        var kind = (op >> 9) & 7;
        if (kind == 4)
        {
            var bit = Fetch() & 0xFFu;
            BitOperation((op >> 6) & 3, bit, mode, reg, immediate: true);
            return;
        }

        var size = SizeField((op >> 6) & 3);
        if (size == 0 || kind == 7)
        {
            Illegal();
            return;
        }

        if (mode == 7 && reg == 4)
        {
            if (kind is not (0 or 1 or 5) || size == 4)
            {
                Illegal();
                return;
            }

            if (size == 1)
            {
                var value = Fetch() & 0xFF;
                Ccr = (byte)(kind == 0 ? Ccr | value : kind == 1 ? Ccr & value : Ccr ^ value);
            }
            else if (!_s)
            {
                Exception(8, _instructionPc, 34);
                return;
            }
            else
            {
                var value = Fetch();
                SetSr((ushort)(kind == 0 ? Sr | value : kind == 1 ? Sr & value : Sr ^ value));
            }

            _cycles += 12;
            return;
        }

        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var source = Immediate(size);
        var destination = ReadEa(mode, reg, size);
        uint result;
        switch (kind)
        {
            case 0:
                result = destination | source;
                Logic(result, size);
                break;
            case 1:
                result = destination & source;
                Logic(result, size);
                break;
            case 2:
                result = Subtract(source, destination, size, extend: false);
                break;
            case 3:
                result = Add(source, destination, size, extend: false);
                break;
            case 5:
                result = destination ^ source;
                Logic(result, size);
                break;
            default:
                Compare(source, destination, size);
                if (mode == 0 && size == 4)
                {
                    _cycles += 2;
                }

                return;
        }

        if (mode == 0 && size == 4)
        {
            _cycles += 4;
        }

        WriteBack(mode, reg, size, result);
    }

    /// <summary>BTST, BCHG, BCLR and BSET (kind 0–3). Registers are 32 bits (bit mod 32), memory is a byte (mod 8).</summary>
    private void BitOperation(int kind, uint bitNumber, int mode, int reg, bool immediate)
    {
        var allowed = kind == 0 ? (immediate ? EaData & ~(1 << 11) : EaData) : EaDataAlterable;
        if (!Valid(mode, reg, allowed))
        {
            Illegal();
            return;
        }

        if (mode == 0)
        {
            var bit = (int)(bitNumber & 31);
            var value = _d[reg];
            _z = ((value >> bit) & 1) == 0;
            switch (kind)
            {
                case 0:
                    _cycles += 2;
                    return;
                case 1:
                    value ^= 1u << bit;
                    break;
                case 2:
                    value &= ~(1u << bit);
                    _cycles += 2;
                    break;
                default:
                    value |= 1u << bit;
                    break;
            }

            _cycles += bit >= 16 ? 4 : 2;
            _d[reg] = value;
            return;
        }

        var memoryBit = (int)(bitNumber & 7);
        var data = ReadEa(mode, reg, 1);
        _z = ((data >> memoryBit) & 1) == 0;
        if (kind == 0)
        {
            if (mode == 7 && reg == 4)
            {
                _cycles += 2; // BTST Dn,#imm
            }

            return;
        }

        data = kind switch
        {
            1 => data ^ (1u << memoryBit),
            2 => data & ~(1u << memoryBit),
            _ => data | (1u << memoryBit),
        };
        WriteBack(mode, reg, 1, data);
    }

    /// <summary>MOVEP: a data register to or from every other byte at d16(An), for 8-bit peripherals.</summary>
    private void Movep()
    {
        var dataReg = (_ir >> 9) & 7;
        var address = _a[_ir & 7] + (uint)(short)Fetch();
        switch ((_ir >> 6) & 7)
        {
            case 4:
            {
                var value = (uint)ReadByte(address) << 8;
                value |= ReadByte(address + 2);
                SetDataRegister(dataReg, value, 2);
                break;
            }

            case 5:
            {
                var value = (uint)ReadByte(address) << 24;
                value |= (uint)ReadByte(address + 2) << 16;
                value |= (uint)ReadByte(address + 4) << 8;
                value |= ReadByte(address + 6);
                _d[dataReg] = value;
                break;
            }

            case 6:
                WriteByte(address, _d[dataReg] >> 8);
                WriteByte(address + 2, _d[dataReg]);
                break;
            default:
                WriteByte(address, _d[dataReg] >> 24);
                WriteByte(address + 2, _d[dataReg] >> 16);
                WriteByte(address + 4, _d[dataReg] >> 8);
                WriteByte(address + 6, _d[dataReg]);
                break;
        }
    }

    // ---- 0001/0010/0011: MOVE and MOVEA ----

    private void Move(int size)
    {
        var sourceMode = (_ir >> 3) & 7;
        var sourceReg = _ir & 7;
        var destMode = (_ir >> 6) & 7;
        var destReg = (_ir >> 9) & 7;
        if (!Valid(sourceMode, sourceReg, EaAll) || (size == 1 && sourceMode == 1))
        {
            Illegal();
            return;
        }

        if (destMode == 1)
        {
            if (size == 1)
            {
                Illegal();
                return;
            }

            _a[destReg] = SignExtend(ReadEa(sourceMode, sourceReg, size), size);
            return;
        }

        if (!Valid(destMode, destReg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var value = ReadEa(sourceMode, sourceReg, size);
        Logic(value, size);
        if (size == 1)
        {
            WriteEa(destMode, destReg, size, value, predecrementPenalty: false);
            return;
        }

        // MOVE's destination faults differ from other instructions': (An)+ hasn't moved yet; -(An) has dropped by
        // 2 (a long's low word goes first) and pushes the next instruction's PC; abs.L after a memory source pushes
        // the PC before its second word's prefetch.
        uint address;
        switch (destMode)
        {
            case 3:
                address = _a[destReg];
                if ((address & 1) != 0)
                {
                    Fault(address, read: false, program: false);
                }

                _a[destReg] += (uint)size;
                break;
            case 4:
                if ((_a[destReg] & 1) != 0)
                {
                    _a[destReg] -= 2;
                    _faultPc = _pc;
                    Fault(_a[destReg], read: false, program: false);
                }

                _a[destReg] -= (uint)size;
                address = _a[destReg];
                break;
            case 7 when destReg == 1:
                address = FetchLong();
                if ((address & 1) != 0)
                {
                    _faultPc = sourceMode >= 2 && !(sourceMode == 7 && sourceReg == 4) ? _pc - 4 : _pc - 2;
                    Fault(address, read: false, program: false);
                }

                break;
            case 0:
                SetDataRegister(destReg, value, size);
                return;
            default:
                address = Address(destMode, destReg, size, predecrementPenalty: false);
                break;
        }

        Write(address, size, value);
    }

    private void Moveq()
    {
        if ((_ir & 0x0100) != 0)
        {
            Illegal();
            return;
        }

        var value = (uint)(sbyte)_ir;
        _d[(_ir >> 9) & 7] = value;
        Logic(value, 4);
    }

    // ---- 0100: miscellaneous ----

    private void Group4()
    {
        var op = _ir;
        var mode = (op >> 3) & 7;
        var reg = op & 7;
        var sizeBits = (op >> 6) & 3;
        if ((op & 0x01C0) == 0x01C0)
        {
            Lea(mode, reg);
            return;
        }

        if ((op & 0x01C0) == 0x0180)
        {
            Chk(mode, reg);
            return;
        }

        if ((op & 0x0100) != 0)
        {
            Illegal();
            return;
        }

        switch ((op >> 8) & 0xF)
        {
            case 0x0:
                if (sizeBits == 3)
                {
                    MoveFromSr(mode, reg);
                }
                else
                {
                    Negate(mode, reg, SizeField(sizeBits), extend: true);
                }

                return;
            case 0x2:
                if (sizeBits == 3)
                {
                    Illegal();
                }
                else
                {
                    Clear(mode, reg, SizeField(sizeBits));
                }

                return;
            case 0x4:
                if (sizeBits == 3)
                {
                    MoveToCcr(mode, reg);
                }
                else
                {
                    Negate(mode, reg, SizeField(sizeBits), extend: false);
                }

                return;
            case 0x6:
                if (sizeBits == 3)
                {
                    if (!_s)
                    {
                        Exception(8, _instructionPc, 34);
                    }
                    else
                    {
                        MoveToSr(mode, reg);
                    }
                }
                else
                {
                    Not(mode, reg, SizeField(sizeBits));
                }

                return;
            case 0x8:
                switch (sizeBits)
                {
                    case 0: Nbcd(mode, reg); return;
                    case 1:
                        if (mode == 0)
                        {
                            _d[reg] = (_d[reg] >> 16) | (_d[reg] << 16);
                            Logic(_d[reg], 4);
                        }
                        else
                        {
                            Pea(mode, reg);
                        }

                        return;
                    default:
                        if (mode == 0)
                        {
                            Extend(reg, sizeBits == 3);
                        }
                        else
                        {
                            MovemToMemory(mode, reg, sizeBits == 3 ? 4 : 2);
                        }

                        return;
                }

            case 0xA:
                if (sizeBits == 3)
                {
                    if (op == 0x4AFC)
                    {
                        Illegal();
                    }
                    else
                    {
                        Tas(mode, reg);
                    }
                }
                else
                {
                    Test(mode, reg, SizeField(sizeBits));
                }

                return;
            case 0xC:
                if (sizeBits >= 2)
                {
                    MovemToRegisters(mode, reg, sizeBits == 3 ? 4 : 2);
                }
                else
                {
                    Illegal();
                }

                return;
            case 0xE:
                switch (sizeBits)
                {
                    case 1: Group4E(); return;
                    case 2: Jump(mode, reg, subroutine: true); return;
                    case 3: Jump(mode, reg, subroutine: false); return;
                    default: Illegal(); return;
                }

            default:
                Illegal();
                return;
        }
    }

    private void Group4E()
    {
        var op = _ir;
        var reg = op & 7;
        switch ((op >> 3) & 7)
        {
            case 0:
            case 1:
                // TRAP #n: the pushed PC is the next instruction's.
                Exception(32 + (op & 0xF), _pc, 34);
                return;
            case 2:
            {
                // LINK An,#d16: push An, An = SP, SP += d16.
                var displacement = (uint)(short)Fetch();
                _a[7] -= 4;
                var pushed = _a[reg];
                WriteLong(_a[7], pushed);
                _a[reg] = _a[7];
                _a[7] += displacement;
                return;
            }

            case 3:
            {
                // UNLK An: SP = An, then An = pop.
                var address = _a[reg];
                var value = ReadLong(address);
                _a[7] = address + 4;
                _a[reg] = value;
                return;
            }

            case 4:
                if (Privileged())
                {
                    Usp = _a[reg];
                }

                return;
            case 5:
                if (Privileged())
                {
                    _a[reg] = Usp;
                }

                return;
        }

        switch (op)
        {
            case 0x4E70: // RESET
                if (Privileged())
                {
                    ResetDevices?.Invoke();
                    _cycles += 128;
                }

                return;
            case 0x4E71: // NOP
                return;
            case 0x4E72: // STOP #imm
                if (Privileged())
                {
                    var value = Fetch();
                    SetSr(value);
                    _stopped = true;
                }

                return;
            case 0x4E73: // RTE
                if (Privileged())
                {
                    var sr = Pop16();
                    var pc = Pop32();
                    SetSr((ushort)sr);
                    JumpTo(pc);
                }

                return;
            case 0x4E75: // RTS
                JumpTo(Pop32());
                return;
            case 0x4E76: // TRAPV
                if (_v)
                {
                    Exception(7, _pc, 34);
                }

                return;
            case 0x4E77: // RTR
            {
                var ccr = Pop16();
                var pc = Pop32();
                Ccr = (byte)ccr;
                JumpTo(pc);
                return;
            }

            default:
                Illegal();
                return;
        }
    }

    /// <summary>A jump refills the prefetch queue: the target's first word is read now (and checked for alignment).</summary>
    private void JumpTo(uint target)
    {
        if ((target & 1) != 0)
        {
            JumpFault(target);
        }

        _pc = target;
        _cycles += 4;
    }

    private static readonly int[] JumpCycles = [0, 0, 8, 0, 0, 10, 14, 10, 12, 10, 14];

    private void Jump(int mode, int reg, bool subroutine)
    {
        if (!Valid(mode, reg, EaControl))
        {
            Illegal();
            return;
        }

        var target = Address(mode, reg, 4);
        var returnAddress = _pc;
        var cycles = JumpCycles[ModeIndex(mode, reg)];
        if ((target & 1) != 0)
        {
            JumpFault(target);
        }

        if (subroutine)
        {
            Push32(returnAddress);
            cycles += 8;
        }

        _pc = target;
        _cycles = cycles;
    }

    private void Lea(int mode, int reg)
    {
        if (!Valid(mode, reg, EaControl))
        {
            Illegal();
            return;
        }

        _a[(_ir >> 9) & 7] = Address(mode, reg, 4);
        if (mode == 6 || (mode == 7 && reg == 3))
        {
            _cycles += 2;
        }
    }

    private void Pea(int mode, int reg)
    {
        if (!Valid(mode, reg, EaControl))
        {
            Illegal();
            return;
        }

        var address = Address(mode, reg, 4);
        if (mode == 6 || (mode == 7 && reg == 3))
        {
            _cycles += 2;
        }

        Push32(address);
    }

    /// <summary>CHK &lt;ea&gt;,Dn: a trap when Dn.w is below 0 or above the bound.</summary>
    private void Chk(int mode, int reg)
    {
        if (!Valid(mode, reg, EaData))
        {
            Illegal();
            return;
        }

        var bound = (short)ReadEa(mode, reg, 2);
        var value = (short)_d[(_ir >> 9) & 7];
        _z = value == 0;
        _v = false;
        _c = false;
        if (value < 0 || value > bound)
        {
            // Above the bound takes 38 cycles plus the operand's; below 0 (and not above) takes 40.
            _n = value < 0;
            Exception(6, _pc, _cycles + (value > bound ? 34 : 36));
        }
        else
        {
            _cycles += 6;
        }
    }

    private void MoveFromSr(int mode, int reg)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var sr = Sr;
        if (mode == 0)
        {
            SetDataRegister(reg, sr, 2);
            _cycles += 2;
            return;
        }

        ReadEa(mode, reg, 2); // the 68000 reads before it writes
        WriteBack(mode, reg, 2, sr);
    }

    private void MoveToCcr(int mode, int reg)
    {
        if (!Valid(mode, reg, EaData))
        {
            Illegal();
            return;
        }

        Ccr = (byte)ReadEa(mode, reg, 2);
        _cycles += 8;
    }

    private void MoveToSr(int mode, int reg)
    {
        if (!Valid(mode, reg, EaData))
        {
            Illegal();
            return;
        }

        SetSr((ushort)ReadEa(mode, reg, 2));
        _cycles += 8;
    }

    private void Negate(int mode, int reg, int size, bool extend)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var value = ReadEa(mode, reg, size);
        var result = Subtract(value, 0, size, extend);
        if (mode == 0 && size == 4)
        {
            _cycles += 2;
        }

        WriteBack(mode, reg, size, result);
    }

    private void Clear(int mode, int reg, int size)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        if (mode == 0)
        {
            SetDataRegister(reg, 0, size);
            if (size == 4)
            {
                _cycles += 2;
            }
        }
        else
        {
            ReadEa(mode, reg, size); // CLR reads before it writes on the 68000
            WriteBack(mode, reg, size, 0);
        }

        _n = false;
        _z = true;
        _v = false;
        _c = false;
    }

    private void Not(int mode, int reg, int size)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var result = ~ReadEa(mode, reg, size) & Mask(size);
        Logic(result, size);
        if (mode == 0 && size == 4)
        {
            _cycles += 2;
        }

        WriteBack(mode, reg, size, result);
    }

    private void Extend(int reg, bool toLong)
    {
        if (toLong)
        {
            _d[reg] = (uint)(short)_d[reg];
            Logic(_d[reg], 4);
        }
        else
        {
            SetDataRegister(reg, (uint)(sbyte)_d[reg], 2);
            Logic(_d[reg], 2);
        }
    }

    private void Test(int mode, int reg, int size)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        Logic(ReadEa(mode, reg, size), size);
    }

    /// <summary>TAS: test a byte and set its bit 7 in one indivisible read-modify-write.</summary>
    private void Tas(int mode, int reg)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var value = ReadEa(mode, reg, 1);
        Logic(value, 1);
        if (mode != 0)
        {
            _cycles += 2;
        }

        WriteBack(mode, reg, 1, value | 0x80);
    }

    private void MovemToMemory(int mode, int reg, int size)
    {
        if (!Valid(mode, reg, EaControlAlterable) && mode != 4)
        {
            Illegal();
            return;
        }

        var mask = Fetch();
        if (mode == 4)
        {
            // -(An): the mask is reversed (bit 0 is A7) and registers are stored from A7 down to D0. The 68000 stores
            // An's original value if An is in the list.
            var address = _a[reg];
            var start = _a[reg];
            for (var i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) == 0)
                {
                    continue;
                }

                var index = 15 - i;
                address -= (uint)size;
                if (size == 4 && (address & 1) != 0)
                {
                    Fault(address + 2, read: false, program: false); // the low word is written first
                }

                var value = index >= 8 ? (index - 8 == reg ? start : _a[index - 8]) : _d[index];
                Write(address, size, value);
            }

            _a[reg] = address;
            return;
        }

        var target = Address(mode, reg, size);
        for (var i = 0; i < 16; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            Write(target, size, i >= 8 ? _a[i - 8] : _d[i]);
            target += (uint)size;
        }
    }

    private void MovemToRegisters(int mode, int reg, int size)
    {
        if (!Valid(mode, reg, EaControl) && mode != 3)
        {
            Illegal();
            return;
        }

        var mask = Fetch();
        var address = mode == 3 ? _a[reg] : Address(mode, reg, size);
        const bool program = false;
        if (mode == 3 && (address & 1) != 0)
        {
            _a[reg] = address + 2;
            Fault(address, read: true, program);
        }

        for (var i = 0; i < 16; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            var value = SignExtend(Read(address, size, program), size);
            if (i >= 8)
            {
                _a[i - 8] = value;
            }
            else
            {
                _d[i] = value;
            }

            address += (uint)size;
        }

        ReadWord(address, program); // the 68000 reads one more word than it needs
        if (mode == 3)
        {
            _a[reg] = address;
        }
    }

    // ---- 0101: ADDQ, SUBQ, Scc, DBcc ----

    private void Group5()
    {
        var mode = (_ir >> 3) & 7;
        var reg = _ir & 7;
        var sizeBits = (_ir >> 6) & 3;
        if (sizeBits == 3)
        {
            var condition = (_ir >> 8) & 0xF;
            if (mode == 1)
            {
                DecrementAndBranch(condition, reg);
                return;
            }

            if (!Valid(mode, reg, EaDataAlterable))
            {
                Illegal();
                return;
            }

            var set = Condition(condition);
            if (mode == 0)
            {
                SetDataRegister(reg, set ? 0xFFu : 0, 1);
                if (set)
                {
                    _cycles += 2;
                }

                return;
            }

            ReadEa(mode, reg, 1); // Scc reads before it writes
            WriteBack(mode, reg, 1, set ? 0xFFu : 0);
            return;
        }

        var size = SizeField(sizeBits);
        if (!Valid(mode, reg, EaAlterable) || (mode == 1 && size == 1))
        {
            Illegal();
            return;
        }

        var data = (uint)((_ir >> 9) & 7);
        if (data == 0)
        {
            data = 8;
        }

        var subtract = (_ir & 0x0100) != 0;
        if (mode == 1)
        {
            // Address registers change in full, whatever the size, and keep the flags.
            _a[reg] = subtract ? _a[reg] - data : _a[reg] + data;
            _cycles += size == 4 ? 2 : 4;
            return;
        }

        var destination = ReadEa(mode, reg, size);
        var result = subtract ? Subtract(data, destination, size, extend: false) : Add(data, destination, size, extend: false);
        if (mode == 0 && size == 4)
        {
            _cycles += 4;
        }

        WriteBack(mode, reg, size, result);
    }

    private void DecrementAndBranch(int condition, int reg)
    {
        var baseAddress = _pc;
        var displacement = (uint)(short)Fetch();
        if (Condition(condition))
        {
            _cycles = 12;
            return;
        }

        var counter = (ushort)(_d[reg] - 1);
        SetDataRegister(reg, counter, 2);
        if (counter == 0xFFFF)
        {
            _cycles = 14;
            return;
        }

        var target = baseAddress + displacement;
        if ((target & 1) != 0)
        {
            JumpFault(target);
        }

        _pc = target;
        _cycles = 10;
    }

    // ---- 0110: Bcc, BRA, BSR ----

    private void Branch()
    {
        var condition = (_ir >> 8) & 0xF;
        var baseAddress = _pc;
        var displacement = (uint)(sbyte)_ir;
        var wordDisplacement = displacement == 0;
        if (wordDisplacement)
        {
            displacement = (uint)(short)Fetch();
        }

        var target = baseAddress + displacement;
        if (condition == 1)
        {
            // BSR: push the address after the displacement (before the target's alignment is checked).
            Push32(_pc);
            if ((target & 1) != 0)
            {
                JumpFault(target);
            }

            _pc = target;
            _cycles = 18;
            return;
        }

        if (!Condition(condition))
        {
            _cycles = wordDisplacement ? 12 : 8;
            return;
        }

        if ((target & 1) != 0)
        {
            JumpFault(target);
        }

        _pc = target;
        _cycles = 10;
    }

    // ---- 1000: OR, DIVU, DIVS, SBCD ----

    private void Group8()
    {
        var mode = (_ir >> 3) & 7;
        var reg = _ir & 7;
        var dataReg = (_ir >> 9) & 7;
        var opmode = (_ir >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            Divide(mode, reg, dataReg, signed: opmode == 7);
            return;
        }

        if (opmode == 4 && mode <= 1)
        {
            DecimalRegisterOrMemory(mode, reg, dataReg, subtract: true);
            return;
        }

        LogicalToRegisterOrMemory(mode, reg, dataReg, opmode, and: false);
    }

    /// <summary>AND and OR: &lt;ea&gt; op Dn → Dn (opmode 0–2), or Dn op &lt;ea&gt; → &lt;ea&gt; (opmode 4–6).</summary>
    private void LogicalToRegisterOrMemory(int mode, int reg, int dataReg, int opmode, bool and)
    {
        var size = SizeField(opmode & 3);
        if (opmode < 4)
        {
            if (!Valid(mode, reg, EaData))
            {
                Illegal();
                return;
            }

            var operand = ReadEa(mode, reg, size);
            var result = (and ? operand & _d[dataReg] : operand | _d[dataReg]) & Mask(size);
            Logic(result, size);
            SetDataRegister(dataReg, result, size);
            if (size == 4)
            {
                _cycles += mode == 0 || (mode == 7 && reg == 4) ? 4 : 2;
            }

            return;
        }

        if (!Valid(mode, reg, EaMemoryAlterable))
        {
            Illegal();
            return;
        }

        var memory = ReadEa(mode, reg, size);
        var value = (and ? memory & _d[dataReg] : memory | _d[dataReg]) & Mask(size);
        Logic(value, size);
        WriteBack(mode, reg, size, value);
    }

    private void Divide(int mode, int reg, int dataReg, bool signed)
    {
        if (!Valid(mode, reg, EaData))
        {
            Illegal();
            return;
        }

        var divisor = ReadEa(mode, reg, 2);
        var dividend = _d[dataReg];
        if (divisor == 0)
        {
            // Divide by zero: N, Z and V clear, C clear.
            _n = false;
            _z = false;
            _v = false;
            _c = false;
            Exception(5, _pc, _cycles + 34);
            return;
        }

        _c = false;
        if (!signed)
        {
            _cycles += DivuCycles(dividend, (ushort)divisor) - 4;
            var quotient = dividend / divisor;
            if (quotient > 0xFFFF)
            {
                // Overflow: V set, C clear, N and Z unchanged; the register keeps its value.
                _v = true;
                return;
            }

            var remainder = dividend % divisor;
            _d[dataReg] = (remainder << 16) | quotient;
            _v = false;
            _n = (quotient & 0x8000) != 0;
            _z = quotient == 0;
            return;
        }

        var signedDividend = (int)dividend;
        var signedDivisor = (int)(short)divisor;
        _cycles += DivsCycles(signedDividend, (short)signedDivisor) - 4;
        var signedQuotient = (long)signedDividend / signedDivisor;
        if (signedQuotient is > short.MaxValue or < short.MinValue)
        {
            _v = true;
            return;
        }

        var signedRemainder = (long)signedDividend % signedDivisor;
        _d[dataReg] = ((uint)(ushort)signedRemainder << 16) | (ushort)signedQuotient;
        _v = false;
        _n = signedQuotient < 0;
        _z = signedQuotient == 0;
    }

    /// <summary>DIVU's cycles, which depend on the quotient's bits (Jorge Cwik, "68000 division timing").</summary>
    private static int DivuCycles(uint dividend, ushort divisor)
    {
        if ((dividend >> 16) >= divisor)
        {
            return 10;
        }

        var cycles = 38;
        var high = (uint)divisor << 16;
        for (var i = 0; i < 15; i++)
        {
            var before = dividend;
            dividend <<= 1;
            if ((int)before < 0)
            {
                dividend -= high;
            }
            else
            {
                cycles += 2;
                if (dividend >= high)
                {
                    dividend -= high;
                    cycles--;
                }
            }
        }

        return cycles * 2;
    }

    private static int DivsCycles(int dividend, short divisor)
    {
        var cycles = 6;
        if (dividend < 0)
        {
            cycles++;
        }

        var absDividend = (uint)Math.Abs((long)dividend);
        var absDivisor = (uint)Math.Abs((int)divisor);
        var signedQuotient = (long)dividend / divisor;
        if ((absDividend >> 16) >= absDivisor || signedQuotient is > short.MaxValue or < short.MinValue)
        {
            return (cycles + 2) * 2; // overflow is found early
        }

        var quotient = absDividend / absDivisor;
        cycles += 55;
        if (divisor >= 0)
        {
            cycles += dividend >= 0 ? -1 : 1;
        }

        for (var i = 0; i < 15; i++)
        {
            if ((short)quotient >= 0)
            {
                cycles++;
            }

            quotient <<= 1;
        }

        return cycles * 2;
    }

    // ---- 1001 / 1101: SUB, SUBA, SUBX, ADD, ADDA, ADDX ----

    private void AddSub(bool subtract)
    {
        var mode = (_ir >> 3) & 7;
        var reg = _ir & 7;
        var dataReg = (_ir >> 9) & 7;
        var opmode = (_ir >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            // ADDA/SUBA: the source is sign-extended and the whole address register changes, without flags.
            var size = opmode == 3 ? 2 : 4;
            if (!Valid(mode, reg, EaAll))
            {
                Illegal();
                return;
            }

            var source = SignExtend(ReadEa(mode, reg, size), size);
            _a[dataReg] = subtract ? _a[dataReg] - source : _a[dataReg] + source;
            _cycles += size == 2 || mode <= 1 || (mode == 7 && reg == 4) ? 4 : 2;
            return;
        }

        var operandSize = SizeField(opmode & 3);
        if (opmode >= 4 && mode <= 1)
        {
            // ADDX/SUBX: Dy,Dx or -(Ay),-(Ax).
            uint source;
            uint destination;
            if (mode == 0)
            {
                source = _d[reg] & Mask(operandSize);
                destination = _d[dataReg] & Mask(operandSize);
                var result = subtract ? Subtract(source, destination, operandSize, extend: true) : Add(source, destination, operandSize, extend: true);
                SetDataRegister(dataReg, result, operandSize);
                if (operandSize == 4)
                {
                    _cycles += 4;
                }

                return;
            }

            _cycles += 2;
            source = ReadPredecrement(reg, operandSize);
            destination = ReadPredecrement(dataReg, operandSize);
            var memoryResult = subtract ? Subtract(source, destination, operandSize, extend: true) : Add(source, destination, operandSize, extend: true);
            Write(_a[dataReg], operandSize, memoryResult);
            return;
        }

        if (opmode < 4)
        {
            if (!Valid(mode, reg, EaAll) || (mode == 1 && operandSize == 1))
            {
                Illegal();
                return;
            }

            var source = ReadEa(mode, reg, operandSize);
            var destination = _d[dataReg] & Mask(operandSize);
            var result = subtract ? Subtract(source, destination, operandSize, extend: false) : Add(source, destination, operandSize, extend: false);
            SetDataRegister(dataReg, result, operandSize);
            if (operandSize == 4)
            {
                _cycles += mode <= 1 || (mode == 7 && reg == 4) ? 4 : 2;
            }

            return;
        }

        if (!Valid(mode, reg, EaMemoryAlterable))
        {
            Illegal();
            return;
        }

        var value = ReadEa(mode, reg, operandSize);
        var sum = subtract ? Subtract(_d[dataReg] & Mask(operandSize), value, operandSize, extend: false) : Add(_d[dataReg] & Mask(operandSize), value, operandSize, extend: false);
        WriteBack(mode, reg, operandSize, sum);
    }

    /// <summary>-(An) for ADDX, SUBX, ABCD and SBCD: the register drops first, then the operand is read.</summary>
    private uint ReadPredecrement(int reg, int size)
    {
        if (size == 4)
        {
            // A long operand is read low word first: the register drops by 2, then by 2 again.
            _a[reg] -= 2;
            if ((_a[reg] & 1) != 0)
            {
                Fault(_a[reg], read: true, program: false);
            }

            _a[reg] -= 2;
            return ReadLong(_a[reg]);
        }

        _a[reg] -= (uint)(reg == 7 && size == 1 ? 2 : size);
        return Read(_a[reg], size);
    }

    // ---- 1011: CMP, CMPA, CMPM, EOR ----

    private void GroupB()
    {
        var mode = (_ir >> 3) & 7;
        var reg = _ir & 7;
        var dataReg = (_ir >> 9) & 7;
        var opmode = (_ir >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            var size = opmode == 3 ? 2 : 4;
            if (!Valid(mode, reg, EaAll))
            {
                Illegal();
                return;
            }

            var source = SignExtend(ReadEa(mode, reg, size), size);
            Compare(source, _a[dataReg], 4);
            _cycles += 2;
            return;
        }

        var operandSize = SizeField(opmode & 3);
        if (opmode < 3)
        {
            if (!Valid(mode, reg, EaAll) || (mode == 1 && operandSize == 1))
            {
                Illegal();
                return;
            }

            Compare(ReadEa(mode, reg, operandSize), _d[dataReg] & Mask(operandSize), operandSize);
            if (operandSize == 4)
            {
                _cycles += 2;
            }

            return;
        }

        if (mode == 1)
        {
            // CMPM (Ay)+,(Ax)+: each register moves on before its read.
            var sourceAddress = _a[reg];
            _a[reg] += (uint)(reg == 7 && operandSize == 1 ? 2 : operandSize);
            var source = Read(sourceAddress, operandSize);
            var destinationAddress = _a[dataReg];
            _a[dataReg] += (uint)(dataReg == 7 && operandSize == 1 ? 2 : operandSize);
            var destination = Read(destinationAddress, operandSize);
            Compare(source, destination, operandSize);
            return;
        }

        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var value = (ReadEa(mode, reg, operandSize) ^ _d[dataReg]) & Mask(operandSize);
        Logic(value, operandSize);
        if (mode == 0 && operandSize == 4)
        {
            _cycles += 4;
        }

        WriteBack(mode, reg, operandSize, value);
    }

    // ---- 1100: AND, MULU, MULS, ABCD, EXG ----

    private void GroupC()
    {
        var mode = (_ir >> 3) & 7;
        var reg = _ir & 7;
        var dataReg = (_ir >> 9) & 7;
        var opmode = (_ir >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            Multiply(mode, reg, dataReg, signed: opmode == 7);
            return;
        }

        if (opmode == 4 && mode <= 1)
        {
            DecimalRegisterOrMemory(mode, reg, dataReg, subtract: false);
            return;
        }

        if (opmode == 5 && mode <= 1)
        {
            if (mode == 0)
            {
                (_d[dataReg], _d[reg]) = (_d[reg], _d[dataReg]);
            }
            else
            {
                (_a[dataReg], _a[reg]) = (_a[reg], _a[dataReg]);
            }

            _cycles += 2;
            return;
        }

        if (opmode == 6 && mode == 1)
        {
            (_d[dataReg], _a[reg]) = (_a[reg], _d[dataReg]);
            _cycles += 2;
            return;
        }

        LogicalToRegisterOrMemory(mode, reg, dataReg, opmode, and: true);
    }

    private void Multiply(int mode, int reg, int dataReg, bool signed)
    {
        if (!Valid(mode, reg, EaData))
        {
            Illegal();
            return;
        }

        var source = ReadEa(mode, reg, 2);
        uint result;
        int bits;
        if (signed)
        {
            result = (uint)((short)source * (short)_d[dataReg]);
            bits = System.Numerics.BitOperations.PopCount(((source << 1) ^ source) & 0xFFFF);
        }
        else
        {
            result = source * (_d[dataReg] & 0xFFFF);
            bits = System.Numerics.BitOperations.PopCount(source);
        }

        _d[dataReg] = result;
        Logic(result, 4);
        _cycles += 34 + 2 * bits;
    }

    /// <summary>ABCD and SBCD, Dy,Dx or -(Ay),-(Ax).</summary>
    private void DecimalRegisterOrMemory(int mode, int reg, int dataReg, bool subtract)
    {
        if (mode == 0)
        {
            var result = subtract ? SubtractDecimal(_d[reg] & 0xFF, _d[dataReg] & 0xFF) : AddDecimal(_d[reg] & 0xFF, _d[dataReg] & 0xFF);
            SetDataRegister(dataReg, result, 1);
            _cycles += 2;
            return;
        }

        _cycles += 2;
        var source = ReadPredecrement(reg, 1);
        var destination = ReadPredecrement(dataReg, 1);
        Write(_a[dataReg], 1, subtract ? SubtractDecimal(source, destination) : AddDecimal(source, destination));
    }

    private void Nbcd(int mode, int reg)
    {
        if (!Valid(mode, reg, EaDataAlterable))
        {
            Illegal();
            return;
        }

        var value = ReadEa(mode, reg, 1);
        var result = SubtractDecimal(value, 0);
        if (mode == 0)
        {
            _cycles += 2;
        }

        WriteBack(mode, reg, 1, result);
    }

    // ---- 1110: shifts and rotates ----

    private void Shift()
    {
        var sizeBits = (_ir >> 6) & 3;
        var left = (_ir & 0x0100) != 0;
        if (sizeBits == 3)
        {
            // Memory: a word, by one bit.
            var mode = (_ir >> 3) & 7;
            var reg = _ir & 7;
            if ((_ir & 0x0800) != 0 || !Valid(mode, reg, EaMemoryAlterable))
            {
                Illegal();
                return;
            }

            var value = ReadEa(mode, reg, 2);
            WriteBack(mode, reg, 2, ShiftValue((_ir >> 9) & 3, left, value, 1, 2));
            return;
        }

        var size = SizeField(sizeBits);
        var dataReg = _ir & 7;
        var countField = (_ir >> 9) & 7;
        var count = (_ir & 0x20) != 0 ? (int)(_d[countField] & 63) : countField == 0 ? 8 : countField;
        var result = ShiftValue((_ir >> 3) & 3, left, _d[dataReg] & Mask(size), count, size);
        SetDataRegister(dataReg, result, size);
        _cycles += (size == 4 ? 4 : 2) + 2 * count;
    }

    /// <summary>ASx (0), LSx (1), ROXx (2) and ROx (3) by <paramref name="count"/> bits, with their flags.</summary>
    private uint ShiftValue(int type, bool left, uint value, int count, int size)
    {
        var msb = Msb(size);
        var mask = Mask(size);
        _v = false;
        if (count == 0)
        {
            _c = type == 2 && _x;
            SetNz(value, size);
            return value;
        }

        switch (type)
        {
            case 0:
                if (left)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var before = value & msb;
                        _c = before != 0;
                        value = (value << 1) & mask;
                        if ((value & msb) != before)
                        {
                            _v = true;
                        }
                    }
                }
                else
                {
                    var sign = value & msb;
                    for (var i = 0; i < count; i++)
                    {
                        _c = (value & 1) != 0;
                        value = (value >> 1) | sign;
                    }

                    // Past the operand's width the 68000 shifts out zeros, not copies of the sign (SingleStepTests).
                    if (count > size * 8)
                    {
                        _c = false;
                    }
                }

                _x = _c;
                break;
            case 1:
                for (var i = 0; i < count; i++)
                {
                    if (left)
                    {
                        _c = (value & msb) != 0;
                        value = (value << 1) & mask;
                    }
                    else
                    {
                        _c = (value & 1) != 0;
                        value >>= 1;
                    }
                }

                _x = _c;
                break;
            case 2:
                for (var i = 0; i < count; i++)
                {
                    if (left)
                    {
                        var outBit = (value & msb) != 0;
                        value = ((value << 1) & mask) | (_x ? 1u : 0);
                        _x = outBit;
                    }
                    else
                    {
                        var outBit = (value & 1) != 0;
                        value = (value >> 1) | (_x ? msb : 0);
                        _x = outBit;
                    }
                }

                _c = _x;
                break;
            default:
                for (var i = 0; i < count; i++)
                {
                    if (left)
                    {
                        _c = (value & msb) != 0;
                        value = ((value << 1) & mask) | (_c ? 1u : 0);
                    }
                    else
                    {
                        _c = (value & 1) != 0;
                        value = (value >> 1) | (_c ? msb : 0);
                    }
                }

                break;
        }

        SetNz(value, size);
        return value;
    }

    // ---- Arithmetic and flags ----

    private void Logic(uint value, int size)
    {
        SetNz(value, size);
        _v = false;
        _c = false;
    }

    private uint Add(uint source, uint destination, int size, bool extend)
    {
        var mask = Mask(size);
        var msb = Msb(size);
        source &= mask;
        destination &= mask;
        var wide = (ulong)source + destination + (extend && _x ? 1u : 0u);
        var result = (uint)wide & mask;
        _c = _x = (wide >> (size * 8)) != 0;
        _v = ((source ^ result) & (destination ^ result) & msb) != 0;
        _n = (result & msb) != 0;
        _z = extend ? _z && result == 0 : result == 0;
        return result;
    }

    private uint Subtract(uint source, uint destination, int size, bool extend)
    {
        var mask = Mask(size);
        var msb = Msb(size);
        source &= mask;
        destination &= mask;
        var result = (destination - source - (extend && _x ? 1u : 0u)) & mask;
        _c = _x = (((source & ~destination) | (result & ~destination) | (source & result)) & msb) != 0;
        _v = ((source ^ destination) & (result ^ destination) & msb) != 0;
        _n = (result & msb) != 0;
        _z = extend ? _z && result == 0 : result == 0;
        return result;
    }

    private void Compare(uint source, uint destination, int size)
    {
        var x = _x;
        Subtract(source, destination, size, extend: false);
        _x = x;
    }

    /// <summary>ABCD: decimal add with extend, including the 68000's undocumented V and N.</summary>
    private uint AddDecimal(uint source, uint destination)
    {
        var extend = _x ? 1 : 0;
        var unadjusted = (int)(destination + source) + extend;
        var result = (int)(destination & 0xF) + (int)(source & 0xF) + extend;
        result += (int)(destination & 0xF0) + (int)(source & 0xF0) + (((9 - result) >> 4) & 0x06);
        result += ((0x9F - result) >> 4) & 0x60;
        _c = _x = (result & ~0xFF) != 0;
        _n = (result & 0x80) != 0;
        _v = (~unadjusted & result & 0x80) != 0;
        _z = _z && (result & 0xFF) == 0;
        return (uint)result & 0xFF;
    }

    /// <summary>SBCD and NBCD: decimal subtract with extend, including the 68000's undocumented V and N.</summary>
    private uint SubtractDecimal(uint source, uint destination)
    {
        var extend = _x ? 1 : 0;
        var unadjusted = (int)destination - (int)source - extend;
        var top = (int)(destination & 0xF0) - (int)(source & 0xF0) - (0x60 & (unadjusted >> 4));
        var result = (int)(destination & 0xF) - (int)(source & 0xF) - extend;
        var lowAdjustment = 0x06 & (result >> 4);
        result += top - lowAdjustment;
        _c = _x = ((unadjusted - lowAdjustment) & 0x300) != 0;
        _n = (result & 0x80) != 0;
        _v = (unadjusted & ~result & 0x80) != 0;
        _z = _z && (result & 0xFF) == 0;
        return (uint)result & 0xFF;
    }
}
