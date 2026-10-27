using GameVault.Core.State;

namespace GameVault.Core.Snes;

/// <summary>Where the DSP's two ports sit on the CPU's bus.</summary>
/// <param name="BankMask">Bank bits compared (after ignoring bit 7, the FastROM mirror).</param>
/// <param name="Bank">The banks' value under <paramref name="BankMask"/>.</param>
/// <param name="OffsetLow">The window's first offset in the bank.</param>
/// <param name="OffsetHigh">The window's last offset.</param>
/// <param name="StatusBit">The address bit that selects the status register (set) or the data register (clear).</param>
public readonly record struct NecDspMap(int BankMask, int Bank, int OffsetLow, int OffsetHigh, int StatusBit)
{
    /// <summary>LoROM boards up to 1 MB: banks $30–$3F (and $B0–$BF), $8000–$FFFF; $C000 and up is the status register.</summary>
    public static NecDspMap LoRom1Mb { get; } = new(0x70, 0x30, 0x8000, 0xFFFF, 0x4000);

    /// <summary>LoROM boards of 2 MB: banks $60–$6F (and $E0–$EF), $0000–$7FFF; $4000 and up is the status register.</summary>
    public static NecDspMap LoRom2Mb { get; } = new(0x70, 0x60, 0x0000, 0x7FFF, 0x4000);

    /// <summary>HiROM boards: banks $00–$1F (and $80–$9F), $6000–$7FFF; $7000 and up is the status register.</summary>
    public static NecDspMap HiRom { get; } = new(0x60, 0x00, 0x6000, 0x7FFF, 0x1000);
}

/// <summary>
/// The NEC µPD7725 signal processor of the DSP-1, DSP-2, DSP-3 and DSP-4 cartridges (outstanding-work plan §4), running
/// the chip's own program ROM, which the player supplies: 2,048 24-bit instructions and 1,024 16-bit data words (the
/// usual 8 KB dump, program first). It has 256 words of RAM, two 16-bit accumulators with their flags, a 16×16
/// multiplier that runs after every instruction, a four-level stack, and the data (DR) and status (SR) registers the
/// SNES reads and writes, one byte at a time.
/// </summary>
/// <remarks>
/// References: the µPD7725 data sheet's instruction set (OP, RT, JP and LD), as fullsnes ("SNES Cart DSP-n/ST010/ST011")
/// and the bsnes/MAME cores describe it. One instruction a cycle at 7.6 MHz.
/// </remarks>
public sealed class NecDsp : ISnesCoprocessor
{
    public const int ProgramWords = 2048;
    public const int DataWords = 1024;
    public const int DumpSize = ProgramWords * 3 + DataWords * 2;

    /// <summary>The chip's clock against the SNES master clock (21,477,272 Hz).</summary>
    private const long DspHz = 7_600_000;
    private const long MasterHz = 21_477_272;

    // SR bits.
    private const int SrRqm = 0x8000;
    private const int SrDrs = 0x1000;
    private const int SrDrc = 0x0400;
    private const int SrReadOnly = 0x907C; // RQM, DRS and the unused bits keep their value through LD SR

    private static readonly StateTag Tag = StateTag.From("UPD7");

    private readonly uint[] _program;
    private readonly ushort[] _dataRom;
    private readonly ushort[] _ram = new ushort[256];
    private readonly ushort[] _stack = new ushort[4];
    private readonly NecDspMap _map;

    private ushort _pc;
    private ushort _rp;
    private byte _dp;
    private int _sp;
    private short _k;
    private short _l;
    private ushort _m;
    private ushort _n;
    private ushort _a;
    private ushort _b;
    private Flags _flagA;
    private Flags _flagB;
    private ushort _tr;
    private ushort _trb;
    private ushort _sr;
    private ushort _dr;
    private ushort _si;
    private ushort _so;

    /// <summary>Master clocks × <see cref="DspHz"/> run so far this frame (so the ratio stays exact).</summary>
    private long _ticks;

    public NecDsp(string name, uint[] program, ushort[] dataRom, NecDspMap map)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(program.Length, ProgramWords);
        ArgumentOutOfRangeException.ThrowIfNotEqual(dataRom.Length, DataWords);
        Name = name;
        _program = program;
        _dataRom = dataRom;
        _map = map;
    }

    public string Name { get; }

    public bool Irq => false;

    /// <summary>Instructions run so far (tests and diagnostics; not part of the state).</summary>
    public long Instructions { get; private set; }

    /// <summary>
    /// Splits an 8 KB dump into its program and data ROMs. Dumps come in either byte order, so the order is chosen by
    /// which one makes the program's jumps use real branch conditions.
    /// </summary>
    /// <exception cref="ArgumentException">The dump isn't 8 KB.</exception>
    public static (uint[] Program, ushort[] Data) SplitDump(ReadOnlySpan<byte> dump)
    {
        if (dump.Length != DumpSize)
        {
            throw new ArgumentException($"A µPD7725 program ROM dump is {DumpSize} bytes.", nameof(dump));
        }

        var little = new uint[ProgramWords];
        var big = new uint[ProgramWords];
        for (var i = 0; i < ProgramWords; i++)
        {
            little[i] = (uint)(dump[i * 3] | dump[i * 3 + 1] << 8 | dump[i * 3 + 2] << 16);
            big[i] = (uint)(dump[i * 3] << 16 | dump[i * 3 + 1] << 8 | dump[i * 3 + 2]);
        }

        var useBig = JumpScore(big) > JumpScore(little);
        var data = new ushort[DataWords];
        var at = ProgramWords * 3;
        for (var i = 0; i < DataWords; i++)
        {
            data[i] = useBig
                ? (ushort)(dump[at + i * 2] << 8 | dump[at + i * 2 + 1])
                : (ushort)(dump[at + i * 2] | dump[at + i * 2 + 1] << 8);
        }

        return (useBig ? big : little, data);
    }

    /// <summary>How many JP instructions use a branch condition the chip has, minus those that don't.</summary>
    private static int JumpScore(uint[] program)
    {
        var score = 0;
        foreach (var op in program)
        {
            if (op >> 22 == 2)
            {
                score += IsBranchCondition((op >> 13) & 0x1FF) ? 1 : -1;
            }
        }

        return score;
    }

    private static bool IsBranchCondition(uint brch) =>
        brch is 0x000 or 0x100 or 0x101 or 0x140 or 0x141 || (brch is >= 0x080 and <= 0x0BE && ((brch & 1) == 0 || brch is >= 0x0B0 and <= 0x0B3));

    public void Power()
    {
        Array.Clear(_ram);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_stack);
        _pc = _rp = 0;
        _dp = 0;
        _sp = 0;
        _k = _l = 0;
        _m = _n = _a = _b = 0;
        _flagA = _flagB = default;
        _tr = _trb = _sr = _dr = _si = _so = 0;
        _ticks = 0;
    }

    // ---- The SNES side ----

    public bool Claims(uint address)
    {
        var bank = (int)(address >> 16) & 0x7F;
        var offset = (int)(address & 0xFFFF);
        return (bank & _map.BankMask) == _map.Bank && offset >= _map.OffsetLow && offset <= _map.OffsetHigh;
    }

    public byte Read(uint address, byte openBus)
    {
        if ((address & _map.StatusBit) != 0)
        {
            return (byte)(_sr >> 8);
        }

        if ((_sr & SrDrc) != 0)
        {
            // 8-bit transfers
            _sr &= unchecked((ushort)~SrRqm);
            return (byte)_dr;
        }

        if ((_sr & SrDrs) == 0)
        {
            _sr |= SrDrs;
            return (byte)_dr;
        }

        _sr &= unchecked((ushort)~(SrRqm | SrDrs));
        return (byte)(_dr >> 8);
    }

    public void Write(uint address, byte value)
    {
        if ((address & _map.StatusBit) != 0)
        {
            return; // the status register is read-only to the SNES
        }

        if ((_sr & SrDrc) != 0)
        {
            _sr &= unchecked((ushort)~SrRqm);
            _dr = (ushort)((_dr & 0xFF00) | value);
            return;
        }

        if ((_sr & SrDrs) == 0)
        {
            _sr |= SrDrs;
            _dr = (ushort)((_dr & 0xFF00) | value);
            return;
        }

        _sr &= unchecked((ushort)~(SrRqm | SrDrs));
        _dr = (ushort)((value << 8) | (_dr & 0x00FF));
    }

    public void CatchUp(long clock)
    {
        var target = clock * DspHz;
        while (_ticks < target)
        {
            Step();
            _ticks += MasterHz;
        }
    }

    public void EndFrame(long frameClocks) => _ticks -= frameClocks * DspHz;

    // ---- The DSP ----

    /// <summary>Runs one instruction, then the multiplier.</summary>
    public void Step()
    {
        var op = _program[_pc];
        _pc = (ushort)((_pc + 1) & (ProgramWords - 1));
        switch (op >> 22)
        {
            case 0:
                ExecuteOp(op);
                break;
            case 1: // RT: OP, then return
                ExecuteOp(op);
                _pc = Pop();
                break;
            case 2:
                ExecuteJump(op);
                break;
            default:
                Load((ushort)(op >> 6), (int)(op & 15));
                break;
        }

        // The multiplier works on K and L every cycle: M is the sign and top 15 bits, N the low 15 bits and a zero.
        var product = _k * _l;
        _m = (ushort)(product >> 15);
        _n = (ushort)(product << 1);
        Instructions++;
    }

    private void ExecuteOp(uint op)
    {
        var pselect = (int)(op >> 20) & 3;
        var alu = (int)(op >> 16) & 15;
        var asl = (int)(op >> 15) & 1;
        var dpl = (int)(op >> 13) & 3;
        var dphm = (int)(op >> 9) & 15;
        var rpdcr = (op >> 8) & 1;
        var src = (int)(op >> 4) & 15;
        var dst = (int)op & 15;

        ushort idb = src switch
        {
            0 => _trb,
            1 => _a,
            2 => _b,
            3 => _tr,
            4 => _dp,
            5 => _rp,
            6 => _dataRom[_rp & (DataWords - 1)],
            7 => (ushort)(0x8000 - (_flagA.S1 ? 1 : 0)),
            8 => ReadDrForDsp(),
            9 => _dr,
            10 => _sr,
            11 or 12 => _si,
            13 => (ushort)_k,
            14 => (ushort)_l,
            _ => _ram[_dp],
        };

        if (alu != 0)
        {
            ushort p = pselect switch
            {
                0 => _ram[_dp],
                1 => idb,
                2 => _m,
                _ => _n,
            };

            ushort q;
            Flags flag;
            bool carry;
            if (asl == 0)
            {
                q = _a;
                flag = _flagA;
                carry = _flagB.C;
            }
            else
            {
                q = _b;
                flag = _flagB;
                carry = _flagA.C;
            }

            var c = carry ? 1 : 0;
            int r;
            switch (alu)
            {
                case 1: r = q | p; break;
                case 2: r = q & p; break;
                case 3: r = q ^ p; break;
                case 4: r = q - p; break;
                case 5: r = q + p; break;
                case 6: r = q - p - c; break;
                case 7: r = q + p + c; break;
                case 8: r = q - 1; p = 1; break;
                case 9: r = q + 1; p = 1; break;
                case 10: r = ~q; break;
                case 11: r = (q >> 1) | (q & 0x8000); break; // SHR1 keeps the sign
                case 12: r = (q << 1) | c; break; // SHL1 takes the other accumulator's carry
                case 13: r = (q << 2) | 3; break;
                case 14: r = (q << 4) | 15; break;
                default: r = (q << 8) | (q >> 8); break; // XCHG
            }

            var result = (ushort)r;
            flag.S0 = (result & 0x8000) != 0;
            flag.Z = result == 0;
            switch (alu)
            {
                case 4 or 5 or 6 or 7 or 8 or 9:
                    if ((alu & 1) != 0)
                    {
                        flag.Ov0 = ((q ^ result) & ~(q ^ p) & 0x8000) != 0;
                        flag.C = result < q;
                    }
                    else
                    {
                        flag.Ov0 = ((q ^ result) & (q ^ p) & 0x8000) != 0;
                        flag.C = result > q;
                    }

                    if (flag.Ov0)
                    {
                        // OV1 counts overflows (odd or even); S1 keeps the true sign across one overflow.
                        flag.S1 = flag.Ov1 ^ ((result & 0x8000) == 0);
                        flag.Ov1 = !flag.Ov1;
                    }

                    break;
                case 11:
                    flag.C = (q & 1) != 0;
                    flag.Ov0 = flag.Ov1 = false;
                    break;
                case 12:
                    flag.C = (q >> 15) != 0;
                    flag.Ov0 = flag.Ov1 = false;
                    break;
                default:
                    flag.C = false;
                    flag.Ov0 = flag.Ov1 = false;
                    break;
            }

            if (asl == 0)
            {
                _a = result;
                _flagA = flag;
            }
            else
            {
                _b = result;
                _flagB = flag;
            }
        }

        Load(idb, dst);

        switch (dpl)
        {
            case 1: _dp = (byte)((_dp & 0xF0) | ((_dp + 1) & 0x0F)); break;
            case 2: _dp = (byte)((_dp & 0xF0) | ((_dp - 1) & 0x0F)); break;
            case 3: _dp &= 0xF0; break;
        }

        _dp ^= (byte)(dphm << 4);
        if (rpdcr != 0)
        {
            _rp = (ushort)((_rp - 1) & (DataWords - 1));
        }
    }

    private ushort ReadDrForDsp()
    {
        _sr |= SrRqm;
        return _dr;
    }

    private void ExecuteJump(uint op)
    {
        var brch = (op >> 13) & 0x1FF;
        var target = (ushort)((op >> 2) & (ProgramWords - 1));
        bool take;
        switch (brch)
        {
            case 0x000: // JMPSO
                _pc = (ushort)(_so & (ProgramWords - 1));
                return;
            case 0x100 or 0x101: // LJMP, HJMP (one bank on the µPD7725)
                _pc = target;
                return;
            case 0x140 or 0x141: // LCALL, HCALL
                Push(_pc);
                _pc = target;
                return;
            case 0x080: take = !_flagA.C; break;
            case 0x082: take = _flagA.C; break;
            case 0x084: take = !_flagB.C; break;
            case 0x086: take = _flagB.C; break;
            case 0x088: take = !_flagA.Z; break;
            case 0x08A: take = _flagA.Z; break;
            case 0x08C: take = !_flagB.Z; break;
            case 0x08E: take = _flagB.Z; break;
            case 0x090: take = !_flagA.Ov0; break;
            case 0x092: take = _flagA.Ov0; break;
            case 0x094: take = !_flagB.Ov0; break;
            case 0x096: take = _flagB.Ov0; break;
            case 0x098: take = !_flagA.Ov1; break;
            case 0x09A: take = _flagA.Ov1; break;
            case 0x09C: take = !_flagB.Ov1; break;
            case 0x09E: take = _flagB.Ov1; break;
            case 0x0A0: take = !_flagA.S0; break;
            case 0x0A2: take = _flagA.S0; break;
            case 0x0A4: take = !_flagB.S0; break;
            case 0x0A6: take = _flagB.S0; break;
            case 0x0A8: take = !_flagA.S1; break;
            case 0x0AA: take = _flagA.S1; break;
            case 0x0AC: take = !_flagB.S1; break;
            case 0x0AE: take = _flagB.S1; break;
            case 0x0B0: take = (_dp & 0x0F) == 0x00; break;
            case 0x0B1: take = (_dp & 0x0F) != 0x00; break;
            case 0x0B2: take = (_dp & 0x0F) == 0x0F; break;
            case 0x0B3: take = (_dp & 0x0F) != 0x0F; break;
            case 0x0B4: take = true; break;  // JNSIAK: the serial port is never busy
            case 0x0B6: take = false; break; // JSIAK
            case 0x0B8: take = true; break;  // JNSOAK
            case 0x0BA: take = false; break; // JSOAK
            case 0x0BC: take = (_sr & SrRqm) == 0; break;
            case 0x0BE: take = (_sr & SrRqm) != 0; break;
            default: return;
        }

        if (take)
        {
            _pc = target;
        }
    }

    /// <summary>LD (and an OP's move): <paramref name="value"/> into the register <paramref name="dst"/> selects.</summary>
    private void Load(ushort value, int dst)
    {
        switch (dst)
        {
            case 1: _a = value; break;
            case 2: _b = value; break;
            case 3: _tr = value; break;
            case 4: _dp = (byte)value; break;
            case 5: _rp = (ushort)(value & (DataWords - 1)); break;
            case 6:
                _dr = value;
                _sr |= SrRqm;
                break;
            case 7: _sr = (ushort)((_sr & SrReadOnly) | (value & ~SrReadOnly)); break;
            case 8: _so = BitReverse(value); break; // SO, LSB first
            case 9: _so = value; break;
            case 10: _k = (short)value; break;
            case 11:
                _k = (short)value;
                _l = (short)_dataRom[_rp & (DataWords - 1)];
                break;
            case 12:
                _l = (short)value;
                _k = (short)_ram[(_dp | 0x40) & 0xFF];
                break;
            case 13: _l = (short)value; break;
            case 14: _trb = value; break;
            case 15: _ram[_dp] = value; break;
        }
    }

    private static ushort BitReverse(ushort value)
    {
        var result = 0;
        for (var i = 0; i < 16; i++)
        {
            result |= ((value >> i) & 1) << (15 - i);
        }

        return (ushort)result;
    }

    private void Push(ushort value)
    {
        _stack[_sp] = value;
        _sp = (_sp + 1) & 3;
    }

    private ushort Pop()
    {
        _sp = (_sp - 1) & 3;
        return _stack[_sp];
    }

    // ---- Tests ----

    internal ushort A => _a;

    internal ushort B => _b;

    internal ushort Dr => _dr;

    internal ushort Sr => _sr;

    internal ushort Pc => _pc;

    internal ushort Ram(int index) => _ram[index & 0xFF];

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        foreach (var word in _ram)
        {
            writer.WriteUInt16(word);
        }

        foreach (var word in _stack)
        {
            writer.WriteUInt16(word);
        }

        writer.WriteUInt16(_pc);
        writer.WriteUInt16(_rp);
        writer.WriteByte(_dp);
        writer.WriteInt32(_sp);
        writer.WriteUInt16((ushort)_k);
        writer.WriteUInt16((ushort)_l);
        writer.WriteUInt16(_m);
        writer.WriteUInt16(_n);
        writer.WriteUInt16(_a);
        writer.WriteUInt16(_b);
        writer.WriteByte(_flagA.Pack());
        writer.WriteByte(_flagB.Pack());
        writer.WriteUInt16(_tr);
        writer.WriteUInt16(_trb);
        writer.WriteUInt16(_sr);
        writer.WriteUInt16(_dr);
        writer.WriteUInt16(_si);
        writer.WriteUInt16(_so);
        writer.WriteInt64(_ticks);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < _ram.Length; i++)
        {
            _ram[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _stack.Length; i++)
        {
            _stack[i] = reader.ReadUInt16();
        }

        _pc = (ushort)(reader.ReadUInt16() & (ProgramWords - 1));
        _rp = (ushort)(reader.ReadUInt16() & (DataWords - 1));
        _dp = reader.ReadByte();
        _sp = reader.ReadInt32() & 3;
        _k = (short)reader.ReadUInt16();
        _l = (short)reader.ReadUInt16();
        _m = reader.ReadUInt16();
        _n = reader.ReadUInt16();
        _a = reader.ReadUInt16();
        _b = reader.ReadUInt16();
        _flagA = Flags.Unpack(reader.ReadByte());
        _flagB = Flags.Unpack(reader.ReadByte());
        _tr = reader.ReadUInt16();
        _trb = reader.ReadUInt16();
        _sr = reader.ReadUInt16();
        _dr = reader.ReadUInt16();
        _si = reader.ReadUInt16();
        _so = reader.ReadUInt16();
        _ticks = reader.ReadInt64();
        reader.EndChunk();
    }

    /// <summary>An accumulator's flags: overflow 0 and 1, zero, carry, sign 0 and sign 1.</summary>
    private struct Flags
    {
        public bool Ov0;
        public bool Ov1;
        public bool Z;
        public bool C;
        public bool S0;
        public bool S1;

        public readonly byte Pack() => (byte)((Ov0 ? 1 : 0) | (Ov1 ? 2 : 0) | (Z ? 4 : 0) | (C ? 8 : 0) | (S0 ? 16 : 0) | (S1 ? 32 : 0));

        public static Flags Unpack(byte b) => new()
        {
            Ov0 = (b & 1) != 0,
            Ov1 = (b & 2) != 0,
            Z = (b & 4) != 0,
            C = (b & 8) != 0,
            S0 = (b & 16) != 0,
            S1 = (b & 32) != 0,
        };
    }
}
