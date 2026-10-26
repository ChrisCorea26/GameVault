namespace GameVault.Core.Cpu;

/// <summary>
/// The WDC 65C02's opcodes (Phase 12: the enhanced Apple IIe). Unused opcodes are no-ops of the documented
/// lengths and timings. Reference: WDC W65C02S datasheet; "65C02 opcodes" tables on 6502.org.
/// </summary>
public static class OpcodeTable65C02
{
    // Modes: imp acc imm zp zpx zpy abs abx aby ind izx izy rel zpi iax zpr one.
    private static readonly string[] Rows =
    [
        "BRK:imp ORA:izx NOP:imm NOP:one TSB:zp  ORA:zp  ASL:zp  RMB:zp  PHP:imp ORA:imm ASL:acc NOP:one TSB:abs ORA:abs ASL:abs BBR:zpr",
        "BPL:rel ORA:izy ORA:zpi NOP:one TRB:zp  ORA:zpx ASL:zpx RMB:zp  CLC:imp ORA:aby INC:acc NOP:one TRB:abs ORA:abx ASL:abx BBR:zpr",
        "JSR:abs AND:izx NOP:imm NOP:one BIT:zp  AND:zp  ROL:zp  RMB:zp  PLP:imp AND:imm ROL:acc NOP:one BIT:abs AND:abs ROL:abs BBR:zpr",
        "BMI:rel AND:izy AND:zpi NOP:one BIT:zpx AND:zpx ROL:zpx RMB:zp  SEC:imp AND:aby DEC:acc NOP:one BIT:abx AND:abx ROL:abx BBR:zpr",
        "RTI:imp EOR:izx NOP:imm NOP:one NOP:zp  EOR:zp  LSR:zp  RMB:zp  PHA:imp EOR:imm LSR:acc NOP:one JMP:abs EOR:abs LSR:abs BBR:zpr",
        "BVC:rel EOR:izy EOR:zpi NOP:one NOP:zpx EOR:zpx LSR:zpx RMB:zp  CLI:imp EOR:aby PHY:imp NOP:one NOP:iax EOR:abx LSR:abx BBR:zpr",
        "RTS:imp ADC:izx NOP:imm NOP:one STZ:zp  ADC:zp  ROR:zp  RMB:zp  PLA:imp ADC:imm ROR:acc NOP:one JMP:ind ADC:abs ROR:abs BBR:zpr",
        "BVS:rel ADC:izy ADC:zpi NOP:one STZ:zpx ADC:zpx ROR:zpx RMB:zp  SEI:imp ADC:aby PLY:imp NOP:one JMP:iax ADC:abx ROR:abx BBR:zpr",
        "BRA:rel STA:izx NOP:imm NOP:one STY:zp  STA:zp  STX:zp  SMB:zp  DEY:imp BIT:imm TXA:imp NOP:one STY:abs STA:abs STX:abs BBS:zpr",
        "BCC:rel STA:izy STA:zpi NOP:one STY:zpx STA:zpx STX:zpy SMB:zp  TYA:imp STA:aby TXS:imp NOP:one STZ:abs STA:abx STZ:abx BBS:zpr",
        "LDY:imm LDA:izx LDX:imm NOP:one LDY:zp  LDA:zp  LDX:zp  SMB:zp  TAY:imp LDA:imm TAX:imp NOP:one LDY:abs LDA:abs LDX:abs BBS:zpr",
        "BCS:rel LDA:izy LDA:zpi NOP:one LDY:zpx LDA:zpx LDX:zpy SMB:zp  CLV:imp LDA:aby TSX:imp NOP:one LDY:abx LDA:abx LDX:aby BBS:zpr",
        "CPY:imm CMP:izx NOP:imm NOP:one CPY:zp  CMP:zp  DEC:zp  SMB:zp  INY:imp CMP:imm DEX:imp WAI:imp CPY:abs CMP:abs DEC:abs BBS:zpr",
        "BNE:rel CMP:izy CMP:zpi NOP:one NOP:zpx CMP:zpx DEC:zpx SMB:zp  CLD:imp CMP:aby PHX:imp STP:imp NOP:abs CMP:abx DEC:abx BBS:zpr",
        "CPX:imm SBC:izx NOP:imm NOP:one CPX:zp  SBC:zp  INC:zp  SMB:zp  INX:imp SBC:imm NOP:imp NOP:one CPX:abs SBC:abs INC:abs BBS:zpr",
        "BEQ:rel SBC:izy SBC:zpi NOP:one NOP:zpx SBC:zpx INC:zpx SMB:zp  SED:imp SBC:aby PLX:imp NOP:one NOP:abs SBC:abx INC:abx BBS:zpr",
    ];

    private static readonly Instruction[] Instructions = new Instruction[256];
    private static readonly AddressingMode[] Modes = new AddressingMode[256];

    static OpcodeTable65C02()
    {
        for (var row = 0; row < 16; row++)
        {
            var cells = Rows[row].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var col = 0; col < 16; col++)
            {
                var parts = cells[col].Split(':');
                var opcode = (row << 4) | col;
                Instructions[opcode] = Enum.Parse<Instruction>(parts[0]);
                Modes[opcode] = parts[1] switch
                {
                    "imp" => AddressingMode.Implied,
                    "acc" => AddressingMode.Accumulator,
                    "imm" => AddressingMode.Immediate,
                    "zp" => AddressingMode.ZeroPage,
                    "zpx" => AddressingMode.ZeroPageX,
                    "zpy" => AddressingMode.ZeroPageY,
                    "abs" => AddressingMode.Absolute,
                    "abx" => AddressingMode.AbsoluteX,
                    "aby" => AddressingMode.AbsoluteY,
                    "ind" => AddressingMode.Indirect,
                    "izx" => AddressingMode.IndirectX,
                    "izy" => AddressingMode.IndirectY,
                    "rel" => AddressingMode.Relative,
                    "zpi" => AddressingMode.ZeroPageIndirect,
                    "iax" => AddressingMode.AbsoluteIndexedIndirect,
                    "zpr" => AddressingMode.ZeroPageRelative,
                    "one" => AddressingMode.SingleCycle,
                    _ => throw new InvalidOperationException($"Unknown addressing mode '{parts[1]}'."),
                };
            }
        }
    }

    public static Instruction GetInstruction(byte opcode) => Instructions[opcode];

    public static AddressingMode GetMode(byte opcode) => Modes[opcode];
}
