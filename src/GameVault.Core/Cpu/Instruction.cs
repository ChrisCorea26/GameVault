namespace GameVault.Core.Cpu;

/// <summary>
/// Every 6502 operation, including the unofficial ones (NESdev Wiki: "CPU unofficial opcodes").
/// Unofficial names follow nestest.log where it uses them (for example ISB rather than ISC).
/// </summary>
public enum Instruction : byte
{
    // Official
    ADC, AND, ASL, BCC, BCS, BEQ, BIT, BMI, BNE, BPL, BRK, BVC, BVS, CLC,
    CLD, CLI, CLV, CMP, CPX, CPY, DEC, DEX, DEY, EOR, INC, INX, INY, JMP,
    JSR, LDA, LDX, LDY, LSR, NOP, ORA, PHA, PHP, PLA, PLP, ROL, ROR, RTI,
    RTS, SBC, SEC, SED, SEI, STA, STX, STY, TAX, TAY, TSX, TXA, TXS, TYA,

    // Unofficial
    ALR, ANC, ANE, ARR, AXS, DCP, ISB, JAM, LAS, LAX, LXA, RLA, RRA, SAX,
    SHA, SHX, SHY, SLO, SRE, TAS,
}
