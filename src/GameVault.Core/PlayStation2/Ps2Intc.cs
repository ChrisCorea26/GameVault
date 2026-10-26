using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The EE's interrupt controller (ps2tek: "EE Interrupt Controller (INTC)"): INTC_STAT at $1000F000 (a source's bit is
/// set when it raises its interrupt; writing 1 clears it) and INTC_MASK at $1000F010 (writing 1 toggles a bit).
/// <see cref="Int0"/> is the EE's INT0 line (COP0 Cause bit 10, IP2). The DMAC drives INT1 on its own.
/// </summary>
public sealed class Ps2Intc : IStateful
{
    public const uint StatAddress = 0x1000F000;
    public const uint MaskAddress = 0x1000F010;

    /// <summary>The 15 sources (<see cref="Ps2Interrupt"/>).</summary>
    public const uint ValidBits = 0x7FFF;

    private static readonly StateTag Tag = StateTag.From("EINT");

    /// <summary>INTC_STAT: the raised sources.</summary>
    public uint Stat { get; private set; }

    /// <summary>INTC_MASK: the enabled sources.</summary>
    public uint Mask { get; private set; }

    /// <summary>The EE's INT0 line: a raised source is enabled.</summary>
    public bool Int0 => (Stat & Mask) != 0;

    public void Raise(Ps2Interrupt source) => Stat |= 1u << (int)source;

    public void Reset()
    {
        Stat = 0;
        Mask = 0;
    }

    /// <summary>Reads INTC_STAT or INTC_MASK; other addresses read 0.</summary>
    public uint Read(uint address) => (address & ~0xFu) switch
    {
        StatAddress => Stat,
        MaskAddress => Mask,
        _ => 0,
    };

    public void Write(uint address, uint value)
    {
        switch (address & ~0xFu)
        {
            case StatAddress:
                Stat &= ~(value & ValidBits);
                break;
            case MaskAddress:
                Mask ^= value & ValidBits;
                break;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteUInt32(Stat);
        writer.WriteUInt32(Mask);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported EE INTC state version.");
        }

        Stat = reader.ReadUInt32() & ValidBits;
        Mask = reader.ReadUInt32() & ValidBits;
        reader.EndChunk();
    }
}
