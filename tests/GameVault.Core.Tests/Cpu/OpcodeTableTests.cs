using GameVault.Core.Cpu;

namespace GameVault.Core.Tests.Cpu;

public class OpcodeTableTests
{
    [Fact]
    public void Exactly_151_opcodes_are_official()
    {
        Enumerable.Range(0, 256).Count(op => OpcodeTable.IsOfficial((byte)op)).Should().Be(151);
    }

    [Theory]
    [InlineData(0xA9, Instruction.LDA, AddressingMode.Immediate, 2)]
    [InlineData(0x6C, Instruction.JMP, AddressingMode.Indirect, 3)]
    [InlineData(0xB6, Instruction.LDX, AddressingMode.ZeroPageY, 2)]
    [InlineData(0x0A, Instruction.ASL, AddressingMode.Accumulator, 1)]
    [InlineData(0xF0, Instruction.BEQ, AddressingMode.Relative, 2)]
    [InlineData(0xEB, Instruction.SBC, AddressingMode.Immediate, 2)]
    [InlineData(0x9E, Instruction.SHX, AddressingMode.AbsoluteY, 3)]
    [InlineData(0xE3, Instruction.ISB, AddressingMode.IndirectX, 2)]
    public void Decodes_opcodes(byte opcode, Instruction instruction, AddressingMode mode, int length)
    {
        OpcodeTable.GetInstruction(opcode).Should().Be(instruction);
        OpcodeTable.GetMode(opcode).Should().Be(mode);
        OpcodeTable.GetLength(opcode).Should().Be(length);
    }

    [Theory]
    [InlineData(0xEA, true)]
    [InlineData(0x1A, false)]
    [InlineData(0xE9, true)]
    [InlineData(0xEB, false)]
    [InlineData(0xA7, false)]
    public void Knows_which_opcodes_are_official(byte opcode, bool official)
    {
        OpcodeTable.IsOfficial(opcode).Should().Be(official);
    }
}
