using System.Text;
using System.Text.RegularExpressions;

namespace GameVault.Core.Tests.PlayStation2.R5900;

/// <summary>
/// ps2autotests programs too irregular to replay line by line (cpu/ee_simd/funnel, cpu/ee_fpu/fcr and transfer, the
/// branch-delay programs, cpu/ee/lsudelay), ported: each runs the test's instruction sequences on the CPU and prints
/// what the test prints, for comparison with its <c>.expected</c> file.
/// </summary>
internal static partial class AutotestPorts
{
    private const int T6 = 14;
    private const int T7 = 15;
    private const int Rd = 8;
    private const uint Data = 0x8003_0000;

    [GeneratedRegex(@"/[+-][^ ,)\n]*")]
    private static partial Regex FloatTextRegex();

    /// <summary>The bits of each <c>bits/decimal</c> float: the decimals are the PS2 libc's formatting.</summary>
    public static string WithoutFloatText(string text) => FloatTextRegex().Replace(text, "");

    private static string Quad(UInt128 value)
    {
        var high = (ulong)(value >> 64);
        var low = (ulong)value;
        return $"{high >> 32:x8} {(uint)high:x8} {low >> 32:x8} {(uint)low:x8}";
    }

    private static UInt128 Both(ulong value) => new(value, value);

    private static void Store(R5900TestMachine machine, uint address, params uint[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            machine.Bus.WriteWord((address & 0x1FFF_FFFF) + (uint)(i * 4), words[i]);
        }
    }

    public static string Funnel()
    {
        var m = new R5900TestMachine();
        var cpu = m.Cpu;
        var output = new StringBuilder("-- TEST BEGIN\n");
        Store(m, Data, 0x12345678, 0x9ABCDEF0, 0x11223344, 0xDEADBEEF); // C_SHIFT_PATTERN1
        Store(m, Data + 16, 0xAABBCCDD, 0x12121212, 0xFFFFFFFF, 0x1337C0DE); // C_SHIFT_PATTERN2

        string PrintSa()
        {
            cpu.SetGpr(11, Both(0x1234ABCD));
            m.Run(Ee.Mfsa(11));
            return Quad(cpu.GetGpr(11));
        }

        void SetSa(ulong value)
        {
            cpu.SetGpr(T6, Both(value));
            m.Run(Ee.Mtsa(T6));
        }

        output.Append("mfsa:\n");
        foreach (var (value, name) in new (ulong, string)[] { (0, "0"), (1, "1"), (8, "8"), (16, "16"), (0xFFFF, "0xFFFF"), (0, "C_ZERO"), (0x7FFF, "C_S16_MAX") })
        {
            SetSa(value);
            output.Append($"  mfsa {name}: {PrintSa()}\n");
        }

        output.Append("\nmtsa:\n");
        cpu.SetGpr(Rd, 0);
        foreach (var label in new[] { "mtsa immediately before qfsrv", "mtsa with nops before qfsrv" })
        {
            var nops = label.Contains("nops") ? new uint[] { 0, 0, 0 } : [];
            cpu.SetGpr64(T6 + 10, Data); // the pattern's address in $t8
            m.Run([Ee.Mtsa(0), Ee.Lq(T6, T6 + 10, 0), Ee.R(0x0F), Ee.Nop, Ee.Ori(T7, 0, 1), Ee.Mtsa(T7), .. nops, Ee.Qfsrv(Rd, T6, T6)]);
            output.Append($"  {label}: {Quad(cpu.GetGpr(Rd))}\n");
        }

        foreach (var (name, encode, k) in new (string, Func<int, int, uint>, string)[] { ("mtsab", Ee.Mtsab, "15 16 17"), ("mtsah", Ee.Mtsah, "7 8 9") })
        {
            output.Append($"\n{name}:\n");
            cpu.SetGpr(Rd, 0);
            m.Run(Ee.Mtsa(0), Ee.Nop, Ee.Nop, Ee.Mfsa(Rd), encode(0, 1));
            output.Append($"  {name} next to mfsa: {Quad(cpu.GetGpr(Rd))} ({PrintSa()})\n");
            m.Run(Ee.Mtsa(0), Ee.Nop, Ee.Nop, Ee.Mfsa(Rd), Ee.Nop, Ee.Nop, encode(0, 1));
            output.Append($"  {name} with space after mfsa: {Quad(cpu.GetGpr(Rd))} ({PrintSa()})\n");
            foreach (var immediate in k.Split(' '))
            {
                m.Run(encode(0, int.Parse(immediate)));
                output.Append($"  {name} {immediate}: {PrintSa()}\n");
            }

            m.Run(Ee.Ori(T6, 0, 4), encode(T6, 1));
            output.Append($"  {name} 4 ^ 1: {PrintSa()}\n");
            m.Run(Ee.Ori(T6, 0, 5), encode(T6, 1));
            output.Append($"  {name} 5 ^ 1: {PrintSa()}\n");
        }

        output.Append("\nqdsrv:\n");
        foreach (var shift in new[] { 0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17 })
        {
            cpu.SetGpr(Rd, 0);
            cpu.SetGpr64(T6 + 10, Data);
            m.Run(Ee.Lq(T6, T6 + 10, 0), Ee.Lq(T7, T6 + 10, 16), Ee.R(0x0F), Ee.Mtsab(0, shift), Ee.Nop, Ee.Nop, Ee.Nop, Ee.Qfsrv(Rd, T6, T7));
            output.Append($"  qdsrv {shift}: {Quad(cpu.GetGpr(Rd))}\n");
        }

        output.Append("\n-- TEST END\n");
        return output.ToString();
    }

    public static string Fcr()
    {
        var m = new R5900TestMachine();
        var cpu = m.Cpu;
        var output = new StringBuilder("-- TEST BEGIN\n");

        uint ReadFcr(int index)
        {
            m.Run(Ee.Lui(Rd, 0), Ee.Cfc1(Rd, index), Ee.Nop);
            return (uint)cpu.GetGpr64(Rd);
        }

        void Print(string title, bool all)
        {
            output.Append($"{title}:\n  ");
            if (all)
            {
                for (var i = 0; i < 31; i++)
                {
                    output.Append($"fcr{i}: {ReadFcr(i):x8}, ");
                }
            }
            else
            {
                output.Append($"fcr0: {ReadFcr(0):x8}, ");
            }

            output.Append($"fcr31: {ReadFcr(31):x8}\n");
        }

        void Update(uint value)
        {
            cpu.SetGpr64(2, value);
            m.Run(Ee.Ctc1(2, 31), Ee.Nop);
        }

        Print("Initial", true);
        cpu.SetGpr64(4, 0xDEADBEEF);
        m.Run(Ee.Ctc1(4, 0), Ee.Nop);
        Print("Update fcr0", false);
        foreach (var (value, title) in new (uint, string)[]
                 {
                     (0x00000003, "Update rounding mode (RM)"), (0x0000007C, "Update flags"),
                 })
        {
            Update(value);
            Print(title, false);
        }

        Print("All regs", true);
        foreach (var (value, title) in new (uint, string)[]
                 {
                     (0x00000F80, "Update enables"), (0x0001F000, "Update cause"), (0x00020000, "Update unimplemented (E)"),
                     (0x01000000, "Update flushing (FS)"), (0x00400000, "Update flushing (FO)"), (0x00200000, "Update flushing (FN)"),
                     (0x00800000, "Update FCC"), (0xFE000000, "Update FCC1-7"), (0x001C0000, "Update unknown"),
                 })
        {
            Update(value);
            Print(title, false);
        }

        output.Append("\nFlag situations:\n");
        var situations = new (string Title, uint[] Program)[]
        {
            ("sqrt(-1)", [Ee.Lui(2, 0xBF80), Ee.Mtc1(2, 0), Ee.Nop, Ee.FpuS(0x04, 0, 0, 0)]),
            ("Divide zero by zero", [Ee.Mtc1(0, 0), Ee.Nop, Ee.FpuS(0x03, 0, 0, 0)]),
            ("Divide one by zero", [Ee.Lui(2, 0x3F80), Ee.Mtc1(2, 1), Ee.Mtc1(0, 0), Ee.Nop, Ee.FpuS(0x03, 0, 1, 0)]),
            ("NAN math", [Ee.Lui(2, 0x7F80), Ee.Ori(2, 2, 1), Ee.Mtc1(2, 0), Ee.Nop, Ee.FpuS(0x00, 0, 0, 0)]),
            ("Overflow", [Ee.Lui(2, 0x7F7F), Ee.Ori(2, 2, 0xFFFF), Ee.Mtc1(2, 0), Ee.Nop, Ee.FpuS(0x02, 0, 0, 0)]),
            ("Underflow", [Ee.Lui(2, 0x0080), Ee.Mtc1(2, 1), Ee.Lui(2, 0x4040), Ee.Mtc1(2, 0), Ee.Nop, Ee.FpuS(0x03, 0, 1, 0)]),
            ("Inexact", [Ee.Lui(2, 0x3F80), Ee.Mtc1(2, 1), Ee.Lui(2, 0x4040), Ee.Ori(2, 2, 0xFFFF), Ee.Mtc1(2, 0), Ee.Nop, Ee.FpuS(0x03, 0, 1, 0)]),
        };
        foreach (var (title, program) in situations)
        {
            Update(0);
            m.Run(program);
            Print(title, false);
            output.Append($"  result = {cpu.GetFpr(0):x8}\n");
        }

        output.Append("-- TEST END\n");
        return output.ToString();
    }

    public static string Transfer()
    {
        var m = new R5900TestMachine();
        var cpu = m.Cpu;
        var output = new StringBuilder("-- TEST BEGIN\n");
        uint[] pattern =
        {
            0x45678123, 0x9ABCDEF0, 0xDEADBEEF, 0xC0DE1337, 0x23456789, 0xABCDEF01, 0xBEEFDEAD, 0xC0DEC0DE,
            0x8899AABB, 0xCCDDEEFF, 0x00112233, 0x44556677,
        };

        // CF_PATTERN is float[3][4] initialised with integers: each converted to the nearest float.
        var floats = pattern.Select(v => BitConverter.SingleToUInt32Bits(v)).ToArray();
        cpu.SetGpr64(9, Data + 16);

        output.Append("lwc1:\n");
        var offsets = new[] { 0, 4, -4 };
        cpu.SetFpr(1, 0x1337);
        for (var k = 0; k < 6; k++)
        {
            Store(m, Data, floats);
            cpu.SetFpr(1, k < 3 ? 0x3F800001u : 0x1337u);
            m.Run(Ee.I(0x31, 1, 9, offsets[k % 3]), Ee.R(0x0F));
            output.Append($"  lwc1 {(k < 3 ? offsets[k].ToString("+0;-0") : "+" + offsets[k % 3])}: {cpu.GetFpr(1):x8}\n");
        }

        output.Append("\nswc1:\n");
        for (var k = 0; k < 6; k++)
        {
            Store(m, Data, floats);
            cpu.SetFpr(1, k < 3 ? 0x3F800001u : 0x1337u);
            var offset = offsets[k % 3];
            m.Run(Ee.I(0x39, 1, 9, offset), Ee.R(0x0F));
            var word = m.Bus.ReadWord(((Data + 16) & 0x1FFF_FFFF) + (uint)offset);
            output.Append($"  swc1 {(k < 3 ? offset.ToString("+0;-0") : "+" + offset)}: {word:x8}\n");
        }

        output.Append('\n');

        // cfc1/ctc1/mfc1 with a register first set to its value in both halves of both doublewords.
        cpu.SetGpr(Rd, Both(0x12345678_12345678));
        m.Run(Ee.Cfc1(Rd, 31), Ee.R(0x0F));
        output.Append($"cfc1: {Quad(cpu.GetGpr(Rd))}\n");
        m.Run(Ee.Cfc1(0, 31), Ee.R(0x0F), Ee.Por(Rd, 0, 0));
        output.Append($"cfc1 -> $0: {Quad(cpu.GetGpr(Rd))}\n");
        cpu.SetGpr(Rd, Both(0xFFFFFFFF_FFFFFFFF));
        m.Run(Ee.Ctc1(Rd, 31), Ee.R(0x0F), Ee.Cfc1(Rd, 31), Ee.R(0x0F));
        output.Append($"ctc1: {Quad(cpu.GetGpr(Rd))}\n");
        cpu.SetFpr(1, 0xBF800000);
        cpu.SetGpr(Rd, Both(0x80F0000F_80F0000F));
        m.Run(Ee.Mfc1(Rd, 1), Ee.R(0x0F));
        output.Append($"mfc1 negative: {Quad(cpu.GetGpr(Rd))}\n");
        cpu.SetFpr(1, 0x3F800000);
        cpu.SetGpr(Rd, Both(0x00F0000F_00F0000F));
        m.Run(Ee.Mfc1(Rd, 1), Ee.R(0x0F));
        output.Append($"mfc1 positive: {Quad(cpu.GetGpr(Rd))}\n");
        m.Run(Ee.Mfc1(0, 1), Ee.R(0x0F), Ee.Por(Rd, 0, 0));
        output.Append($"mfc1 -> $0: {Quad(cpu.GetGpr(Rd))}\n");
        cpu.SetGpr(Rd, Both(0xFFFFFFFF_80F0000F));
        m.Run(Ee.Mtc1(Rd, 1), Ee.R(0x0F));
        output.Append($"mtc1 negative: {cpu.GetFpr(1):x8}\n");
        cpu.SetGpr(Rd, Both(0xFFFFFFFF_00F0000F));
        m.Run(Ee.Mtc1(Rd, 1), Ee.R(0x0F));
        output.Append($"mtc1 positive: {cpu.GetFpr(1):x8}\n");
        output.Append("-- TEST END\n");
        return output.ToString();
    }

    /// <summary>Four branches in a row (each in the previous one's delay slot), then targets 1–4: "li result, n; j skip; nop".</summary>
    private static uint[] Chain(Func<int, uint> branch, uint[] prologue)
    {
        var program = new List<uint>(prologue);
        var first = program.Count;
        var targets = first + 5;
        for (var i = 0; i < 4; i++)
        {
            program.Add(branch(targets + (i * 3) - (first + i + 1)));
        }

        program.Add(Ee.Nop);
        var skip = targets + 12;
        for (var i = 0; i < 4; i++)
        {
            program.Add(Ee.Ori(Rd, 0, i + 1));
            program.Add(Ee.J(R5900TestMachine.Origin + (uint)(skip * 4)));
            program.Add(Ee.Nop);
        }

        return [.. program];
    }

    /// <summary>cpu/ee_fpu/branchdelay: BC1T and BC1TL chains after a true C.EQ.</summary>
    public static string FpuBranchDelay()
    {
        var m = new R5900TestMachine();
        var output = new StringBuilder("-- TEST BEGIN\n");
        foreach (var (name, kind) in new[] { ("bc1t", 1), ("bc1tl", 3) })
        {
            m.Run(Chain(offset => Ee.Bc1(kind, offset), [Ee.FpuS(0x32, 0, 0, 0)]));
            output.Append($"{name}: delay branch: {(uint)m.Cpu.GetGpr64(Rd):x8}\n");
        }

        output.Append("-- TEST END\n");
        return output.ToString();
    }

    /// <summary>cpu/ee/branchdelay, without its "jalr: non-ra" line (an absolute address in the test's ELF).</summary>
    public static string EeBranchDelay()
    {
        var m = new R5900TestMachine();
        var cpu = m.Cpu;
        var output = new StringBuilder("-- TEST BEGIN\n");
        var o = R5900TestMachine.Origin;

        m.Run(Chain(offset => Ee.Beq(8 + 1, 8 + 1, offset), [Ee.Ori(9, 0, 0)]));
        output.Append($"beq: delay branch: {(uint)cpu.GetGpr64(Rd):x8}\n");

        // jal target2 with "li ra, 2" in its delay slot: the delay slot's write wins.
        uint target2 = o + 5 * 4;
        m.Run(0x0C00_0000 | ((target2 >> 2) & 0x03FF_FFFF), Ee.Ori(31, 0, 2), Ee.Ori(Rd, 0, 1), Ee.J(o + 6 * 4), Ee.Nop, Ee.R(0x25, Rd, 31, 0));
        output.Append($"jal: ra order: {(uint)cpu.GetGpr64(Rd):x8}\n");

        uint[] Jalr(uint jalr, uint delay, uint atTarget, uint target1 = 0)
        {
            var t = o + 7 * 4;
            return [Ee.Lui(8 + 2, (int)(t >> 16)), Ee.Ori(8 + 2, 8 + 2, (int)(t & 0xFFFF)), jalr, delay, target1 == 0 ? Ee.Ori(Rd, 0, 1) : target1, Ee.J(o + 8 * 4), Ee.Nop, atTarget];
        }

        // jalr $t2 with "li ra, 2" in the delay slot.
        m.Run(Jalr(Ee.R(0x09, 31, 10), Ee.Ori(31, 0, 2), Ee.R(0x25, Rd, 31, 0)));
        output.Append($"jalr: ra order: {(uint)cpu.GetGpr64(Rd):x8}\n");

        // jalr result, $t2 with "li result, 1" in the delay slot.
        cpu.SetGpr64(Rd, ulong.MaxValue);
        m.Run(Jalr(Ee.R(0x09, Rd, 10), Ee.Ori(Rd, 0, 1), Ee.Nop, Ee.Nop));
        output.Append($"jalr: non-ra order: {(uint)cpu.GetGpr64(Rd):x8}\n");

        // jalr $t2, $t2: jumps to the old $t2.
        m.Run(Jalr(Ee.R(0x09, 10, 10), Ee.Nop, Ee.Ori(Rd, 0, 2)));
        output.Append($"jalr: rs/rd match: {(uint)cpu.GetGpr64(Rd):x8}\n");

        // jalr $t2 with the delay slot overwriting $t2.
        m.Run(Jalr(Ee.R(0x09, 31, 10), Ee.Ori(10, 0, 5), Ee.Ori(Rd, 0, 2)));
        output.Append($"jalr: clobber rs: {(uint)cpu.GetGpr64(Rd):x8}\n");

        foreach (var (name, rt) in new[] { ("bltzal", 0x10), ("bltzall", 0x12), ("bgezal", 0x11), ("bgezall", 0x13) })
        {
            // Taken (for bltz*, $t0 = -1; for bgez*, 0), with "li ra, 2" in the delay slot.
            cpu.SetGpr64(9, name.StartsWith("bltz", StringComparison.Ordinal) ? ulong.MaxValue : 0);
            m.Run(Ee.I(0x01, rt, 9, 4), Ee.Ori(31, 0, 2), Ee.Ori(Rd, 0, 1), Ee.J(o + 6 * 4), Ee.Nop, Ee.R(0x25, Rd, 31, 0));
            output.Append($"{name}: ra order: {(uint)cpu.GetGpr64(Rd):x8}\n");
        }

        output.Append("-- TEST END\n");
        return output.ToString();
    }

    public static string LsuDelay()
    {
        var m = new R5900TestMachine();
        Store(m, Data, 0x13371337);
        m.Cpu.SetGpr64(9, Data);
        m.Run(Ee.Lui(8, 0x1122), Ee.Ori(8, 8, 0x3344), Ee.Lw(8, 9, 0), Ee.R(0x25, 2, 0, 8), Ee.R(0x0F));
        return $"-- TEST BEGIN\nlw: {(uint)m.Cpu.GetGpr64(2):x8}\n-- TEST END\n";
    }
}
