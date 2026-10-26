namespace GameVault.Core.Nintendo64;

/// <summary>The MIPS Interface's interrupt sources, in MI_INTR / MI_MASK bit order (n64brew: "MIPS Interface").</summary>
[Flags]
public enum N64Interrupt : uint
{
    None = 0,
    Sp = 1 << 0,
    Si = 1 << 1,
    Ai = 1 << 2,
    Vi = 1 << 3,
    Pi = 1 << 4,
    Dp = 1 << 5,
}

/// <summary>
/// What the RCP's units (the RSP, the RDP) need from the rest of the console. Every memory here is big-endian, as
/// the N64 sees it: byte <c>n</c> of a span is the byte at address <c>n</c>.
/// </summary>
public interface IN64RcpHost
{
    /// <summary>RDRAM: 4 MB, or 8 MB with the Expansion Pak.</summary>
    Span<byte> Rdram { get; }

    /// <summary>The RSP's 4 KB data memory, which the RDP can read commands from (XBUS).</summary>
    Span<byte> Dmem { get; }

    /// <summary>Sets an interrupt flag in MI_INTR (the CPU sees it when its mask bit is set).</summary>
    void RaiseInterrupt(N64Interrupt source);

    /// <summary>Clears an interrupt flag in MI_INTR.</summary>
    void ClearInterrupt(N64Interrupt source);
}
