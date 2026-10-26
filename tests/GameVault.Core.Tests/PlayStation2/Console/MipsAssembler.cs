namespace GameVault.Core.Tests.PlayStation2.Console;

/// <summary>
/// A small two-pass MIPS assembler for the hand-written test programs (the stand-in BIOS): the MIPS I instructions both
/// the EE and the IOP run, a few R5900 ones (LQ/SQ, ERET, TLBWI), labels and data. Branches, jumps and loads get a NOP
/// after them, so the same code is safe on the IOP's R3000 (load delay) and the EE.
/// </summary>
internal sealed class MipsAssembler(uint origin)
{
    // Registers.
    public const int Zero = 0, V0 = 2, V1 = 3, A0 = 4, A1 = 5, A2 = 6, A3 = 7;
    public const int T0 = 8, T1 = 9, T2 = 10, T3 = 11, S0 = 16, S1 = 17, S2 = 18, S3 = 19, S4 = 20, S5 = 21;
    public const int K0 = 26, Gp = 28, Ra = 31;

    private readonly List<uint> _words = [];
    private readonly Dictionary<string, uint> _labels = [];
    private readonly List<(int Index, string Label, char Kind)> _fixups = [];

    /// <summary>The address of the next word.</summary>
    public uint Here => origin + (uint)(_words.Count * 4);

    public MipsAssembler Label(string name)
    {
        _labels.Add(name, Here);
        return this;
    }

    /// <summary>Pads with zeros up to <paramref name="address"/>.</summary>
    public void Org(uint address)
    {
        if (address < Here)
        {
            throw new InvalidOperationException($"Org {address:X8} is behind {Here:X8}.");
        }

        while (Here < address)
        {
            _words.Add(0);
        }
    }

    public void Word(uint value) => _words.Add(value);

    /// <summary>A zero-terminated ASCII string, padded to a word.</summary>
    public void Ascii(string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        for (var i = 0; i < bytes.Length; i += 4)
        {
            uint word = 0;
            for (var b = 0; b < 4 && i + b < bytes.Length; b++)
            {
                word |= (uint)bytes[i + b] << (8 * b);
            }

            _words.Add(word);
        }
    }

    // ---- Encodings ----

    private void R(int function, int rd, int rs, int rt, int sa = 0) =>
        _words.Add((uint)((rs << 21) | (rt << 16) | (rd << 11) | (sa << 6) | function));

    private void I(int opcode, int rt, int rs, int immediate) =>
        _words.Add((uint)((opcode << 26) | (rs << 21) | (rt << 16) | (immediate & 0xFFFF)));

    public void Nop() => _words.Add(0);

    public void Addu(int rd, int rs, int rt) => R(0x21, rd, rs, rt);

    public void Move(int rd, int rs) => Addu(rd, rs, Zero);

    public void And(int rd, int rs, int rt) => R(0x24, rd, rs, rt);

    public void Or(int rd, int rs, int rt) => R(0x25, rd, rs, rt);

    public void Nor(int rd, int rs, int rt) => R(0x27, rd, rs, rt);

    public void Srl(int rd, int rt, int sa) => R(0x02, rd, 0, rt, sa);

    public void Addiu(int rt, int rs, int immediate) => I(0x09, rt, rs, immediate);

    public void Sltiu(int rt, int rs, int immediate) => I(0x0B, rt, rs, immediate);

    public void Andi(int rt, int rs, int immediate) => I(0x0C, rt, rs, immediate);

    public void Ori(int rt, int rs, int immediate) => I(0x0D, rt, rs, immediate);

    public void Lui(int rt, int immediate) => I(0x0F, rt, 0, immediate);

    /// <summary>lui + ori: always two words.</summary>
    public void Li(int rt, uint value)
    {
        Lui(rt, (int)(value >> 16));
        Ori(rt, rt, (int)(value & 0xFFFF));
    }

    /// <summary>A label's address (lui + ori).</summary>
    public void La(int rt, string label)
    {
        _fixups.Add((_words.Count, label, 'h'));
        Lui(rt, 0);
        _fixups.Add((_words.Count, label, 'l'));
        Ori(rt, rt, 0);
    }

    public void Lw(int rt, int rs, int offset)
    {
        I(0x23, rt, rs, offset);
        Nop();
    }

    public void Lbu(int rt, int rs, int offset)
    {
        I(0x24, rt, rs, offset);
        Nop();
    }

    public void Sb(int rt, int rs, int offset) => I(0x28, rt, rs, offset);

    public void Sw(int rt, int rs, int offset) => I(0x2B, rt, rs, offset);

    /// <summary>R5900: a 128-bit load.</summary>
    public void Lq(int rt, int rs, int offset) => I(0x1E, rt, rs, offset);

    /// <summary>R5900: a 128-bit store.</summary>
    public void Sq(int rt, int rs, int offset) => I(0x1F, rt, rs, offset);

    public void Mfc0(int rt, int rd)
    {
        _words.Add((uint)((0x10 << 26) | (rt << 16) | (rd << 11)));
        Nop();
    }

    public void Mtc0(int rt, int rd) => _words.Add((uint)((0x10 << 26) | (0x04 << 21) | (rt << 16) | (rd << 11)));

    public void Tlbwi() => _words.Add(0x4200_0002);

    public void Eret() => _words.Add(0x4200_0018);

    public void Syscall() => _words.Add(0x0000_000C);

    private void Branch(int opcode, int rs, int rt, string label)
    {
        _fixups.Add((_words.Count, label, 'b'));
        I(opcode, rt, rs, 0);
        Nop();
    }

    public void Beq(int rs, int rt, string label) => Branch(0x04, rs, rt, label);

    public void Bne(int rs, int rt, string label) => Branch(0x05, rs, rt, label);

    public void Blez(int rs, string label) => Branch(0x06, rs, 0, label);

    public void Bgtz(int rs, string label) => Branch(0x07, rs, 0, label);

    public void B(string label) => Beq(Zero, Zero, label);

    /// <summary><c>b .</c></summary>
    public void Halt()
    {
        _words.Add(0x1000_FFFF);
        Nop();
    }

    public void J(string label)
    {
        _fixups.Add((_words.Count, label, 'j'));
        _words.Add(0x0800_0000);
        Nop();
    }

    public void Jal(string label)
    {
        _fixups.Add((_words.Count, label, 'j'));
        _words.Add(0x0C00_0000);
        Nop();
    }

    public void Jr(int rs)
    {
        R(0x08, 0, rs, 0);
        Nop();
    }

    /// <summary>Resolves the labels and returns the words.</summary>
    public uint[] Assemble()
    {
        var words = _words.ToArray();
        foreach (var (index, label, kind) in _fixups)
        {
            var target = _labels[label];
            var at = origin + (uint)(index * 4);
            words[index] |= kind switch
            {
                'h' => target >> 16,
                'l' => target & 0xFFFF,
                'b' => (uint)((int)(target - (at + 4)) >> 2) & 0xFFFF,
                _ => (target >> 2) & 0x03FF_FFFF,
            };
        }

        return words;
    }

    /// <summary>The assembled program as little-endian bytes.</summary>
    public byte[] AssembleBytes()
    {
        var words = Assemble();
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }
}
