using System.Globalization;
using System.Text;

namespace GameVault.Core.Cpu;

/// <summary>
/// Formats the CPU state before an instruction in the <c>nestest.log</c> layout, for example
/// <c>C000  4C F5 C5  JMP $C5F5                       A:00 X:00 Y:00 P:24 SP:FD PPU:  0, 21 CYC:7</c>.
/// Debug-only: it allocates, so keep it out of the hot path.
/// </summary>
public static class CpuTracer
{
    public static string Format(Cpu6502 cpu, Func<ushort, byte> peek, int scanline, int dot)
    {
        var pc = cpu.PC;
        var opcode = peek(pc);
        var length = OpcodeTable.GetLength(opcode);
        var op1 = peek((ushort)(pc + 1));
        var op2 = peek((ushort)(pc + 2));

        var bytes = length switch
        {
            1 => $"{opcode:X2}",
            2 => $"{opcode:X2} {op1:X2}",
            _ => $"{opcode:X2} {op1:X2} {op2:X2}",
        };

        var line = new StringBuilder(96);
        line.Append(CultureInfo.InvariantCulture, $"{pc:X4}  {bytes,-9}");
        line.Append(OpcodeTable.IsOfficial(opcode) ? ' ' : '*');
        line.Append(Disassemble(cpu, peek, opcode, op1, op2).PadRight(32));
        line.Append(CultureInfo.InvariantCulture,
            $"A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.Status:X2} SP:{cpu.SP:X2} PPU:{scanline,3},{dot,3} CYC:{cpu.CycleCount}");
        return line.ToString();
    }

    private static string Disassemble(Cpu6502 cpu, Func<ushort, byte> peek, byte opcode, byte op1, byte op2)
    {
        var instruction = OpcodeTable.GetInstruction(opcode);
        var name = instruction.ToString();
        var absolute = (ushort)(op1 | (op2 << 8));

        ushort Word(byte zeroPage) => (ushort)(peek(zeroPage) | (peek((byte)(zeroPage + 1)) << 8));

        return OpcodeTable.GetMode(opcode) switch
        {
            AddressingMode.Implied => name,
            AddressingMode.Accumulator => $"{name} A",
            AddressingMode.Immediate => $"{name} #${op1:X2}",
            AddressingMode.ZeroPage => $"{name} ${op1:X2} = {peek(op1):X2}",
            AddressingMode.ZeroPageX => ZeroPageIndexed(name, op1, 'X', cpu.X, peek),
            AddressingMode.ZeroPageY => ZeroPageIndexed(name, op1, 'Y', cpu.Y, peek),
            AddressingMode.Absolute when instruction is Instruction.JMP or Instruction.JSR => $"{name} ${absolute:X4}",
            AddressingMode.Absolute => $"{name} ${absolute:X4} = {peek(absolute):X2}",
            AddressingMode.AbsoluteX => AbsoluteIndexed(name, absolute, 'X', cpu.X, peek),
            AddressingMode.AbsoluteY => AbsoluteIndexed(name, absolute, 'Y', cpu.Y, peek),
            AddressingMode.Indirect => $"{name} (${absolute:X4}) = {IndirectTarget(absolute, peek):X4}",
            AddressingMode.IndirectX => IndirectX(name, op1, cpu.X, peek),
            AddressingMode.IndirectY => IndirectY(name, op1, cpu.Y, Word(op1), peek),
            AddressingMode.Relative => $"{name} ${(ushort)(cpu.PC + 2 + (sbyte)op1):X4}",
            _ => name,
        };
    }

    private static string ZeroPageIndexed(string name, byte zp, char register, byte index, Func<ushort, byte> peek)
    {
        var address = (byte)(zp + index);
        return $"{name} ${zp:X2},{register} @ {address:X2} = {peek(address):X2}";
    }

    private static string AbsoluteIndexed(string name, ushort baseAddress, char register, byte index, Func<ushort, byte> peek)
    {
        var address = (ushort)(baseAddress + index);
        return $"{name} ${baseAddress:X4},{register} @ {address:X4} = {peek(address):X2}";
    }

    private static string IndirectX(string name, byte zp, byte x, Func<ushort, byte> peek)
    {
        var pointer = (byte)(zp + x);
        var address = (ushort)(peek(pointer) | (peek((byte)(pointer + 1)) << 8));
        return $"{name} (${zp:X2},X) @ {pointer:X2} = {address:X4} = {peek(address):X2}";
    }

    private static string IndirectY(string name, byte zp, byte y, ushort baseAddress, Func<ushort, byte> peek)
    {
        var address = (ushort)(baseAddress + y);
        return $"{name} (${zp:X2}),Y = {baseAddress:X4} @ {address:X4} = {peek(address):X2}";
    }

    private static ushort IndirectTarget(ushort pointer, Func<ushort, byte> peek) =>
        (ushort)(peek(pointer) | (peek((ushort)((pointer & 0xFF00) | ((pointer + 1) & 0xFF))) << 8));
}
