using System.Buffers.Binary;
using System.Text;

namespace GameVault.Core.PlayStation2;

// Fast boot through the BIOS (Phase 17 plan, "Two ways to boot"): the BIOS runs until its kernel starts EELOAD, the EE
// loader that would load OSDSYS (or the disc's game); then the ELF is loaded instead and started with ExecPS2.
public sealed partial class Ps2Console
{
    /// <summary>
    /// Where the BIOS's kernel starts EELOAD (PCSX2's <c>EELOAD_START</c>, read for facts only: its interpreter and
    /// recompilers watch the EE's PC reaching it). The first time the EE gets there, the BIOS has set up the EE kernel and
    /// the IOP is booting its modules.
    /// </summary>
    public const uint EeLoaderEntry = 0x0008_2000;

    /// <summary>The EE kernel's ExecPS2 system call (ps2tek: "EE Syscalls"): a0 = entry, a1 = gp, a2 = argc, a3 = argv.</summary>
    private const int ExecPs2Syscall = 7;

    // The stub ExecPS2 is called from, and the arguments it passes, in EELOAD's page (EELOAD itself isn't run).
    private const uint StubAddress = EeLoaderEntry;
    private const uint ArgvAddress = EeLoaderEntry + 0x100;
    private const int ArgumentSpace = 0xE00;

    private bool _bootHookArmed;

    /// <summary>
    /// With a BIOS, an ELF (or a disc with fast boot on) waits for the EE reaching EELOAD's entry; a breakpoint there
    /// costs nothing elsewhere.
    /// </summary>
    private void ArmBootHook()
    {
        _bootHookArmed = _bios is not null && (Elf is not null || (Options.FastBoot && _boot.Disc is not null));
        if (_bootHookArmed)
        {
            Cpu.SetBreakpoint(EeLoaderEntry);
        }
        else
        {
            Cpu.ClearBreakpoint();
        }
    }

    /// <summary>
    /// The EE reached EELOAD's entry: the ELF (or the disc's boot executable) goes into RDRAM and is started by ExecPS2
    /// with its arguments, as EELOAD would after loading it — the BIOS's kernel then starts it on a fresh main thread and
    /// hands the arguments to its <c>SetupThread</c>. When there's nothing to load (a disc without SYSTEM.CNF or BOOT2, or
    /// an ELF that would overwrite EELOAD's page), EELOAD runs as usual.
    /// </summary>
    private void EeLoaderReached()
    {
        Cpu.ClearBreakpoint();
        _bootHookArmed = false;

        Ps2Elf? elf = null;
        string path;
        IReadOnlyList<string> arguments;
        if (Elf is not null)
        {
            elf = Elf;
            path = _boot.Path;
            arguments = _boot.Arguments;
        }
        else
        {
            path = _boot.Disc is { } disc ? Ps2DiscFiles.ReadBoot2(disc) ?? "" : "";
            arguments = [];
            if (path.Length > 0 && Ps2DiscFiles.ReadFile(_boot.Disc!, path) is { } bytes && Ps2Elf.IsElf(bytes))
            {
                try
                {
                    elf = Ps2Elf.Parse(bytes);
                }
                catch (InvalidDataException)
                {
                    elf = null;
                }
            }
        }

        if (elf is null)
        {
            return;
        }

        var (start, end) = elf.PhysicalRange;
        if (start < StubAddress + 0x1000 && end > StubAddress)
        {
            return;
        }

        elf.Load(_rdram);
        var argc = WriteArguments(path, arguments);

        // EELOAD's page now holds:  addiu v1, zero, 7 ; syscall ; b . ; nop   (the EE's PC is already at its start).
        var stub = _rdram.AsSpan((int)StubAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(stub, 0x2403_0000u | ExecPs2Syscall);
        BinaryPrimitives.WriteUInt32LittleEndian(stub[4..], 0x0000_000C);
        BinaryPrimitives.WriteUInt32LittleEndian(stub[8..], 0x1000_FFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(stub[12..], 0);
        Cpu.SetGpr64(4, SignExtend(elf.Entry));
        Cpu.SetGpr64(5, 0); // gp: the program's crt0 sets its own
        Cpu.SetGpr64(6, (ulong)argc);
        Cpu.SetGpr64(7, ArgvAddress);
        Cpu.Jump(StubAddress);
    }

    /// <summary>argv[] (pointers, then the strings) at <see cref="ArgvAddress"/>; returns argc.</summary>
    private int WriteArguments(string path, IReadOnlyList<string> arguments)
    {
        var all = new List<string>(arguments.Count + 1) { path };
        all.AddRange(arguments);
        var count = all.Count;
        while (count > 1 && (count * 4) + all.Take(count).Sum(a => Encoding.ASCII.GetByteCount(a) + 1) > ArgumentSpace)
        {
            count--; // what doesn't fit is dropped, as ExecPS2's own limit would
        }

        var area = _rdram.AsSpan((int)ArgvAddress, ArgumentSpace);
        area.Clear();
        var text = ArgvAddress + (uint)(count * 4);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(area[(i * 4)..], text);
            var bytes = Encoding.ASCII.GetBytes(all[i]);
            var room = (int)(ArgvAddress + ArgumentSpace - text) - 1;
            bytes.AsSpan(0, Math.Min(bytes.Length, Math.Max(0, room))).CopyTo(_rdram.AsSpan((int)text));
            text += (uint)bytes.Length + 1;
        }

        return count;
    }

    private static ulong SignExtend(uint address) => (ulong)(long)(int)address;
}
