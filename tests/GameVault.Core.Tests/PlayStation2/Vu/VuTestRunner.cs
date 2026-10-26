using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using GameVault.Core.PlayStation2;
using static GameVault.Core.Tests.PlayStation2.Vu.VuOps;

namespace GameVault.Core.Tests.PlayStation2.Vu;

/// <summary>A VU host for tests: VU1 for VU0's $4000 window and CMSAR1, and a record of XGKICKs and interrupts.</summary>
internal sealed class FakeVuHost : IPs2VuHost
{
    public Ps2Vu? Unit1 { get; set; }

    public uint Top { get; set; }

    public uint Itop { get; set; }

    public List<uint> Kicks { get; } = [];

    public List<int> Interrupts { get; } = [];

    public uint ReadTop(int unit) => Top;

    public uint ReadItop(int unit) => Itop;

    public void XgKick(uint address) => Kicks.Add(address);

    public void RaiseInterrupt(int unit) => Interrupts.Add(unit);

    public IPs2VectorUnit Vu1 => Unit1 ?? throw new InvalidOperationException("No VU1.");

    /// <summary>Both units wired together, as the console does.</summary>
    public static (Ps2Vu Vu0, Ps2Vu Vu1, FakeVuHost Host) CreatePair()
    {
        var host = new FakeVuHost();
        var vu0 = new Ps2Vu(0, host);
        var vu1 = new Ps2Vu(1, host);
        host.Unit1 = vu1;
        return (vu0, vu1, host);
    }
}

/// <summary>
/// ps2autotests' tests/vu/test_runner.cpp (ISC licence) in C#: a program is assembled into micro memory, the exit
/// sequence stores every register to data memory at $100 from the VU side, and the results are printed as the test
/// programs print them, so the output can be compared with the hardware's .expected file.
/// </summary>
internal class VuTestRunner : VuBlock
{
    private const int RegisterBase = 0x100;
    private const int RegisterSize = 16 * (16 + 32 + 3);

    protected readonly Ps2Vu Vu0;
    protected readonly Ps2Vu Vu1;
    protected readonly FakeVuHost Host;
    protected readonly StringBuilder Out = new();
    private readonly int _vu;
    private uint _status;
    private uint _mac;
    private uint _clipping;

    public VuTestRunner(int vu, Ps2Vu vu0, Ps2Vu vu1, FakeVuHost host)
        : base(vu == 0 ? vu0.MicroMemory : vu1.MicroMemory)
    {
        _vu = vu;
        Vu0 = vu0;
        Vu1 = vu1;
        Host = host;
    }

    public string Output => Out.ToString();

    protected Ps2Vu Unit => _vu == 0 ? Vu0 : Vu1;

    protected byte[] Data => Unit.DataMemory;

    public void Printf(string text) => Out.Append(text);

    /// <summary>Writes a quadword of constants into the unit's data memory (the programs' setup_*_constants).</summary>
    public static void SetConstant(byte[] data, int byteAddress, params uint[] lanes)
    {
        for (var n = 0; n < 4; n++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(byteAddress + n * 4), lanes[n]);
        }
    }

    public void Execute()
    {
        RunnerExit();
        Data.AsSpan(RegisterBase, RegisterSize).Fill(0xCC);
        InitFlags();
        if (_vu == 1)
        {
            Vu0.WriteControl(31, 0); // ctc2 $0, $31: CMSAR1 starts VU1 at 0.
        }
        else
        {
            Vu0.ExecuteMacro(Macro(0x38)); // vcallms 0
        }

        var unit = Unit;
        for (var guard = 0; unit.Running && guard < 100; guard++)
        {
            unit.Run(100_000);
        }

        if (unit.Running)
        {
            Printf("ERROR: Timed out waiting for vu code to finish.\n");
        }
    }

    private void RunnerExit()
    {
        var pos = RegisterBase / 16;
        for (var i = 0; i < 16; i++)
        {
            WrLower(ISW(DestXYZW, VI(i), VI(0), pos++));
        }

        WrLower(IADDIU(VI(2), VI(0), pos));
        for (var i = 0; i < 32; i++)
        {
            WrLower(SQ(DestXYZW, VF(i), VI(0), pos++));
        }

        WrLower(FCGET(VI(1)));
        WrLower(ISW(DestXYZW, VI(1), VI(0), pos++));
        WrLower(FMOR(VI(1), VI(0)));
        WrLower(ISW(DestXYZW, VI(1), VI(0), pos++));
        WrLower(FSOR(VI(1), 0));
        WrLower(ISW(DestXYZW, VI(1), VI(0), pos++));
        SafeExit();
    }

    private void InitFlags()
    {
        Vu0.ExecuteMacro(Macro(NOP()));
        Vu0.WriteControl(16, 0);
        Vu0.WriteControl(17, 0);
        Vu0.WriteControl(18, 0);
        Vu0.ExecuteMacro(Macro(NOP()));
        _status = Vu0.ReadControl(16);
        _mac = Vu0.ReadControl(17);
        _clipping = Vu0.ReadControl(18);
        if (_clipping != 0)
        {
            Printf("WARNING: Clipping flag was not cleared.\n");
        }
    }

    protected int ValueAddress(int byteAddress) => byteAddress / 16;

    public void WrLoadFloatRegister(int dest, int r, int byteAddress) => WrLower(LQ(dest, r, VI(0), ValueAddress(byteAddress)));

    public void WrSetIntegerRegister(int r, uint v)
    {
        if (v == 0x8000)
        {
            WrLower(IADDI(r, VI(0), -1));
            WrLower(ISUBIU(r, r, 0x7FFF));
        }
        else if ((v & 0x8000) != 0)
        {
            WrLower(ISUBIU(r, VI(0), (int)(v & ~0x8000u)));
        }
        else
        {
            WrLower(IADDIU(r, VI(0), (int)v));
        }
    }

    public void WrLoadIntegerRegister(int dest, int r, int byteAddress) => WrLower(ILW(dest, r, VI(0), ValueAddress(byteAddress)));

    private uint RegisterWord(int quad, int lane) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(RegisterBase + quad * 16 + lane * 4));

    public void PrintRegister(int r, bool newline)
    {
        if (r >= 32)
        {
            Printf(RegisterWord(r - 32, 0).ToString("x4"));
        }
        else
        {
            for (var lane = 0; lane < 4; lane++)
            {
                if (lane != 0)
                {
                    Printf(" ");
                }

                PrintFloatBits(RegisterWord(16 + r, lane));
            }
        }

        if (newline)
        {
            Printf("\n");
        }
    }

    public void PrintRegisterField(int r, int field, bool newline)
    {
        PrintFloatBits(RegisterWord(16 + r, field));
        if (newline)
        {
            Printf("\n");
        }
    }

    public void PrintStatus(bool newline)
    {
        var clipping = RegisterWord(48, 0);
        var mac = RegisterWord(49, 0);
        var status = RegisterWord(50, 0);
        if (status == _status && mac == _mac && clipping == _clipping)
        {
            Printf(" (no flag changes)");
        }
        else
        {
            Printf($" st=+{status & ~_status:x4},-{~status & _status:x4}, mac=+{mac & ~_mac:x4},-{~mac & _mac:x4}, clip={clipping:x8}");
        }

        if (newline)
        {
            Printf("\n");
        }
    }

    private void PrintFloatBits(uint bits) => Printf($"{bits:x8}/{CFloat.Format2(bits)}");
}

/// <summary>C's <c>printf("%c%0.2f", sign, fabsf(f))</c> as newlib on the PS2 prints it (exact, round half to even).</summary>
internal static class CFloat
{
    public static string Format2(uint bits)
    {
        var sign = (bits & 0x80000000) != 0 ? '-' : '+';
        var exponent = (int)((bits >> 23) & 0xFF);
        var mantissa = bits & 0x7FFFFF;
        if (exponent == 255)
        {
            return sign + (mantissa == 0 ? "Inf" : "NaN");
        }

        BigInteger significand = exponent == 0 ? mantissa : mantissa | 0x800000;
        var power = (exponent == 0 ? 1 : exponent) - 150;
        BigInteger numerator = significand * 100;
        BigInteger denominator = BigInteger.One;
        if (power >= 0)
        {
            numerator <<= power;
        }
        else
        {
            denominator <<= -power;
        }

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven))
        {
            quotient += 1;
        }

        var whole = BigInteger.DivRem(quotient, 100, out var cents);
        return $"{sign}{whole}.{(int)cents:00}";
    }
}
