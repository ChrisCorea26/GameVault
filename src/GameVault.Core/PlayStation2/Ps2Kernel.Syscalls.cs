using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// The system call table (ps2sdk's syscallnr.h names them; Play!'s PS2OS.cpp for the behaviour).
public sealed partial class Ps2Kernel
{
    /// <summary>SetSyscall's table: the program's own system calls, run in kernel mode (0 = the kernel's own).</summary>
    private readonly uint[] _customSyscalls = new uint[256];

    // SetVSyncFlag's pointers, written at the next V-blank.
    private uint _vsyncFlag;
    private uint _vsyncCsr;

    /// <summary>SifSetReg's kernel registers ($80000000 + n), which the SIF libraries keep their state in.</summary>
    private readonly uint[] _sifSoftRegisters = new uint[32];

    private void ResetState()
    {
        Array.Clear(_threads);
        Array.Clear(_semaphores);
        _ready.Clear();
        _current = 0;
        Array.Clear(_customSyscalls);
        _customReturns.Clear();
        Array.Clear(_sifSoftRegisters);
        _vsyncFlag = _vsyncCsr = 0;
        _intcHandlers.Clear();
        _dmacHandlers.Clear();
        _inInterrupt = false;
        _alarms.Clear();
        _sifQueue.Clear();
        _sifActive = 0;
        _sifNextId = 1;
        _sifCompleted = 0;
        Fault = null;
    }

    private void HandleSyscall()
    {
        var raw = (int)_cpu.GetGpr64(3);
        var fromInterrupt = raw < 0;
        var number = Math.Abs(raw);
        SyscallTrace?.Invoke(raw, _cpu.CurrentPc);

        if (number < _customSyscalls.Length && _customSyscalls[number] != 0)
        {
            CallCustomSyscall(_customSyscalls[number]);
            return;
        }

        switch (number)
        {
            case 0x02: SetGsCrt(); break;
            case 0x04: ExitProgram((int)Arg(0)); break;
            case 0x06: LoadExecPs2(); break;
            case 0x07: ExecPs2(); break;
            case 0x0D or 0x0E or 0x0F: Return(Arg(1)); break; // SetVTLBRefillHandler, SetVCommonHandler, SetVInterruptHandler
            case 0x10: AddHandler(_intcHandlers, (int)Arg(0)); break;
            case 0x11: RemoveHandler(_intcHandlers, (int)Arg(0)); break;
            case 0x12: AddHandler(_dmacHandlers, (int)Arg(0)); break;
            case 0x13: RemoveHandler(_dmacHandlers, (int)Arg(0)); break;
            case 0x14 or 0x1A: EnableIntc(true); break;
            case 0x15 or 0x1B: EnableIntc(false); break;
            case 0x16 or 0x1C: EnableDmac(true); break;
            case 0x17 or 0x1D: EnableDmac(false); break;
            case 0x18 or 0x1E: SetAlarm(); break;
            case 0x19 or 0x1F: ReleaseAlarm(); break;
            case 0x20: CreateThread(); break;
            case 0x21: DeleteThread(); break;
            case 0x22: StartThread(); break;
            case 0x23: ExitThread(delete: false); break;
            case 0x24: ExitThread(delete: true); break;
            case 0x25 or 0x26: TerminateThread(); break;
            case 0x29 or 0x2A: ChangeThreadPriority(fromInterrupt || number == 0x2A); break;
            case 0x2B or 0x2C: RotateThreadReadyQueue(fromInterrupt || number == 0x2C); break;
            case 0x2D or 0x2E: ReleaseWaitThread(fromInterrupt || number == 0x2E); break;
            case 0x2F: Return(_current > 0 ? _current : 0); break; // GetThreadId
            case 0x30 or 0x31: ReferThreadStatus(); break;
            case 0x32: SleepThread(); break;
            case 0x33 or 0x34: WakeupThread(fromInterrupt || number == 0x34); break;
            case 0x35 or 0x36: CancelWakeupThread(); break;
            case 0x37 or 0x38: SuspendThread(fromInterrupt || number == 0x38); break;
            case 0x39 or 0x3A: ResumeThread(fromInterrupt || number == 0x3A); break;
            case 0x3C: SetupThread(); break;
            case 0x3D: SetupHeap(); break;
            case 0x3E: Return(CurrentThread?.HeapBase ?? 0); break; // EndOfHeap
            case 0x40: CreateSema(); break;
            case 0x41 or 0x49: DeleteSema(fromInterrupt || number == 0x49); break;
            case 0x42 or 0x43: SignalSema(fromInterrupt || number == 0x43); break;
            case 0x44: WaitSema(); break;
            case 0x45 or 0x46: PollSema(); break;
            case 0x47 or 0x48: ReferSemaStatus(); break;
            case 0x4B: GetOsdConfigParam(); break;
            case 0x63 or 0x67: Return(_cpu.ReadCop0((int)(Arg(0) & 31))); break; // GetCop0
            case 0x64 or 0x68: Return(0); break; // FlushCache: no cache is modelled
            case 0x6B: Return(0); break; // SifStopDma
            case 0x70: Return((uint)_console.Gs.Imr); break; // GsGetIMR
            case 0x71: GsPutImr(); break;
            case 0x73: SetVSyncFlag(); break;
            case 0x74: SetSyscall(); break;
            case 0x76: SifDmaStat(); break;
            case 0x77: SifSetDma(); break;
            case 0x78: SifSetDChain(); break;
            case 0x79: SifSetReg(); break;
            case 0x7A: SifGetReg(); break;
            case 0x7C: Deci2Call(); break;
            case 0x7E: Return(0x100); break; // MachineType
            case 0x7F: Return(Ps2Console.RdramSize); break; // GetMemorySize
            default:
                UnknownSyscalls.Add(raw);
                Return(0);
                break;
        }
    }

    /// <summary>
    /// A system call the program installed with SetSyscall: the kernel runs it in kernel mode, returning through ERET to
    /// the instruction after the SYSCALL.
    /// </summary>
    private void CallCustomSyscall(uint address)
    {
        _customReturns.Add((_cpu.Pc, _cpu.GetGpr(31), _cpu.Status & R5900Cpu.StatusExl));
        _cpu.Status |= R5900Cpu.StatusExl;
        _cpu.SetGpr64(31, CustomReturnTrap);
        _cpu.Jump(address);
    }

    /// <summary>A custom system call returned: back to the caller with its $ra, as the kernel's ERET does.</summary>
    private void CustomSyscallReturned()
    {
        if (_customReturns.Count == 0)
        {
            RaiseFault("a return from a custom system call that wasn't called");
            return;
        }

        var (pc, ra, exl) = _customReturns[^1];
        _customReturns.RemoveAt(_customReturns.Count - 1);
        _cpu.SetGpr(31, ra);
        _cpu.Status = (_cpu.Status & ~R5900Cpu.StatusExl) | exl;
        _cpu.Jump(pc);
    }

    /// <summary>The custom system calls in progress: where each returns, the caller's $ra and EXL.</summary>
    private readonly List<(uint Pc, UInt128 Ra, uint Exl)> _customReturns = [];

    /// <summary>SetSyscall (116): installs (or with 0, removes) a system call.</summary>
    private void SetSyscall()
    {
        var number = Arg(0);
        if (number < _customSyscalls.Length)
        {
            _customSyscalls[number] = Arg(1);
        }

        Return(0);
    }

    // ---- Program loading ----

    /// <summary>ExecPS2 (7): drops the threads and handlers and starts the code at $a0 with the given gp and arguments.</summary>
    private void ExecPs2()
    {
        var entry = Arg(0);
        var gp = Arg(1);
        var arguments = ReadArguments((int)Arg(2), Arg(3));
        RestartWith(null, entry, gp, arguments);
    }

    /// <summary>
    /// LoadExecPS2 (6): without the IOP's file system the kernel can only re-load the program it booted, which it does
    /// with the new arguments (a program restarting itself); other files end the program.
    /// </summary>
    private void LoadExecPs2()
    {
        var path = ReadString(Arg(0));
        var arguments = ReadArguments((int)Arg(1), Arg(2));
        if (_console.Elf is not { } elf || !SameFile(path, _path))
        {
            RaiseFault($"LoadExecPS2(\"{path}\"): the file isn't available without the BIOS");
            return;
        }

        _path = path;
        _arguments = arguments;
        RestartWith(elf, elf.Entry, 0, arguments);
    }

    private static bool SameFile(string a, string b)
    {
        static string Name(string path) => path[(path.LastIndexOfAny([':', '/', '\\']) + 1)..].TrimEnd(';', '1').ToUpperInvariant();
        return Name(a) == Name(b);
    }

    private List<string> ReadArguments(int count, uint argv)
    {
        var arguments = new List<string>();
        for (var i = 0; i < count && i < 64; i++)
        {
            arguments.Add(ReadString(ReadWord(argv + (uint)i * 4)));
        }

        return arguments;
    }

    private void RestartWith(Ps2Elf? elf, uint entry, uint gp, IReadOnlyList<string> arguments)
    {
        _arguments = arguments;
        Array.Clear(_threads);
        Array.Clear(_semaphores);
        _ready.Clear();
        _current = 0;
        _intcHandlers.Clear();
        _dmacHandlers.Clear();
        Array.Clear(_customSyscalls);
        _console.Intc.Write(Ps2Intc.MaskAddress, _console.Intc.Mask); // every interrupt masked
        StartProgram(elf, entry, gp, arguments);
    }

    // ---- The GS ----

    /// <summary>
    /// SetGsCrt (2): the video mode as the BIOS programs it — NTSC (2) or PAL (3), interlaced or not, field or frame —
    /// through SMODE1, SMODE2 and the sync registers (values from the Linux PS2 port's gs-registers, as GameVault's
    /// homebrew README lists them for NTSC).
    /// </summary>
    private void SetGsCrt()
    {
        var interlaced = Arg(0) != 0;
        var mode = Arg(1);
        var frameMode = Arg(2) != 0;
        var gs = _console.Gs;
        var pal = mode == 3;
        gs.WritePrivileged(0x10, pal ? 0x7_4083_6504UL : 0x7_4083_4504UL); // SMODE1, PRST held
        gs.WritePrivileged(0x10, pal ? 0x7_4080_6504UL : 0x7_4080_4504UL);
        gs.WritePrivileged(0x20, (interlaced ? 1UL : 0) | (frameMode ? 2UL : 0)); // SMODE2: INT, FFMD
        gs.WritePrivileged(0x30, 8); // SRFSH
        gs.WritePrivileged(0x40, pal ? 0x0007_F5B6_1F06_F040UL : 0x0007_F5B6_1F06_F040UL); // SYNCH1
        gs.WritePrivileged(0x50, pal ? 0x33A4D8UL : 0x33A4D8UL); // SYNCH2
        gs.WritePrivileged(0x60, pal ? 0x00A7_0005_0260_0601UL : 0x00C7_8006_01A0_1801UL); // SYNCV
        Return(0);
    }

    /// <summary>GsPutIMR (113): the GS's interrupt mask.</summary>
    private void GsPutImr()
    {
        _console.Gs.WritePrivileged(0x1010, Arg(0));
        Return(0);
    }

    /// <summary>SetVSyncFlag (115): at the next V-blank the kernel writes 1 to the first address and CSR to the second.</summary>
    private void SetVSyncFlag()
    {
        _vsyncFlag = Arg(0);
        _vsyncCsr = Arg(1);
        Return(0);
    }

    /// <summary>The console's V-blank: SetVSyncFlag's flags.</summary>
    internal void VBlankStart()
    {
        if (_vsyncFlag != 0)
        {
            WriteWord(_vsyncFlag, 1);
        }

        if (_vsyncCsr != 0)
        {
            var csr = _console.Gs.Csr;
            WriteWord(_vsyncCsr, (uint)csr);
            WriteWord(_vsyncCsr + 4, (uint)(csr >> 32));
        }

        _vsyncFlag = _vsyncCsr = 0;
    }

    /// <summary>GetOsdConfigParam (75): English, 4:3, the version-2 extended config (Play!).</summary>
    private void GetOsdConfigParam()
    {
        WriteWord(Arg(0), (1u << 4) | (2u << 13) | (1u << 16));
        Return(0);
    }

    // ---- Deci2Call (124): the debugger's TTY ----

    private void Deci2Call()
    {
        var function = Arg(0);
        var param = Arg(1);
        switch (function)
        {
            case 0x01: // Deci2Open: one channel
                Return(1);
                break;
            case 0x03: // Deci2Send: the text after the 12-byte header goes to the TTY
            {
                var buffer = ReadWord(param + 4);
                var data = buffer != 0 ? ReadWord(buffer + 16) : 0;
                if (data != 0)
                {
                    var size = ReadWord(data);
                    for (uint i = 12; i < size; i++)
                    {
                        _console.DebugOutput?.Invoke((char)At(data + i, 1)[0]);
                    }

                    WriteWord(buffer + 4, 0);
                }

                Return(1);
                break;
            }

            case 0x04: // Deci2Poll
                Return(1);
                break;
            case 0x10: // kPuts
                foreach (var c in ReadString(ReadWord(param), 65536))
                {
                    _console.DebugOutput?.Invoke(c);
                }

                Return(0);
                break;
            default:
                Return(0);
                break;
        }
    }

    // ---- State ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        writer.WriteBool(Fault is not null);
        writer.WriteUInt32(_vsyncFlag);
        writer.WriteUInt32(_vsyncCsr);
        foreach (var value in _customSyscalls)
        {
            writer.WriteUInt32(value);
        }

        foreach (var value in _sifSoftRegisters)
        {
            writer.WriteUInt32(value);
        }

        writer.WriteInt32(_customReturns.Count);
        foreach (var (pc, ra, exl) in _customReturns)
        {
            writer.WriteUInt32(pc);
            writer.WriteUInt64((ulong)ra);
            writer.WriteUInt64((ulong)(ra >> 64));
            writer.WriteUInt32(exl);
        }

        writer.WriteInt32(_arguments.Count);
        foreach (var argument in _arguments)
        {
            WriteString(writer, argument);
        }

        WriteString(writer, _path);
        SaveThreads(writer);
        SaveInterrupts(writer);
        SaveSif(writer);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported PS2 kernel state version.");
        }

        Fault = reader.ReadBool() ? "a fault (restored from a save state)" : null;
        _vsyncFlag = reader.ReadUInt32();
        _vsyncCsr = reader.ReadUInt32();
        for (var i = 0; i < _customSyscalls.Length; i++)
        {
            _customSyscalls[i] = reader.ReadUInt32();
        }

        for (var i = 0; i < _sifSoftRegisters.Length; i++)
        {
            _sifSoftRegisters[i] = reader.ReadUInt32();
        }

        var returns = reader.ReadInt32();
        if (returns is < 0 or > 64)
        {
            throw new StateFormatException("Too many nested system calls.");
        }

        _customReturns.Clear();
        for (var i = 0; i < returns; i++)
        {
            var pc = reader.ReadUInt32();
            var low = reader.ReadUInt64();
            var ra = new UInt128(reader.ReadUInt64(), low);
            _customReturns.Add((pc, ra, reader.ReadUInt32()));
        }

        var count = reader.ReadInt32();
        if (count is < 0 or > 64)
        {
            throw new StateFormatException("Too many program arguments.");
        }

        var arguments = new List<string>();
        for (var i = 0; i < count; i++)
        {
            arguments.Add(ReadString(reader));
        }

        _arguments = arguments;
        _path = ReadString(reader);
        LoadThreads(reader);
        LoadInterrupts(reader);
        LoadSif(reader);
        reader.EndChunk();
    }

    private static void WriteString(StateWriter writer, string value)
    {
        writer.WriteInt32(value.Length);
        foreach (var c in value)
        {
            writer.WriteUInt16(c);
        }
    }

    private static string ReadString(StateReader reader)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > 4096)
        {
            throw new StateFormatException("String too long.");
        }

        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)reader.ReadUInt16();
        }

        return new string(chars);
    }
}
