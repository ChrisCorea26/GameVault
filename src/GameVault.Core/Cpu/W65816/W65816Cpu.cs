using GameVault.Core.State;

namespace GameVault.Core.Cpu.W65816;

/// <summary>The 65C816's bus: 24-bit addresses, one call per bus cycle, and <see cref="Idle"/> for internal cycles.</summary>
public interface IW65816Bus
{
    byte Read(uint address);

    void Write(uint address, byte value);

    /// <summary>An internal cycle with no memory access (the SNES runs it at the fast speed).</summary>
    void Idle();
}

/// <summary>
/// WDC 65C816 (SPEC §7.11, Phase 14): native and emulation modes, 8/16-bit accumulator and index registers, every
/// opcode and addressing mode (direct page, stack relative, long, block moves), decimal mode in both widths, and
/// NMI, IRQ, BRK, COP, WAI and STP. One <see cref="Step"/> runs an instruction; every bus cycle goes through the bus,
/// so the console can charge each access its own speed.
/// </summary>
/// <remarks>
/// References: WDC W65C816S datasheet; "Programming the 65816" (Eyes and Lichty); the SingleStepTests 65816 suite,
/// whose per-cycle listings settle the emulation-mode wrapping and the extra cycles.
/// </remarks>
public sealed partial class W65816Cpu : IStateful
{
    public const byte FlagC = 0x01, FlagZ = 0x02, FlagI = 0x04, FlagD = 0x08, FlagX = 0x10, FlagM = 0x20, FlagV = 0x40, FlagN = 0x80;

    private static readonly StateTag Tag = StateTag.From("W816");

    private readonly IW65816Bus _bus;

    public W65816Cpu(IW65816Bus bus) => _bus = bus;

    public ushort A { get; set; }

    public ushort X { get; set; }

    public ushort Y { get; set; }

    public ushort S { get; set; }

    public ushort D { get; set; }

    public byte Dbr { get; set; }

    public byte Pbr { get; set; }

    public ushort Pc { get; set; }

    public byte P { get; set; }

    /// <summary>Emulation mode (the 6502-compatible mode the CPU starts in).</summary>
    public bool E { get; set; }

    public bool Nmi { get; set; }

    /// <summary>The IRQ line, held by the devices.</summary>
    public bool Irq { get; set; }

    /// <summary>After WAI, until an interrupt.</summary>
    public bool Waiting { get; internal set; }

    /// <summary>After STP, until reset.</summary>
    public bool Stopped { get; internal set; }

    private bool M8 => (P & FlagM) != 0;

    private bool X8 => (P & FlagX) != 0;

    public void Reset()
    {
        E = true;
        P = (byte)((P | FlagM | FlagX | FlagI) & ~FlagD);
        X &= 0xFF;
        Y &= 0xFF;
        S = (ushort)(0x0100 | (S & 0xFF));
        D = 0;
        Dbr = 0;
        Pbr = 0;
        Waiting = false;
        Stopped = false;
        Pc = (ushort)(Read(0xFFFC) | Read(0xFFFD) << 8);
    }

    /// <summary>Runs one instruction, or takes a pending interrupt.</summary>
    public void Step()
    {
        if (Stopped)
        {
            _bus.Idle();
            return;
        }

        if (Nmi)
        {
            Nmi = false;
            Waiting = false;
            Interrupt(E ? 0xFFFA : 0xFFEA, software: false);
            return;
        }

        if (Irq && Waiting)
        {
            Waiting = false;
        }

        if (Waiting)
        {
            _bus.Idle();
            return;
        }

        if (Irq && (P & FlagI) == 0)
        {
            Interrupt(E ? 0xFFFE : 0xFFEE, software: false);
            return;
        }

        Execute(FetchByte());
    }

    // ---- Bus helpers ----

    private byte Read(uint address) => _bus.Read(address & 0xFFFFFF);

    private void Write(uint address, byte value) => _bus.Write(address & 0xFFFFFF, value);

    private void Idle() => _bus.Idle();

    private byte FetchByte()
    {
        var value = Read((uint)(Pbr << 16 | Pc));
        Pc++;
        return value;
    }

    private ushort FetchWord()
    {
        var low = FetchByte();
        return (ushort)(low | FetchByte() << 8);
    }

    /// <summary>Bank-0 addresses (direct page and stack) wrap within bank 0.</summary>
    private static uint Bank0(int address) => (uint)(address & 0xFFFF);

    private void SetNz8(int value)
    {
        P = (byte)(P & ~(FlagN | FlagZ) | (value & 0x80) | ((value & 0xFF) == 0 ? FlagZ : 0));
    }

    private void SetNz16(int value)
    {
        P = (byte)(P & ~(FlagN | FlagZ) | ((value >> 8) & 0x80) | ((value & 0xFFFF) == 0 ? FlagZ : 0));
    }

    private void SetFlag(byte flag, bool on) => P = (byte)(on ? P | flag : P & ~flag);

    /// <summary>After P changes: the index registers lose their high bytes when X is set; emulation keeps M and X set.</summary>
    private void FixFlags()
    {
        if (E)
        {
            P |= FlagM | FlagX;
        }

        if (X8)
        {
            X &= 0xFF;
            Y &= 0xFF;
        }
    }

    // ---- The stack ----

    private void Push(byte value)
    {
        Write(S, value);
        S = E ? (ushort)(0x0100 | ((S - 1) & 0xFF)) : (ushort)(S - 1);
    }

    private byte Pull()
    {
        S = E ? (ushort)(0x0100 | ((S + 1) & 0xFF)) : (ushort)(S + 1);
        return Read(S);
    }

    /// <summary>The 65816's own instructions (PEA, PEI, PER, PHD, PLD, JSL, RTL, …) move S across the whole bank even in emulation mode; S's high byte is fixed afterwards.</summary>
    private void PushNew(byte value)
    {
        Write(S, value);
        S--;
    }

    private byte PullNew()
    {
        S++;
        return Read(S);
    }

    private void EndNew()
    {
        if (E)
        {
            S = (ushort)(0x0100 | (S & 0xFF));
        }
    }

    private void Interrupt(int vector, bool software)
    {
        if (!software)
        {
            // Two cycles to recognize the interrupt (the opcode fetch that's thrown away and an internal cycle).
            Read((uint)(Pbr << 16 | Pc));
            Idle();
        }

        if (!E)
        {
            Push(Pbr);
        }

        Push((byte)(Pc >> 8));
        Push((byte)Pc);
        Push(E && !software ? (byte)(P & ~FlagX) : P);
        P = (byte)((P | FlagI) & ~FlagD);
        Pbr = 0;
        Pc = (ushort)(Read((uint)vector) | Read((uint)vector + 1) << 8);
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt16(A);
        writer.WriteUInt16(X);
        writer.WriteUInt16(Y);
        writer.WriteUInt16(S);
        writer.WriteUInt16(D);
        writer.WriteByte(Dbr);
        writer.WriteByte(Pbr);
        writer.WriteUInt16(Pc);
        writer.WriteByte(P);
        writer.WriteBool(E);
        writer.WriteBool(Nmi);
        writer.WriteBool(Irq);
        writer.WriteBool(Waiting);
        writer.WriteBool(Stopped);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        A = reader.ReadUInt16();
        X = reader.ReadUInt16();
        Y = reader.ReadUInt16();
        S = reader.ReadUInt16();
        D = reader.ReadUInt16();
        Dbr = reader.ReadByte();
        Pbr = reader.ReadByte();
        Pc = reader.ReadUInt16();
        P = reader.ReadByte();
        E = reader.ReadBool();
        Nmi = reader.ReadBool();
        Irq = reader.ReadBool();
        Waiting = reader.ReadBool();
        Stopped = reader.ReadBool();
        reader.EndChunk();
    }
}
