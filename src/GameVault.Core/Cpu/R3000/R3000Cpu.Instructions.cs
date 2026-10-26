namespace GameVault.Core.Cpu.R3000;

public sealed partial class R3000Cpu
{
    private void Execute(uint instruction)
    {
        var rs = (int)(instruction >> 21) & 31;
        var rt = (int)(instruction >> 16) & 31;
        var imm = instruction & 0xFFFF;
        var simm = (uint)(short)imm;

        switch (instruction >> 26)
        {
            case 0x00:
                ExecuteSpecial(instruction, rs, rt);
                break;
            case 0x01:
                BranchOnZero(instruction, rs, rt, simm);
                break;
            case 0x02: // J
                Branch(true, (_pc & 0xF0000000) | ((instruction & 0x03FFFFFF) << 2));
                break;
            case 0x03: // JAL
                Set(31, _nextPc);
                Branch(true, (_pc & 0xF0000000) | ((instruction & 0x03FFFFFF) << 2));
                break;
            case 0x04: // BEQ
                Branch(_gpr[rs] == _gpr[rt], _pc + (simm << 2));
                break;
            case 0x05: // BNE
                Branch(_gpr[rs] != _gpr[rt], _pc + (simm << 2));
                break;
            case 0x06: // BLEZ
                Branch((int)_gpr[rs] <= 0, _pc + (simm << 2));
                break;
            case 0x07: // BGTZ
                Branch((int)_gpr[rs] > 0, _pc + (simm << 2));
                break;
            case 0x08: // ADDI
            {
                var a = (int)_gpr[rs];
                var result = a + (int)simm;
                if (((a ^ result) & ((int)simm ^ result)) < 0)
                {
                    Exception(R3000Exception.Overflow);
                    break;
                }

                Set(rt, (uint)result);
                break;
            }

            case 0x09: // ADDIU
                Set(rt, _gpr[rs] + simm);
                break;
            case 0x0A: // SLTI
                Set(rt, (int)_gpr[rs] < (int)simm ? 1u : 0);
                break;
            case 0x0B: // SLTIU
                Set(rt, _gpr[rs] < simm ? 1u : 0);
                break;
            case 0x0C: // ANDI
                Set(rt, _gpr[rs] & imm);
                break;
            case 0x0D: // ORI
                Set(rt, _gpr[rs] | imm);
                break;
            case 0x0E: // XORI
                Set(rt, _gpr[rs] ^ imm);
                break;
            case 0x0F: // LUI
                Set(rt, imm << 16);
                break;
            case 0x10:
                ExecuteCop0(instruction, rs, rt);
                break;
            case 0x11:
            case 0x13:
                // COP1 and COP3 don't exist: with their usable bit set, their instructions do nothing.
                CheckCoprocessor((int)(instruction >> 26) & 3);
                break;
            case 0x12:
                ExecuteCop2(instruction, rs, rt);
                break;
            case 0x20: // LB
            {
                var address = _gpr[rs] + simm;
                SetDelayed(rt, (uint)(sbyte)_bus.Read8(Physical(address)));
                break;
            }

            case 0x21: // LH
            {
                var address = _gpr[rs] + simm;
                if ((address & 1) != 0)
                {
                    AddressError(R3000Exception.AddressErrorLoad, address);
                    break;
                }

                SetDelayed(rt, (uint)(short)_bus.Read16(Physical(address)));
                break;
            }

            case 0x22: // LWL
            {
                var address = _gpr[rs] + simm;
                var word = Load32(address & ~3u);
                var current = CurrentForMerge(rt);
                var value = (address & 3) switch
                {
                    0 => (current & 0x00FFFFFF) | (word << 24),
                    1 => (current & 0x0000FFFF) | (word << 16),
                    2 => (current & 0x000000FF) | (word << 8),
                    _ => word,
                };
                SetDelayed(rt, value);
                break;
            }

            case 0x23: // LW
            {
                var address = _gpr[rs] + simm;
                if ((address & 3) != 0)
                {
                    AddressError(R3000Exception.AddressErrorLoad, address);
                    break;
                }

                SetDelayed(rt, Load32(address));
                break;
            }

            case 0x24: // LBU
                SetDelayed(rt, _bus.Read8(Physical(_gpr[rs] + simm)));
                break;
            case 0x25: // LHU
            {
                var address = _gpr[rs] + simm;
                if ((address & 1) != 0)
                {
                    AddressError(R3000Exception.AddressErrorLoad, address);
                    break;
                }

                SetDelayed(rt, _bus.Read16(Physical(address)));
                break;
            }

            case 0x26: // LWR
            {
                var address = _gpr[rs] + simm;
                var word = Load32(address & ~3u);
                var current = CurrentForMerge(rt);
                var value = (address & 3) switch
                {
                    0 => word,
                    1 => (current & 0xFF000000) | (word >> 8),
                    2 => (current & 0xFFFF0000) | (word >> 16),
                    _ => (current & 0xFFFFFF00) | (word >> 24),
                };
                SetDelayed(rt, value);
                break;
            }

            case 0x28: // SB
                Store8(_gpr[rs] + simm, _gpr[rt]);
                break;
            case 0x29: // SH
            {
                var address = _gpr[rs] + simm;
                if ((address & 1) != 0)
                {
                    AddressError(R3000Exception.AddressErrorStore, address);
                    break;
                }

                Store16(address, _gpr[rt]);
                break;
            }

            case 0x2A: // SWL
            {
                var address = _gpr[rs] + simm;
                var aligned = address & ~3u;
                var memory = Load32(aligned);
                var value = _gpr[rt];
                Store32(aligned, (address & 3) switch
                {
                    0 => (memory & 0xFFFFFF00) | (value >> 24),
                    1 => (memory & 0xFFFF0000) | (value >> 16),
                    2 => (memory & 0xFF000000) | (value >> 8),
                    _ => value,
                });
                break;
            }

            case 0x2B: // SW
            {
                var address = _gpr[rs] + simm;
                if ((address & 3) != 0)
                {
                    AddressError(R3000Exception.AddressErrorStore, address);
                    break;
                }

                Store32(address, _gpr[rt]);
                break;
            }

            case 0x2E: // SWR
            {
                var address = _gpr[rs] + simm;
                var aligned = address & ~3u;
                var memory = Load32(aligned);
                var value = _gpr[rt];
                Store32(aligned, (address & 3) switch
                {
                    0 => value,
                    1 => (memory & 0x000000FF) | (value << 8),
                    2 => (memory & 0x0000FFFF) | (value << 16),
                    _ => (memory & 0x00FFFFFF) | (value << 24),
                });
                break;
            }

            case 0x30: // LWC0
            case 0x31: // LWC1
            case 0x33: // LWC3
            case 0x38: // SWC0
            case 0x39: // SWC1
            case 0x3B: // SWC3
                // No such coprocessor registers: with the usable bit set, nothing happens (ps1-tests cpu/cop).
                CheckCoprocessor((int)(instruction >> 26) & 3);
                break;
            case 0x32: // LWC2
            {
                if (!CheckCoprocessor(2))
                {
                    break;
                }

                var address = _gpr[rs] + simm;
                if ((address & 3) != 0)
                {
                    AddressError(R3000Exception.AddressErrorLoad, address);
                    break;
                }

                Gte.WriteData(rt, Load32(address));
                break;
            }

            case 0x3A: // SWC2
            {
                if (!CheckCoprocessor(2))
                {
                    break;
                }

                var address = _gpr[rs] + simm;
                if ((address & 3) != 0)
                {
                    AddressError(R3000Exception.AddressErrorStore, address);
                    break;
                }

                Store32(address, Gte.ReadData(rt));
                break;
            }

            default:
                Exception(R3000Exception.ReservedInstruction);
                break;
        }
    }

    private void ExecuteSpecial(uint instruction, int rs, int rt)
    {
        var rd = (int)(instruction >> 11) & 31;
        var shamt = (int)(instruction >> 6) & 31;

        switch (instruction & 0x3F)
        {
            case 0x00: // SLL
                Set(rd, _gpr[rt] << shamt);
                break;
            case 0x02: // SRL
                Set(rd, _gpr[rt] >> shamt);
                break;
            case 0x03: // SRA
                Set(rd, (uint)((int)_gpr[rt] >> shamt));
                break;
            case 0x04: // SLLV
                Set(rd, _gpr[rt] << (int)(_gpr[rs] & 31));
                break;
            case 0x06: // SRLV
                Set(rd, _gpr[rt] >> (int)(_gpr[rs] & 31));
                break;
            case 0x07: // SRAV
                Set(rd, (uint)((int)_gpr[rt] >> (int)(_gpr[rs] & 31)));
                break;
            case 0x08: // JR
                Branch(true, _gpr[rs]);
                break;
            case 0x09: // JALR
            {
                var target = _gpr[rs];
                Set(rd, _nextPc);
                Branch(true, target);
                break;
            }

            case 0x0C:
                Exception(R3000Exception.Syscall);
                break;
            case 0x0D:
                Exception(R3000Exception.Breakpoint);
                break;
            case 0x10: // MFHI
                WaitForMultiplier();
                Set(rd, Hi);
                break;
            case 0x11: // MTHI
                Hi = _gpr[rs];
                break;
            case 0x12: // MFLO
                WaitForMultiplier();
                Set(rd, Lo);
                break;
            case 0x13: // MTLO
                Lo = _gpr[rs];
                break;
            case 0x18: // MULT
            {
                var a = _gpr[rs];
                var product = (long)(int)a * (int)_gpr[rt];
                Hi = (uint)(product >> 32);
                Lo = (uint)product;

                // The multiplier stops early for small operands (psx-spx: "CPU Arithmetic"): 6, 9 or 13 cycles.
                var magnitude = (int)a < 0 ? ~a : a;
                _hiLoReady = Cycles + (magnitude < 0x800 ? 6 : magnitude < 0x100000 ? 9 : 13);
                break;
            }

            case 0x19: // MULTU
            {
                var a = _gpr[rs];
                var product = (ulong)a * _gpr[rt];
                Hi = (uint)(product >> 32);
                Lo = (uint)product;
                _hiLoReady = Cycles + (a < 0x800 ? 6 : a < 0x100000 ? 9 : 13);
                break;
            }

            case 0x1A: // DIV
            {
                var n = (int)_gpr[rs];
                var d = (int)_gpr[rt];
                if (d == 0)
                {
                    // Division by zero gives these results rather than an exception (psx-spx: "CPU Arithmetic").
                    Hi = (uint)n;
                    Lo = n >= 0 ? 0xFFFFFFFF : 1;
                }
                else if (n == int.MinValue && d == -1)
                {
                    Hi = 0;
                    Lo = 0x80000000;
                }
                else
                {
                    Hi = (uint)(n % d);
                    Lo = (uint)(n / d);
                }

                _hiLoReady = Cycles + 36;
                break;
            }

            case 0x1B: // DIVU
            {
                var n = _gpr[rs];
                var d = _gpr[rt];
                if (d == 0)
                {
                    Hi = n;
                    Lo = 0xFFFFFFFF;
                }
                else
                {
                    Hi = n % d;
                    Lo = n / d;
                }

                _hiLoReady = Cycles + 36;
                break;
            }

            case 0x20: // ADD
            {
                var a = (int)_gpr[rs];
                var b = (int)_gpr[rt];
                var result = a + b;
                if (((a ^ result) & (b ^ result)) < 0)
                {
                    Exception(R3000Exception.Overflow);
                    break;
                }

                Set(rd, (uint)result);
                break;
            }

            case 0x21: // ADDU
                Set(rd, _gpr[rs] + _gpr[rt]);
                break;
            case 0x22: // SUB
            {
                var a = (int)_gpr[rs];
                var b = (int)_gpr[rt];
                var result = a - b;
                if (((a ^ b) & (a ^ result)) < 0)
                {
                    Exception(R3000Exception.Overflow);
                    break;
                }

                Set(rd, (uint)result);
                break;
            }

            case 0x23: // SUBU
                Set(rd, _gpr[rs] - _gpr[rt]);
                break;
            case 0x24: // AND
                Set(rd, _gpr[rs] & _gpr[rt]);
                break;
            case 0x25: // OR
                Set(rd, _gpr[rs] | _gpr[rt]);
                break;
            case 0x26: // XOR
                Set(rd, _gpr[rs] ^ _gpr[rt]);
                break;
            case 0x27: // NOR
                Set(rd, ~(_gpr[rs] | _gpr[rt]));
                break;
            case 0x2A: // SLT
                Set(rd, (int)_gpr[rs] < (int)_gpr[rt] ? 1u : 0);
                break;
            case 0x2B: // SLTU
                Set(rd, _gpr[rs] < _gpr[rt] ? 1u : 0);
                break;
            default:
                Exception(R3000Exception.ReservedInstruction);
                break;
        }
    }

    /// <summary>
    /// BLTZ, BGEZ, BLTZAL and BGEZAL. The CPU decodes only bit 16 (greater-or-equal) and whether bits 17–20 are
    /// 1000 (link), so the other rt values act as one of these four (psx-spx: "CPU Jump Opcodes"). The link happens
    /// whether or not the branch is taken.
    /// </summary>
    private void BranchOnZero(uint instruction, int rs, int rt, uint simm)
    {
        var value = (int)_gpr[rs];
        var taken = (rt & 1) != 0 ? value >= 0 : value < 0;
        if ((rt & 0x1E) == 0x10)
        {
            Set(31, _nextPc);
        }

        Branch(taken, _pc + (simm << 2));
    }

    /// <summary>A branch or jump: the next instruction is its delay slot, then execution continues at <paramref name="target"/> if taken.</summary>
    private void Branch(bool taken, uint target)
    {
        _branch = true;
        if (taken)
        {
            _branchTaken = true;
            _nextPc = target;
        }
    }

    private void ExecuteCop0(uint instruction, int rs, int rt)
    {
        // COP0 is always usable in kernel mode; in user mode it needs its usable bit.
        if ((Sr & 2) != 0 && (Sr & SrCu0) == 0)
        {
            Exception(R3000Exception.CoprocessorUnusable, 0);
            return;
        }

        var rd = (int)(instruction >> 11) & 31;
        switch (rs)
        {
            case 0x00: // MFC0
            {
                uint value;
                switch (rd)
                {
                    case 8:
                        value = BadVaddr;
                        break;
                    case 12:
                        value = Sr;
                        break;
                    case 13:
                        InterruptPending();
                        value = Cause;
                        break;
                    case 14:
                        value = Epc;
                        break;
                    case 15:
                        value = ProcessorIdValue;
                        break;
                    case < 16:
                        value = 0; // The breakpoint registers, which aren't modelled.
                        break;
                    default:
                        Exception(R3000Exception.ReservedInstruction);
                        return;
                }

                SetDelayed(rt, value);
                break;
            }

            case 0x04: // MTC0
                switch (rd)
                {
                    case 12:
                        Sr = _gpr[rt];
                        break;
                    case 13:
                        // Only the two software interrupt bits are writable.
                        Cause = (Cause & ~0x300u) | (_gpr[rt] & 0x300);
                        break;
                }

                break;
            case 0x10 when (instruction & 0x3F) == 0x10: // RFE
                Sr = (Sr & ~0x0Fu) | ((Sr >> 2) & 0x0F);
                break;

            // Other COP0 opcodes do nothing (ps1-tests cpu/cop).
        }
    }

    private void ExecuteCop2(uint instruction, int rs, int rt)
    {
        if (!CheckCoprocessor(2))
        {
            return;
        }

        var rd = (int)(instruction >> 11) & 31;
        if ((rs & 0x10) != 0)
        {
            Gte.Execute(instruction);
            return;
        }

        switch (rs)
        {
            case 0x00: // MFC2
                SetDelayed(rt, Gte.ReadData(rd));
                break;
            case 0x02: // CFC2
                SetDelayed(rt, Gte.ReadControl(rd));
                break;
            case 0x04: // MTC2
                Gte.WriteData(rd, _gpr[rt]);
                break;
            case 0x06: // CTC2
                Gte.WriteControl(rd, _gpr[rt]);
                break;

            // Other COP2 opcodes do nothing (ps1-tests cpu/cop).
        }
    }

    /// <summary>Raises Coprocessor Unusable unless SR's usable bit for <paramref name="coprocessor"/> is set.</summary>
    private bool CheckCoprocessor(int coprocessor)
    {
        if ((Sr & (SrCu0 << coprocessor)) != 0 && (coprocessor != 2 || HasGte))
        {
            return true;
        }

        Exception(R3000Exception.CoprocessorUnusable, coprocessor);
        return false;
    }

    /// <summary>LWL and LWR merge with a load still landing in the same register, not the register's old value.</summary>
    private uint CurrentForMerge(int rt) => _landingReg == rt && rt != 0 ? _landingValue : _gpr[rt];

    private void AddressError(R3000Exception code, uint address)
    {
        BadVaddr = address;
        Exception(code);
    }

    /// <summary>MFHI and MFLO stall until the multiplier or divider finishes.</summary>
    private void WaitForMultiplier()
    {
        if (Cycles < _hiLoReady)
        {
            Cycles = _hiLoReady;
        }
    }
}
