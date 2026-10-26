namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// COP2: VU0 in macro mode (ps2tek: "EE Instruction Decoding", COP2). The transfers move whole VF registers (QMFC2,
/// QMTC2) or the control registers (CFC2, CTC2); with their interlock bit (bit 0) they first wait for a running VU0
/// micro program to end. BC2F/BC2T test whether VU0 is running. Instructions with the CO bit are VU operations,
/// which VU0 executes at once — after any micro program it is running, as the EE stalls for it.
/// </summary>
public sealed partial class R5900Cpu
{
    private bool Cop2Usable()
    {
        if ((_status & StatusCu2) != 0)
        {
            return true;
        }

        RaiseException(R5900Exception.CoprocessorUnusable, 2);
        return false;
    }

    private void ExecuteCop2(uint op, int rs, int rt)
    {
        if (!Cop2Usable())
        {
            return;
        }

        var rd = (int)(op >> 11) & 31;
        var cop2 = _cop2;
        switch (rs)
        {
            case 0x01: // QMFC2
                WaitIfInterlocked(op);
                if (cop2 is not null)
                {
                    var value = cop2.ReadVf(rd);
                    Gpr(rt) = (ulong)value;
                    GprHigh(rt) = (ulong)(value >> 64);
                }

                break;
            case 0x02: // CFC2
                WaitIfInterlocked(op);
                Gpr(rt) = SignExtend32(cop2?.ReadControl(rd) ?? 0);
                break;
            case 0x05: // QMTC2
                WaitIfInterlocked(op);
                cop2?.WriteVf(rd, new UInt128(GprHigh(rt), Gpr(rt)));
                break;
            case 0x06: // CTC2
                WaitIfInterlocked(op);
                cop2?.WriteControl(rd, (uint)Gpr(rt));
                break;
            case 0x08: // BC2F, BC2T, BC2FL, BC2TL
            {
                var running = cop2?.MicroRunning ?? false;
                switch (rt & 3)
                {
                    case 0:
                        Branch(!running, BranchTarget(op));
                        break;
                    case 1:
                        Branch(running, BranchTarget(op));
                        break;
                    case 2:
                        BranchLikely(!running, BranchTarget(op));
                        break;
                    default:
                        BranchLikely(running, BranchTarget(op));
                        break;
                }

                break;
            }

            case >= 0x10: // CO: a VU0 macro instruction
                if (cop2 is not null)
                {
                    if (cop2.MicroRunning)
                    {
                        cop2.WaitForMicro();
                    }

                    cop2.ExecuteMacro(op);
                }

                break;
            default:
                RaiseException(R5900Exception.ReservedInstruction);
                break;
        }
    }

    private void WaitIfInterlocked(uint op)
    {
        if ((op & 1) != 0 && _cop2 is { MicroRunning: true } cop2)
        {
            cop2.WaitForMicro();
        }
    }
}
