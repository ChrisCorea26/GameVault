using System.Text;
using GameVault.Core.PlayStation2;
using GameVault.Core.Tests.Harness;
using static GameVault.Core.Tests.PlayStation2.Vu.VuOps;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>
/// ps2autotests' tests/cpu/vu0_macro programs (integer.cpp, transfer.cpp) replayed through VU0's COP2 interface: each
/// COP2 instruction the EE program runs becomes the matching <see cref="Ps2Vu"/> call, and the printed output must equal
/// the hardware's .expected file. The EE's own work (its registers, LQC2/SQC2 addressing) is reproduced directly.
/// </summary>
public class Ps2VuMacroAutotestTests
{
    private const string IntegerExpected = "ps2autotests/cpu/vu0_macro/integer.expected";
    private const string TransferExpected = "ps2autotests/cpu/vu0_macro/transfer.expected";

    private static readonly uint Vnop = Macro(NOP());

    private sealed class MacroProgram(Ps2Vu vu0)
    {
        private readonly StringBuilder _out = new();
        private uint _status;
        private uint _mac;
        private uint _clipping;

        public string Output => _out.ToString();

        public void Printf(string text) => _out.Append(text);

        // Vu0Flags' constructor.
        public void CaptureFlags()
        {
            vu0.ExecuteMacro(Vnop);
            vu0.WriteControl(16, 0);
            vu0.WriteControl(17, 0);
            vu0.WriteControl(18, 0);
            vu0.ExecuteMacro(Vnop);
            _status = vu0.ReadControl(16);
            _mac = vu0.ReadControl(17);
            _clipping = vu0.ReadControl(18);
        }

        public void PrintChanges()
        {
            vu0.ExecuteMacro(Vnop);
            var status = vu0.ReadControl(16);
            var mac = vu0.ReadControl(17);
            var clipping = vu0.ReadControl(18);
            if (status == _status && mac == _mac && clipping == _clipping)
            {
                Printf(" (no flag changes)");
            }
            else
            {
                Printf($" st=+{status & ~_status:x4},-{~status & _status:x4}, mac=+{mac & ~_mac:x4},-{~mac & _mac:x4}, clip={clipping:x8}");
            }

            Printf("\n");
        }

        public void SetVi(int reg, uint value) => vu0.WriteControl(reg, value);

        // SET_VI_M: the register holds the constant's quadword address, then VILWR.lane loads it.
        public void SetViMemory(int reg, int byteAddress, int dest)
        {
            SetVi(reg, (ushort)(byteAddress / 16));
            vu0.ExecuteMacro(Macro(ILWR(dest, VI(reg), VI(reg))));
            vu0.ExecuteMacro(Vnop);
        }

        public void PrintVi(int reg, bool newline)
        {
            Printf(vu0.ReadControl(reg).ToString("x4"));
            if (newline)
            {
                Printf("\n");
            }
        }
    }

    // The constants are u32 pointers, so CVI_S16_MAX = vu_mem0 + 0x10 is byte $40.
    private static readonly (string Name, int Address)[] s_constants =
    [
        ("CVI_ZERO", 0x000), ("CVI_S16_MAX", 0x040), ("CVI_S16_MIN", 0x080), ("CVI_S32_MAX", 0x0C0),
        ("CVI_S32_MIN", 0x100), ("CVI_S64_MAX", 0x140), ("CVI_S64_MIN", 0x180), ("CVI_NEGONE", 0x1C0),
        ("CVI_ONE", 0x200), ("CVI_GARBAGE1", 0x240), ("CVI_GARBAGE2", 0x280),
    ];

    private static int Constant(string name) => s_constants.First(c => c.Name == name).Address;

    private static void SetupConstants(byte[] data)
    {
        VuTestRunner.SetConstant(data, Constant("CVI_ZERO"), 0, 0, 0, 0);
        VuTestRunner.SetConstant(data, Constant("CVI_S16_MAX"), 0x7FFF, 0, 0, 0);
        VuTestRunner.SetConstant(data, Constant("CVI_S16_MIN"), 0xFFFF8000, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);
        VuTestRunner.SetConstant(data, Constant("CVI_S32_MAX"), 0x7FFFFFFF, 0, 0, 0);
        VuTestRunner.SetConstant(data, Constant("CVI_S32_MIN"), 0x80000000, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);
        VuTestRunner.SetConstant(data, Constant("CVI_S64_MAX"), 0xFFFFFFFF, 0x7FFFFFFF, 0, 0);
        VuTestRunner.SetConstant(data, Constant("CVI_S64_MIN"), 0, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFF);
        VuTestRunner.SetConstant(data, Constant("CVI_NEGONE"), 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF);
        VuTestRunner.SetConstant(data, Constant("CVI_ONE"), 1, 0, 0, 0);
        VuTestRunner.SetConstant(data, Constant("CVI_GARBAGE1"), 0x1337, 0x1338, 0x1339, 0x133A);
        VuTestRunner.SetConstant(data, Constant("CVI_GARBAGE2"), 0xDEADBEEF, 0xDEADBEEE, 0xDEADBEED, 0xDEADBEEC);
    }

    private static void TestDdd(MacroProgram p, Ps2Vu vu0, string op, Func<int, int, int, uint> encode)
    {
        p.Printf($"{op}:\n");
        (uint S, uint T)[] immediates =
            [(0, 0), (0, 1), (1, 1), (1, 0), (2, 2), (0xFFFFFFFF, 1), (0xFFFFFFFF, 0xFFFFFFFF), (0xDEAD, 0xFFFF), (0xDEAD, 0x0F0F)];
        foreach (var (s, t) in immediates)
        {
            p.CaptureFlags();
            p.SetVi(1, 0x1337);
            p.SetVi(2, s);
            p.SetVi(3, t);
            vu0.ExecuteMacro(Macro(encode(VI(1), VI(2), VI(3))));
            p.Printf($"  {op} {(int)s}, {(int)t}: ");
            p.PrintVi(1, false);
            p.PrintChanges();
        }

        string[][] memory =
        [
            ["CVI_ZERO", "CVI_ZERO"], ["CVI_ZERO", "CVI_ONE"], ["CVI_ONE", "CVI_ZERO"], ["CVI_ONE", "CVI_ONE"],
            ["CVI_ONE", "CVI_NEGONE"], ["CVI_S16_MAX", "CVI_S16_MAX"], ["CVI_S16_MIN", "CVI_S16_MIN"],
            ["CVI_S32_MAX", "CVI_S32_MAX"], ["CVI_S32_MIN", "CVI_S32_MIN"], ["CVI_S64_MAX", "CVI_S64_MAX"],
            ["CVI_S64_MIN", "CVI_S64_MIN"], ["CVI_GARBAGE1", "CVI_GARBAGE2"],
        ];
        foreach (var pair in memory)
        {
            p.CaptureFlags();
            p.SetViMemory(1, Constant("CVI_GARBAGE1"), DestX);
            p.SetViMemory(2, Constant(pair[0]), DestX);
            p.SetViMemory(3, Constant(pair[1]), DestX);
            vu0.ExecuteMacro(Macro(encode(VI(1), VI(2), VI(3))));
            p.Printf($"  {op} {pair[0]}, {pair[1]}: ");
            p.PrintVi(1, false);
            p.PrintChanges();
        }

        p.SetVi(1, 0x1337);
        p.SetVi(2, 0xDEAD);
        p.SetVi(3, 0x7331);
        vu0.ExecuteMacro(Macro(encode(VI(0), VI(2), VI(3))));
        vu0.ExecuteMacro(Macro(IOR(VI(1), VI(0), VI(0))));
        p.Printf($"  {op} -> $0: ");
        p.PrintVi(1, true);
        p.Printf("\n");
    }

    private static void TestViaddi(MacroProgram p, Ps2Vu vu0)
    {
        p.Printf("viaddi:\n");
        (uint S, int T)[] immediates = [(0, 0), (1, 1), (0, 15), (0, -16), (1233, 1)];
        foreach (var (s, t) in immediates)
        {
            p.CaptureFlags();
            p.SetVi(1, 0x1337);
            p.SetVi(2, s);
            vu0.ExecuteMacro(Macro(IADDI(VI(1), VI(2), t)));
            p.Printf($"  viaddi {(int)s}, {t}: ");
            p.PrintVi(1, false);
            p.PrintChanges();
        }

        (string S, int T)[] memory =
        [
            ("CVI_ZERO", 0), ("CVI_ZERO", 1), ("CVI_ONE", 0), ("CVI_ONE", 1), ("CVI_ONE", -1), ("CVI_S16_MAX", 15),
            ("CVI_S16_MIN", -16), ("CVI_S32_MAX", 15), ("CVI_S32_MIN", -16), ("CVI_S64_MAX", 15), ("CVI_S64_MIN", -15),
            ("CVI_GARBAGE1", 3),
        ];
        foreach (var (s, t) in memory)
        {
            p.CaptureFlags();
            p.SetViMemory(1, Constant("CVI_GARBAGE1"), DestX);
            p.SetViMemory(2, Constant(s), DestX);
            vu0.ExecuteMacro(Macro(IADDI(VI(1), VI(2), t)));
            p.Printf($"  viaddi {s}, {t}: ");
            p.PrintVi(1, false);
            p.PrintChanges();
        }

        p.SetViMemory(1, Constant("CVI_GARBAGE1"), DestY);
        p.SetViMemory(2, Constant("CVI_GARBAGE1"), DestZ);
        vu0.ExecuteMacro(Macro(IADDI(VI(0), VI(2), 4)));
        vu0.ExecuteMacro(Macro(IOR(VI(1), VI(0), VI(0))));
        p.Printf("  viaddi -> $0: ");
        p.PrintVi(1, true);
        p.Printf("\n");
    }

    [TestRomFact(IntegerExpected)]
    public void Integer_MatchesHardware()
    {
        var (vu0, _, _) = FakeVuHost.CreatePair();
        SetupConstants(vu0.DataMemory);
        var p = new MacroProgram(vu0);
        p.Printf("-- TEST BEGIN\n");
        TestDdd(p, vu0, "viadd", IADD);
        TestViaddi(p, vu0);
        TestDdd(p, vu0, "viand", IAND);
        TestDdd(p, vu0, "vior", IOR);
        TestDdd(p, vu0, "visub", ISUB);
        p.Printf("-- TEST END\n");
        Ps2VuAutotestTests.AssertMatchesExpected(p.Output, IntegerExpected);
    }

    private static string Quad(UInt128 value) =>
        $"{(uint)(value >> 96):x8} {(uint)(value >> 64):x8} {(uint)(value >> 32):x8} {(uint)value:x8}";

    private static UInt128 Replicate(uint word) => new(((ulong)word << 32) | word, ((ulong)word << 32) | word);

    private static UInt128 Pattern(uint x, uint y, uint z, uint w) => new(((ulong)w << 32) | z, ((ulong)y << 32) | x);

    [TestRomFact(TransferExpected)]
    public void Transfer_MatchesHardware()
    {
        var (vu0, _, _) = FakeVuHost.CreatePair();
        var p = new MacroProgram(vu0);
        UInt128[] cfPattern =
        [
            Pattern(0x45678123, 0x9ABCDEF0, 0xDEADBEEF, 0xC0DE1337),
            Pattern(0x23456789, 0xABCDEF01, 0xBEEFDEAD, 0xC0DEC0DE),
            Pattern(0x8899AABB, 0xCCDDEEFF, 0x00112233, 0x44556677),
        ];

        p.Printf("-- TEST BEGIN\n");

        // lqc2: QMTC2 a marker, LQC2 over it from CF_PATTERN[1] + offset, QMFC2 it back.
        p.Printf("lqc2:\n");
        foreach (var offset in new[] { 0, 16, -16 })
        {
            vu0.WriteVf(1, Replicate(0x01234567));
            vu0.WriteVf(1, cfPattern[1 + offset / 16]);
            p.Printf($"  lqc2 {offset:+0;-0;+0}: {Quad(vu0.ReadVf(1))}\n");
        }

        vu0.WriteVf(0, cfPattern[0]);
        vu0.ExecuteMacro(Vnop);
        p.Printf($"  lqc2 -> $vf0: {Quad(vu0.ReadVf(0))}\n\n");

        // sqc2: the stored quadword is VF1 as QMTC2 set it.
        p.Printf("sqc2:\n");
        foreach (var (value, offset) in new[] { (0x01234567u, 0), (0x89ABCDEFu, 16), (0x76543210u, -16) })
        {
            vu0.WriteVf(1, Replicate(value));
            p.Printf($"  sqc2 {offset:+0;-0;+0}: {Quad(vu0.ReadVf(1))}\n");
        }

        p.Printf("\n");

        // cfc2 sign-extends the control register into the low doubleword and leaves the high one.
        vu0.ExecuteMacro(Vnop);
        vu0.WriteControl(21, 0xFFFFFFFF);
        var i = vu0.ReadControl(21);
        var low = (ulong)(long)(int)i;
        p.Printf($"cfc2: {Quad(new UInt128(((ulong)0x12345678 << 32) | 0x12345678, low))}\n");
        p.Printf($"cfc2 -> $0: {Quad(UInt128.Zero)}\n");

        // TPC after a program whose E bit is in the pair at $E8.
        Array.Clear(vu0.MicroMemory, 0, 0x100);
        vu0.MicroMemory[0xEC + 3] = 0x40;
        vu0.ExecuteMacro(Macro(0x38));
        vu0.ExecuteMacro(Vnop);
        vu0.WaitForMicro(); // cfc2.i
        p.Printf($"TPC's value after executing VU subroutine: {vu0.ReadControl(26):x8}\n");

        foreach (var reg in new[] { 0, 1, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 27, 28, 29, 31 })
        {
            vu0.ExecuteMacro(Vnop);
            vu0.WriteControl(reg, 0xFFFFFFFF);
            vu0.ExecuteMacro(Vnop);
            p.Printf($"ctc2 -> vi{reg:00}: {vu0.ReadControl(reg):x8}\n");
        }

        vu0.ExecuteMacro(Vnop);
        p.Printf($"qmfc2: {Quad(vu0.ReadVf(0))}\n");
        p.Printf($"qmfc2 -> $0: {Quad(UInt128.Zero)}\n");
        vu0.ExecuteMacro(Vnop);
        vu0.WriteVf(1, Replicate(0x12345678));
        vu0.ExecuteMacro(Vnop);
        p.Printf($"qmtc2: {Quad(vu0.ReadVf(1))}\n");
        vu0.ExecuteMacro(Vnop);
        vu0.WriteVf(0, Replicate(0x12345678));
        vu0.ExecuteMacro(Vnop);
        p.Printf($"qmtc2 -> $vf0: {Quad(vu0.ReadVf(0))}\n");
        p.Printf("-- TEST END\n");
        Ps2VuAutotestTests.AssertMatchesExpected(p.Output, TransferExpected);
    }
}
