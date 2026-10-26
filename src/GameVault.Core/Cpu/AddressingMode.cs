namespace GameVault.Core.Cpu;

public enum AddressingMode : byte
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndirectX,
    IndirectY,
    Relative,

    // 65C02
    /// <summary>(zp): a pointer in zero page, no index.</summary>
    ZeroPageIndirect,

    /// <summary>JMP (abs,X).</summary>
    AbsoluteIndexedIndirect,

    /// <summary>BBR/BBS: a zero-page operand and a branch offset.</summary>
    ZeroPageRelative,

    /// <summary>A 1-byte, 1-cycle no-op (the 65C02's unused $x3 and $xB columns).</summary>
    SingleCycle,
}
