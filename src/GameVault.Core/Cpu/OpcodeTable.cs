namespace GameVault.Core.Cpu;

/// <summary>
/// Decodes all 256 opcodes into an <see cref="Instruction"/> and <see cref="AddressingMode"/>.
/// Layout follows the NESdev Wiki opcode matrix ("CPU unofficial opcodes").
/// </summary>
public static class OpcodeTable
{
    // One row per high nibble; 16 "MNEMONIC:mode" cells per row.
    // Modes: imp acc imm zp zpx zpy abs abx aby ind izx izy rel.
    private static readonly string[] Rows =
    [
        "BRK:imp ORA:izx JAM:imp SLO:izx NOP:zp  ORA:zp  ASL:zp  SLO:zp  PHP:imp ORA:imm ASL:acc ANC:imm NOP:abs ORA:abs ASL:abs SLO:abs",
        "BPL:rel ORA:izy JAM:imp SLO:izy NOP:zpx ORA:zpx ASL:zpx SLO:zpx CLC:imp ORA:aby NOP:imp SLO:aby NOP:abx ORA:abx ASL:abx SLO:abx",
        "JSR:abs AND:izx JAM:imp RLA:izx BIT:zp  AND:zp  ROL:zp  RLA:zp  PLP:imp AND:imm ROL:acc ANC:imm BIT:abs AND:abs ROL:abs RLA:abs",
        "BMI:rel AND:izy JAM:imp RLA:izy NOP:zpx AND:zpx ROL:zpx RLA:zpx SEC:imp AND:aby NOP:imp RLA:aby NOP:abx AND:abx ROL:abx RLA:abx",
        "RTI:imp EOR:izx JAM:imp SRE:izx NOP:zp  EOR:zp  LSR:zp  SRE:zp  PHA:imp EOR:imm LSR:acc ALR:imm JMP:abs EOR:abs LSR:abs SRE:abs",
        "BVC:rel EOR:izy JAM:imp SRE:izy NOP:zpx EOR:zpx LSR:zpx SRE:zpx CLI:imp EOR:aby NOP:imp SRE:aby NOP:abx EOR:abx LSR:abx SRE:abx",
        "RTS:imp ADC:izx JAM:imp RRA:izx NOP:zp  ADC:zp  ROR:zp  RRA:zp  PLA:imp ADC:imm ROR:acc ARR:imm JMP:ind ADC:abs ROR:abs RRA:abs",
        "BVS:rel ADC:izy JAM:imp RRA:izy NOP:zpx ADC:zpx ROR:zpx RRA:zpx SEI:imp ADC:aby NOP:imp RRA:aby NOP:abx ADC:abx ROR:abx RRA:abx",
        "NOP:imm STA:izx NOP:imm SAX:izx STY:zp  STA:zp  STX:zp  SAX:zp  DEY:imp NOP:imm TXA:imp ANE:imm STY:abs STA:abs STX:abs SAX:abs",
        "BCC:rel STA:izy JAM:imp SHA:izy STY:zpx STA:zpx STX:zpy SAX:zpy TYA:imp STA:aby TXS:imp TAS:aby SHY:abx STA:abx SHX:aby SHA:aby",
        "LDY:imm LDA:izx LDX:imm LAX:izx LDY:zp  LDA:zp  LDX:zp  LAX:zp  TAY:imp LDA:imm TAX:imp LXA:imm LDY:abs LDA:abs LDX:abs LAX:abs",
        "BCS:rel LDA:izy JAM:imp LAX:izy LDY:zpx LDA:zpx LDX:zpy LAX:zpy CLV:imp LDA:aby TSX:imp LAS:aby LDY:abx LDA:abx LDX:aby LAX:aby",
        "CPY:imm CMP:izx NOP:imm DCP:izx CPY:zp  CMP:zp  DEC:zp  DCP:zp  INY:imp CMP:imm DEX:imp AXS:imm CPY:abs CMP:abs DEC:abs DCP:abs",
        "BNE:rel CMP:izy JAM:imp DCP:izy NOP:zpx CMP:zpx DEC:zpx DCP:zpx CLD:imp CMP:aby NOP:imp DCP:aby NOP:abx CMP:abx DEC:abx DCP:abx",
        "CPX:imm SBC:izx NOP:imm ISB:izx CPX:zp  SBC:zp  INC:zp  ISB:zp  INX:imp SBC:imm NOP:imp SBC:imm CPX:abs SBC:abs INC:abs ISB:abs",
        "BEQ:rel SBC:izy JAM:imp ISB:izy NOP:zpx SBC:zpx INC:zpx ISB:zpx SED:imp SBC:aby NOP:imp ISB:aby NOP:abx SBC:abx INC:abx ISB:abx",
    ];

    private static readonly Instruction[] Instructions = new Instruction[256];
    private static readonly AddressingMode[] Modes = new AddressingMode[256];

    static OpcodeTable()
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
                    _ => throw new InvalidOperationException($"Unknown addressing mode '{parts[1]}'."),
                };
            }
        }
    }

    public static Instruction GetInstruction(byte opcode) => Instructions[opcode];

    public static AddressingMode GetMode(byte opcode) => Modes[opcode];

    /// <summary>True for the 151 documented opcodes.</summary>
    public static bool IsOfficial(byte opcode) =>
        Instructions[opcode] switch
        {
            Instruction.NOP => opcode == 0xEA,
            Instruction.SBC => opcode != 0xEB,
            var i => i <= Instruction.TYA,
        };

    /// <summary>Instruction length in bytes, including the opcode.</summary>
    public static int GetLength(byte opcode) => Modes[opcode] switch
    {
        AddressingMode.Implied or AddressingMode.Accumulator => 1,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.Indirect => 3,
        _ => 2,
    };
}
