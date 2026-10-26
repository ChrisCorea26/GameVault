using System.Buffers.Binary;
using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// GameVault's EE kernel (Phase 17 plan §7): what the BIOS's kernel gives a program, written in C# so ELFs run without
/// Sony's code. It boots the ELF as the BIOS's ExecPS2 does (the standard TLB, D_CTRL.DMAE, the GS's interrupt mask,
/// argc/argv), answers the system calls (threads with a priority scheduler, semaphores, interrupt handlers, the SIF
/// calls, the GS calls, Deci2Call's TTY, SetSyscall's custom calls) and dispatches the INTC's and DMAC's interrupts to
/// the handlers the program registered, which run on the EE in kernel mode as on the real kernel.
/// </summary>
/// <remarks>
/// <para>
/// The design follows Play!'s high-level kernel (BSD-2-Clause, Jean-Philip Desjardins: Source/ee/PS2OS.cpp): thread
/// states, the ready queue ordered by priority, the handler queues, SetupThread/SetupHeap. GameVault keeps the kernel's
/// state in C# (and in its save states) instead of in emulated memory, and calls the program's handlers through small
/// trap stubs in the kernel's low memory: a handler returns to a SYSCALL the kernel catches, which runs the next one.
/// Thread semantics are matched to ps2autotests' kernel/ee results (recorded on a PS2), where they differ from Play!'s.
/// </para>
/// <para>
/// Programs start as the real kernel starts them: in user mode with interrupts enabled (Status $70030C11 after ERET:
/// CU0–2, EDI, EIE, IM2/IM3, KSU user, IE) — or, for a program linked in KSEG0 (GameVault's bare-metal homebrew), in
/// kernel mode with interrupts off, since user mode can't run there.
/// </para>
/// </remarks>
public sealed partial class Ps2Kernel : IStateful
{
    // The kernel's low memory (physical; the program sees it through KSEG0). The real kernel owns RDRAM below $80000.
    internal const uint HandlerReturnTrap = 0x8000_1000;
    internal const uint ThreadExitTrap = 0x8000_1010;
    internal const uint IdleLoop = 0x8000_1020;
    internal const uint CustomReturnTrap = 0x8000_1030;
    internal const uint SifChainBase = 0x0000_2000;
    internal const int SifChainEntries = 256;
    internal const uint InterruptStackTop = 0x8003_0000;

    private const uint StatusKsuMask = 3 << 3;
    private const uint StatusUser = 2 << 3;
    private const uint UserStatus = 0x7003_0C11; // after the kernel's ERET into the program (see the remarks)
    private const uint KernelStatus = 0x7001_0C00; // a KSEG0 program: kernel mode, EIE, IM2/IM3, interrupts off

    private const uint Syscall = 0x0000_000C;
    private const uint Nop = 0;
    private const uint BranchSelf = 0x1000_FFFF; // beq $0, $0, -1

    private static readonly StateTag Tag = StateTag.From("PS2K");

    private readonly Ps2Console _console;
    private readonly R5900Cpu _cpu;
    private readonly byte[] _ram;

    private string _path = "";
    private IReadOnlyList<string> _arguments = [];

    public Ps2Kernel(Ps2Console console)
    {
        _console = console;
        _cpu = console.Cpu;
        _ram = console.Rdram;
        _cpu.ExceptionHook = OnException;
    }

    /// <summary>System calls the kernel doesn't implement, by number (each returned 0): for tracing new programs.</summary>
    public SortedSet<int> UnknownSyscalls { get; } = [];

    /// <summary>Called for every system call with its number (negative for the i-forms) and the caller's PC (tracing).</summary>
    public Action<int, uint>? SyscallTrace { get; set; }

    /// <summary>
    /// An exception the program didn't expect (an address error, a TLB miss, a reserved instruction…), as text; the
    /// EE stops there (<see cref="Ps2Console.Exited"/>). Null while none happened.
    /// </summary>
    public string? Fault { get; private set; }

    // ---- Boot ----

    /// <summary>
    /// Loads <paramref name="elf"/> and starts it as the BIOS's ExecPS2 would: the kernel's stubs and exception vectors in
    /// low memory, the standard TLB, the DMAC enabled, the GS's interrupts masked, argc/argv in $a0/$a1.
    /// </summary>
    internal void Boot(Ps2Elf elf, string path, IReadOnlyList<string> arguments)
    {
        _path = path;
        _arguments = arguments;
        ResetState();
        WriteStubs();
        InstallTlb();

        // The BIOS leaves the DMAC enabled, every GS interrupt masked, and timers 2 and 3 counting (Play!'s
        // PS2OS::Initialize notes games that rely on it).
        _console.Dmac.WriteRegister(0x1000_E000, 1); // D_CTRL.DMAE
        _console.Gs.WritePrivileged(0x1010, 0x7F00); // IMR
        _console.Timers.Write(0x1000_1010, 0x80); // T2_MODE: count the bus clock
        _console.Timers.Write(0x1000_1810, 0x83); // T3_MODE: count H-blanks

        StartProgram(elf, elf.Entry, 0, arguments);
    }

    private void StartProgram(Ps2Elf? elf, uint entry, uint gp, IReadOnlyList<string> arguments)
    {
        elf?.Load(_ram);
        _cpu.Status = entry >= 0x8000_0000 ? KernelStatus : UserStatus;
        var argv = WriteArguments(ArgumentBlock, arguments);
        // ps2sdk's crt0 keeps $a0 as a loader's argument block (ps2link's) and clears a word through it at exit; the
        // BIOS passes none, so $a0 is 0. The arguments reach the program through SetupThread; $a1 points at argv too.
        _cpu.SetGpr64(4, 0);
        _cpu.SetGpr64(5, argv); // a1: argv (ps2sdk's crt0 takes its arguments from SetupThread instead)
        _cpu.SetGpr64(28, gp);
        _cpu.Jump(entry);
    }

    /// <summary>The top 4 KB of RDRAM, which the kernel keeps out of the main thread's stack: argv for the entry point.</summary>
    private const uint ArgumentBlock = Ps2Console.RdramSize - 0x1000;

    /// <summary>Writes argv (the path, then the arguments) at <paramref name="at"/>: the pointers, then the strings; returns argv.</summary>
    private uint WriteArguments(uint at, IReadOnlyList<string> arguments)
    {
        var count = arguments.Count + 1;
        var text = at + (uint)(count + 1) * 4;
        for (var i = 0; i < count; i++)
        {
            var value = i == 0 ? _path : arguments[i - 1];
            WriteWord(at + (uint)i * 4, text);
            foreach (var c in value)
            {
                At(text++, 1)[0] = (byte)c;
            }

            At(text++, 1)[0] = 0;
        }

        WriteWord(at + (uint)count * 4, 0);
        return at;
    }

    private void WriteStubs()
    {
        WriteWord(HandlerReturnTrap, Syscall);
        WriteWord(HandlerReturnTrap + 4, Nop);
        WriteWord(ThreadExitTrap, Syscall);
        WriteWord(ThreadExitTrap + 4, Nop);
        WriteWord(IdleLoop, BranchSelf);
        WriteWord(IdleLoop + 4, Nop);
        WriteWord(CustomReturnTrap, Syscall);
        WriteWord(CustomReturnTrap + 4, Nop);

        // The exception vectors trap into the kernel too, which reports an unexpected exception as a fault.
        foreach (var vector in FaultVectors)
        {
            WriteWord(vector, Syscall);
            WriteWord(vector + 4, Nop);
        }
    }

    private static readonly uint[] FaultVectors = [0x8000_0000, 0x8000_0080, 0x8000_0180, 0x8000_0200];

    /// <summary>
    /// The TLB the kernel sets up for a 32 MB console (ps2sdk's ee/kernel/src/tlbfunc.c, AFL 2.0, lists the BIOS's
    /// entries): the scratchpad at $70000000, the hardware registers and the BIOS for user mode, RDRAM at its own
    /// address from $80000 and the uncached ($20000000) and uncached-accelerated ($30000000) mirrors; the remaining
    /// entries are invalid, unmatchable pages in KSEG3.
    /// </summary>
    private void InstallTlb()
    {
        var index = 0;
        foreach (var (pageMask, entryHi, lo0, lo1) in StandardTlb)
        {
            _cpu.WriteTlbEntry(index++, pageMask, entryHi, lo0, lo1);
        }

        for (var hi = 0xE000_0000u + ((uint)index << 13); index < R5900Cpu.TlbEntries; index++, hi += 0x2000)
        {
            _cpu.WriteTlbEntry(index, 0, hi, 0, 0);
        }

        _cpu.WriteCop0(6, (uint)StandardTlb.Length); // Wired
    }

    private static readonly (uint PageMask, uint EntryHi, uint Lo0, uint Lo1)[] StandardTlb =
    [
        (0x0000_0000, 0x7000_0000, 0x8000_0007, 0x0000_0007),
        (0x0000_6000, 0xFFFF_8000, 0x0000_1E1F, 0x0000_1F1F),
        (0x0000_0000, 0x1000_0000, 0x0040_0017, 0x0040_0053),
        (0x0000_0000, 0x1000_2000, 0x0040_0097, 0x0040_00D7),
        (0x0000_0000, 0x1000_4000, 0x0040_0117, 0x0040_0157),
        (0x0000_0000, 0x1000_6000, 0x0040_0197, 0x0040_01D7),
        (0x0000_0000, 0x1000_8000, 0x0040_0217, 0x0040_0257),
        (0x0000_0000, 0x1000_A000, 0x0040_0297, 0x0040_02D7),
        (0x0000_0000, 0x1000_C000, 0x0040_0313, 0x0040_0357),
        (0x0000_0000, 0x1000_E000, 0x0040_0397, 0x0040_03D7),
        (0x0001_E000, 0x1100_0000, 0x0044_0017, 0x0044_0415),
        (0x0001_E000, 0x1200_0000, 0x0048_0017, 0x0048_0415),
        (0x01FF_E000, 0x1E00_0000, 0x0078_0017, 0x007C_0017),
        (0x0007_E000, 0x0008_0000, 0x0000_201F, 0x0000_301F),
        (0x0007_E000, 0x0010_0000, 0x0000_401F, 0x0000_501F),
        (0x0007_E000, 0x0018_0000, 0x0000_601F, 0x0000_701F),
        (0x001F_E000, 0x0020_0000, 0x0000_801F, 0x0000_C01F),
        (0x001F_E000, 0x0040_0000, 0x0001_001F, 0x0001_401F),
        (0x001F_E000, 0x0060_0000, 0x0001_801F, 0x0001_C01F),
        (0x007F_E000, 0x0080_0000, 0x0002_001F, 0x0003_001F),
        (0x007F_E000, 0x0100_0000, 0x0004_001F, 0x0005_001F),
        (0x007F_E000, 0x0180_0000, 0x0006_001F, 0x0007_001F),
        (0x0007_E000, 0x2008_0000, 0x0000_2017, 0x0000_3017),
        (0x0007_E000, 0x2010_0000, 0x0000_4017, 0x0000_5017),
        (0x0007_E000, 0x2018_0000, 0x0000_6017, 0x0000_7017),
        (0x001F_E000, 0x2020_0000, 0x0000_8017, 0x0000_C017),
        (0x001F_E000, 0x2040_0000, 0x0001_0017, 0x0001_4017),
        (0x001F_E000, 0x2060_0000, 0x0001_8017, 0x0001_C017),
        (0x007F_E000, 0x2080_0000, 0x0002_0017, 0x0003_0017),
        (0x007F_E000, 0x2100_0000, 0x0004_0017, 0x0005_0017),
        (0x007F_E000, 0x2180_0000, 0x0006_0017, 0x0007_0017),
        (0x0007_E000, 0x3010_0000, 0x0000_403F, 0x0000_503F),
        (0x0007_E000, 0x3018_0000, 0x0000_603F, 0x0000_703F),
        (0x001F_E000, 0x3020_0000, 0x0000_803F, 0x0000_C03F),
        (0x001F_E000, 0x3040_0000, 0x0001_003F, 0x0001_403F),
        (0x001F_E000, 0x3060_0000, 0x0001_803F, 0x0001_C03F),
        (0x007F_E000, 0x3080_0000, 0x0002_003F, 0x0003_003F),
        (0x007F_E000, 0x3100_0000, 0x0004_003F, 0x0005_003F),
        (0x007F_E000, 0x3180_0000, 0x0006_003F, 0x0007_003F),
    ];

    // ---- Exceptions ----

    /// <summary>The CPU's exception hook: system calls, interrupts and the kernel's traps.</summary>
    private bool OnException(R5900Exception code)
    {
        var pc = _cpu.CurrentPc;
        switch (code)
        {
            case R5900Exception.Syscall:
                if (pc == HandlerReturnTrap)
                {
                    HandlerReturned();
                }
                else if (pc == ThreadExitTrap)
                {
                    ExitThread(delete: false);
                }
                else if (pc == CustomReturnTrap)
                {
                    CustomSyscallReturned();
                }
                else if (Array.IndexOf(FaultVectors, pc) >= 0)
                {
                    RaiseFault($"unhandled exception {(R5900Exception)((_cpu.Cause >> 2) & 0x1F)} at {_cpu.Epc:X8} (BadVAddr {_cpu.BadVAddr:X8})");
                }
                else
                {
                    HandleSyscall();
                }

                return true;

            case R5900Exception.AddressErrorLoad when pc == ThreadExitTrap:
                // A user-mode thread returned to the kernel's thread epilogue.
                ExitThread(delete: false);
                return true;

            case R5900Exception.AddressErrorLoad when pc == HandlerReturnTrap:
                HandlerReturned();
                return true;

            case R5900Exception.Interrupt:
                return BeginInterrupt();

            default:
                return false;
        }
    }

    private void RaiseFault(string message)
    {
        Fault ??= message;
        _console.Exited = true;
        _cpu.StopRequested = true;
    }

    // ---- Memory helpers (the kernel reads the program's structures by virtual address) ----

    /// <summary>
    /// Where a program's pointer points: RDRAM through any of its windows (KUSEG, KSEG0/1, the $20000000/$30000000
    /// mirrors), or the scratchpad at $70000000.
    /// </summary>
    private Span<byte> At(uint address, int length)
    {
        if ((address >> 28) == 7)
        {
            var offset = (int)(address & (R5900Cpu.ScratchpadSize - 1));
            return _cpu.Scratchpad.AsSpan(offset, Math.Min(length, R5900Cpu.ScratchpadSize - offset));
        }

        var physical = (int)(address & (Ps2Console.RdramSize - 1));
        return _ram.AsSpan(physical, Math.Min(length, Ps2Console.RdramSize - physical));
    }

    internal uint ReadWord(uint address)
    {
        var span = At(address, 4);
        return span.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(span) : 0;
    }

    internal void WriteWord(uint address, uint value)
    {
        var span = At(address, 4);
        if (span.Length == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
    }

    internal string ReadString(uint address, int max = 1024)
    {
        var chars = new List<char>();
        for (var i = 0; i < max; i++)
        {
            var span = At(address + (uint)i, 1);
            if (span.Length == 0 || span[0] == 0)
            {
                break;
            }

            chars.Add((char)span[0]);
        }

        return new string([.. chars]);
    }

    private uint Arg(int index) => (uint)_cpu.GetGpr64(4 + index);

    private void Return(int value) => _cpu.SetGpr64(2, (ulong)(long)value);

    private void Return(uint value) => _cpu.SetGpr64(2, (ulong)(long)(int)value);
}
