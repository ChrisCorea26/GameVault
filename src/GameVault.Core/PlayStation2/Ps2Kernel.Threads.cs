using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// Threads, the scheduler and semaphores (Play!'s PS2OS.cpp, BSD-2-Clause, for the design; ps2autotests' kernel/ee/thread
// results for the semantics).
public sealed partial class Ps2Kernel
{
    public const int MaxThreads = 256;
    public const int MaxSemaphores = 256;

    /// <summary>The idle "thread": the EE waits in the kernel's idle loop for an interrupt.</summary>
    private const int IdleThread = -1;

    // ReferThreadStatus's status bits (ps2sdk: kernel.h THS_*).
    private const int ThsRun = 0x01;
    private const int ThsReady = 0x02;
    private const int ThsWait = 0x04;
    private const int ThsSuspend = 0x08;
    private const int ThsDormant = 0x10;

    /// <summary>The size of the context frame the kernel keeps below a thread's stack top (Play!: STACKRES).</summary>
    private const uint StackReserve = 0x2A0;
    private const uint StackFrameReserve = 0x20;

    private enum ThreadState
    {
        Ready = 1, // ready, or running (the current thread)
        Sleeping,
        Waiting,
        Suspended,
        SuspendedWaiting,
        SuspendedSleeping,
        Dormant,
    }

    /// <summary>A thread's registers while it isn't running.</summary>
    private sealed class Context
    {
        public readonly UInt128[] Gpr = new UInt128[32];
        public readonly uint[] Fpr = new uint[32];
        public ulong Hi, Lo, Hi1, Lo1;
        public uint Sa, Fcr31, Acc, Pc, Status;

        public void Save(StateWriter writer)
        {
            foreach (var r in Gpr)
            {
                writer.WriteUInt64((ulong)r);
                writer.WriteUInt64((ulong)(r >> 64));
            }

            foreach (var f in Fpr)
            {
                writer.WriteUInt32(f);
            }

            writer.WriteUInt64(Hi);
            writer.WriteUInt64(Lo);
            writer.WriteUInt64(Hi1);
            writer.WriteUInt64(Lo1);
            writer.WriteUInt32(Sa);
            writer.WriteUInt32(Fcr31);
            writer.WriteUInt32(Acc);
            writer.WriteUInt32(Pc);
            writer.WriteUInt32(Status);
        }

        public void Load(StateReader reader)
        {
            for (var i = 0; i < Gpr.Length; i++)
            {
                var low = reader.ReadUInt64();
                Gpr[i] = new UInt128(reader.ReadUInt64(), low);
            }

            for (var i = 0; i < Fpr.Length; i++)
            {
                Fpr[i] = reader.ReadUInt32();
            }

            Hi = reader.ReadUInt64();
            Lo = reader.ReadUInt64();
            Hi1 = reader.ReadUInt64();
            Lo1 = reader.ReadUInt64();
            Sa = reader.ReadUInt32();
            Fcr31 = reader.ReadUInt32();
            Acc = reader.ReadUInt32();
            Pc = reader.ReadUInt32();
            Status = reader.ReadUInt32();
        }
    }

    private sealed class Thread
    {
        public ThreadState State;
        public uint Entry, StackBase, StackSize, Gp, HeapBase, Attr, Option;
        public int InitPriority, Priority, WakeupCount, WaitSemaphore = -1;

        /// <summary>SuspendThread on the running thread: it keeps running, reported as suspended until it is rescheduled.</summary>
        public bool SelfSuspended;
        public readonly Context Context = new();
    }

    private sealed class Semaphore
    {
        public int Count, MaxCount, InitCount;
        public uint Attr, Option;
        public readonly List<int> Waiting = [];
    }

    private readonly Thread?[] _threads = new Thread?[MaxThreads];
    private readonly Semaphore?[] _semaphores = new Semaphore?[MaxSemaphores];
    private readonly List<int> _ready = [];

    /// <summary>The running thread's id; 0 before SetupThread, <see cref="IdleThread"/> while idle.</summary>
    private int _current;

    // ---- Context switching ----

    private void SaveContext(Context c)
    {
        for (var i = 1; i < 32; i++)
        {
            c.Gpr[i] = _cpu.GetGpr(i);
        }

        for (var i = 0; i < 32; i++)
        {
            c.Fpr[i] = _cpu.GetFpr(i);
        }

        c.Hi = _cpu.Hi;
        c.Lo = _cpu.Lo;
        c.Hi1 = _cpu.Hi1;
        c.Lo1 = _cpu.Lo1;
        c.Sa = _cpu.Sa;
        c.Fcr31 = _cpu.Fcr31;
        c.Acc = _cpu.FpuAccumulator;
        c.Pc = _cpu.Pc;
        c.Status = _cpu.Status & (StatusKsuMask | R5900Cpu.StatusIe);
    }

    private void LoadContext(Context c)
    {
        for (var i = 1; i < 32; i++)
        {
            _cpu.SetGpr(i, c.Gpr[i]);
        }

        for (var i = 0; i < 32; i++)
        {
            _cpu.SetFpr(i, c.Fpr[i]);
        }

        _cpu.Hi = c.Hi;
        _cpu.Lo = c.Lo;
        _cpu.Hi1 = c.Hi1;
        _cpu.Lo1 = c.Lo1;
        _cpu.Sa = c.Sa;
        _cpu.Fcr31 = c.Fcr31;
        _cpu.FpuAccumulator = c.Acc;
        _cpu.Status = (_cpu.Status & ~(StatusKsuMask | R5900Cpu.StatusIe)) | c.Status;
        _cpu.Jump(c.Pc);
    }

    /// <summary>
    /// Runs the highest-priority ready thread (the first in the ready queue), or the idle loop when none is ready. Not
    /// while an exception or interrupt is being handled, while interrupts are disabled, or before SetupThread
    /// (Play!: ThreadShakeAndBake).
    /// </summary>
    private void Reschedule()
    {
        var status = _cpu.Status;
        if ((status & (R5900Cpu.StatusExl | R5900Cpu.StatusErl)) != 0
            || (status & (R5900Cpu.StatusIe | R5900Cpu.StatusEie)) != (R5900Cpu.StatusIe | R5900Cpu.StatusEie)
            || _current == 0)
        {
            return;
        }

        if (_current > 0 && _threads[_current] is { } running)
        {
            running.SelfSuspended = false;
        }

        SwitchTo(_ready.Count > 0 ? _ready[0] : IdleThread);
    }

    private void SwitchTo(int id)
    {
        if (id == _current)
        {
            return;
        }

        if (_current > 0 && _threads[_current] is { } old)
        {
            SaveContext(old.Context);
        }

        Enter(id);
    }

    /// <summary>Makes <paramref name="id"/> the running thread, loading its registers (the caller's are already saved).</summary>
    private void Enter(int id)
    {
        _current = id;
        if (id == IdleThread)
        {
            // The idle loop runs in kernel mode with interrupts on, waiting for one to make a thread ready.
            _cpu.Status = (_cpu.Status & ~(StatusKsuMask | R5900Cpu.StatusExl | R5900Cpu.StatusErl))
                | R5900Cpu.StatusIe | R5900Cpu.StatusEie;
            _cpu.Jump(IdleLoop);
            return;
        }

        LoadContext(_threads[id]!.Context);
    }

    /// <summary>Puts a ready thread in the queue after every thread of the same or better (lower) priority.</summary>
    private void Link(int id)
    {
        var priority = _threads[id]!.Priority;
        for (var i = 0; i < _ready.Count; i++)
        {
            if (_threads[_ready[i]]!.Priority > priority)
            {
                _ready.Insert(i, id);
                return;
            }
        }

        _ready.Add(id);
    }

    private void Unlink(int id) => _ready.Remove(id);

    private Thread? GetThread(uint id) => id < MaxThreads ? _threads[id] : null;

    /// <summary>A dormant thread's context as StartThread will begin it (Play!: ThreadReset).</summary>
    private void ResetThread(Thread thread)
    {
        var top = thread.StackBase + thread.StackSize;
        var c = thread.Context;
        Array.Clear(c.Gpr);
        Array.Clear(c.Fpr);
        c.Hi = c.Lo = c.Hi1 = c.Lo1 = 0;
        c.Sa = c.Acc = 0;
        c.Fcr31 = 0;
        c.Gpr[29] = top - StackFrameReserve; // sp
        c.Gpr[30] = top - StackFrameReserve; // fp
        c.Gpr[28] = thread.Gp;
        c.Gpr[31] = ThreadExitTrap; // ra: returning from the thread's function exits it
        c.Pc = thread.Entry;
        c.Status = StatusUser | R5900Cpu.StatusIe;
        thread.Priority = thread.InitPriority;
        thread.WaitSemaphore = -1;
        thread.SelfSuspended = false;
    }

    private int AllocateThread()
    {
        for (var id = 1; id < MaxThreads; id++)
        {
            if (_threads[id] is null)
            {
                return id;
            }
        }

        return -1;
    }

    /// <summary>When no thread is left (all dormant or deleted) the program has ended (Play!: CheckLivingThreads).</summary>
    private void CheckLivingThreads()
    {
        foreach (var thread in _threads)
        {
            if (thread is not null && thread.State != ThreadState.Dormant)
            {
                return;
            }
        }

        ExitProgram(0);
    }

    private void ExitProgram(int code)
    {
        _console.ExitCode = code;
        _console.Exited = true;
        _cpu.StopRequested = true;
    }

    // ---- Thread system calls ----

    /// <summary>SetupThread (60): creates the main thread (id 1, priority 0), copies argc/argv, returns its stack pointer.</summary>
    private void SetupThread()
    {
        var stack = Arg(1);
        var stackSize = Arg(2);
        var args = Arg(3);

        // A stack of -1 goes at the top of RAM, less 4 KB (Play!: "EE kernel seems to subtract that amount too").
        var stackTop = stack == 0xFFFF_FFFF ? Ps2Console.RdramSize - 0x1000u : stack + stackSize;

        // argc, argv[], the strings: argv[0] is the path.
        var count = (uint)_arguments.Count + 1;
        WriteWord(args, count);
        WriteArguments(args + 4, _arguments);

        int id;
        if (_current is 0 or IdleThread)
        {
            id = AllocateThread();
            _threads[id] = new Thread();
        }
        else
        {
            id = _current; // SetupThread from a thread re-uses it (Play!: 007 Nightfire)
            Unlink(id);
        }

        var thread = _threads[id]!;
        thread.State = ThreadState.Ready;
        thread.StackBase = stackTop - stackSize;
        thread.StackSize = stackSize;
        thread.Gp = Arg(0);
        thread.InitPriority = 0;
        thread.Priority = 0;
        thread.Entry = 0;
        Link(id);
        _current = id;
        Return(stackTop - StackReserve);
    }

    /// <summary>SetupHeap (61): the heap's end (its start plus size, or the thread's stack for -1).</summary>
    private void SetupHeap()
    {
        if (CurrentThread is not { } thread)
        {
            Return(Arg(0) + Arg(1));
            return;
        }

        thread.HeapBase = Arg(1) == 0xFFFF_FFFF ? thread.StackBase : Arg(0) + Arg(1);
        Return(thread.HeapBase);
    }

    private Thread? CurrentThread => _current > 0 ? _threads[_current] : null;

    /// <summary>CreateThread (32): the thread is dormant until StartThread. Returns its id.</summary>
    private void CreateThread()
    {
        var param = Arg(0);
        var priority = (int)ReadWord(param + 20);
        var stackSize = ReadWord(param + 12);
        if (priority is < 0 or >= 128 || stackSize < 0x300)
        {
            Return(-1);
            return;
        }

        var id = AllocateThread();
        if (id < 0)
        {
            Return(-1);
            return;
        }

        var thread = new Thread
        {
            State = ThreadState.Dormant,
            Entry = ReadWord(param + 4),
            StackBase = ReadWord(param + 8),
            StackSize = stackSize,
            Gp = ReadWord(param + 16),
            InitPriority = priority,
            Attr = ReadWord(param + 28),
            Option = ReadWord(param + 32),
            HeapBase = CurrentThread?.HeapBase ?? 0,
        };
        ResetThread(thread);
        _threads[id] = thread;
        Return(id);
    }

    /// <summary>DeleteThread (33): only a dormant thread that isn't the caller.</summary>
    private void DeleteThread()
    {
        var id = Arg(0);
        if (id == _current || GetThread(id) is not { State: ThreadState.Dormant })
        {
            Return(-1);
            return;
        }

        _threads[id] = null;
        Return(id);
    }

    /// <summary>StartThread (34): a dormant thread becomes ready with its argument in $a0.</summary>
    private void StartThread()
    {
        var id = Arg(0);
        if (GetThread(id) is not { State: ThreadState.Dormant } thread)
        {
            Return(-1);
            return;
        }

        ResetThread(thread);
        thread.Context.Gpr[4] = Arg(1);
        thread.State = ThreadState.Ready;
        Link((int)id);
        Return(id);
        Reschedule();
    }

    /// <summary>ExitThread (35) / ExitDeleteThread (36): the caller becomes dormant (or is deleted).</summary>
    private void ExitThread(bool delete)
    {
        if (CurrentThread is not { } thread)
        {
            return;
        }

        var id = _current;
        thread.State = ThreadState.Dormant;
        Unlink(id);
        ResetThread(thread);
        if (delete)
        {
            _threads[id] = null;
        }

        // The caller's registers are gone: switch without saving them.
        _current = IdleThread;
        Enter(_ready.Count > 0 ? _ready[0] : IdleThread);
        CheckLivingThreads();
    }

    /// <summary>TerminateThread (37): another thread becomes dormant, wherever it was waiting.</summary>
    private void TerminateThread()
    {
        var id = Arg(0);
        if (id == _current || GetThread(id) is not { } thread || thread.State == ThreadState.Dormant)
        {
            Return(-1);
            return;
        }

        switch (thread.State)
        {
            case ThreadState.Ready:
                Unlink((int)id);
                break;
            case ThreadState.Waiting:
            case ThreadState.SuspendedWaiting:
                RemoveWaiter((int)id, thread);
                break;
        }

        thread.State = ThreadState.Dormant;
        thread.WakeupCount = 0;
        ResetThread(thread);
        Return(id);
    }

    /// <summary>ChangeThreadPriority (41/-42): returns the old priority; a ready thread moves in the queue.</summary>
    private void ChangeThreadPriority(bool fromInterrupt)
    {
        var id = Arg(0) == 0 ? (uint)_current : Arg(0);
        var priority = (int)Arg(1);
        if (GetThread(id) is not { } thread || thread.State == ThreadState.Dormant || priority is < 0 or >= 128)
        {
            Return(-1);
            return;
        }

        var old = thread.Priority;
        thread.Priority = priority;
        if (thread.State == ThreadState.Ready)
        {
            Unlink((int)id);
            Link((int)id);
        }

        Return(old);
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    /// <summary>RotateThreadReadyQueue (43/-44): the first ready thread of that priority goes behind its equals.</summary>
    private void RotateThreadReadyQueue(bool fromInterrupt)
    {
        var priority = (int)Arg(0);
        if (priority is < 0 or >= 128)
        {
            Return(-1);
            return;
        }

        foreach (var id in _ready)
        {
            if (_threads[id]!.Priority == priority)
            {
                Unlink(id);
                Link(id);
                break;
            }
        }

        Return(priority);
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    /// <summary>ReleaseWaitThread (45/-46): a sleeping or waiting thread is released (its wait returns -1).</summary>
    private void ReleaseWaitThread(bool fromInterrupt)
    {
        var id = Arg(0);
        if (id == _current || GetThread(id) is not { } thread)
        {
            Return(-1);
            return;
        }

        switch (thread.State)
        {
            case ThreadState.Sleeping:
                thread.State = ThreadState.Ready;
                Link((int)id);
                break;
            case ThreadState.Waiting:
                RemoveWaiter((int)id, thread);
                thread.Context.Gpr[2] = unchecked((ulong)-1L);
                thread.State = ThreadState.Ready;
                Link((int)id);
                break;
            case ThreadState.SuspendedSleeping:
                thread.State = ThreadState.Suspended;
                break;
            case ThreadState.SuspendedWaiting:
                RemoveWaiter((int)id, thread);
                thread.Context.Gpr[2] = unchecked((ulong)-1L);
                thread.State = ThreadState.Suspended;
                break;
            default:
                Return(-1);
                return;
        }

        Return(id);
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    /// <summary>ReferThreadStatus (48/-49): the status bits, and the ee_thread_status_t when a pointer is given.</summary>
    private void ReferThreadStatus()
    {
        var id = Arg(0) == 0 ? (uint)_current : Arg(0);
        var pointer = Arg(1);
        if (GetThread(id) is not { } thread)
        {
            Return(-1);
            return;
        }

        var status = thread.State switch
        {
            ThreadState.Ready => id == _current ? (thread.SelfSuspended ? ThsSuspend : ThsRun) : ThsReady,
            ThreadState.Sleeping or ThreadState.Waiting => ThsWait,
            ThreadState.Suspended => ThsSuspend,
            ThreadState.SuspendedSleeping or ThreadState.SuspendedWaiting => ThsWait | ThsSuspend,
            _ => ThsDormant,
        };
        var waitType = thread.State switch
        {
            ThreadState.Sleeping or ThreadState.SuspendedSleeping => 1,
            ThreadState.Waiting or ThreadState.SuspendedWaiting => 2,
            _ => 0,
        };

        if (pointer != 0)
        {
            WriteWord(pointer + 0, (uint)status);
            WriteWord(pointer + 4, thread.Entry);
            WriteWord(pointer + 8, thread.StackBase);
            WriteWord(pointer + 12, thread.StackSize);
            WriteWord(pointer + 16, thread.Gp);
            WriteWord(pointer + 20, (uint)thread.InitPriority);
            WriteWord(pointer + 24, (uint)thread.Priority);
            WriteWord(pointer + 28, thread.Attr);
            WriteWord(pointer + 32, thread.Option);
            WriteWord(pointer + 36, (uint)waitType);
            WriteWord(pointer + 40, waitType == 2 ? (uint)thread.WaitSemaphore : 0);
            WriteWord(pointer + 44, (uint)thread.WakeupCount);
        }

        Return(status);
    }

    /// <summary>SleepThread (50): sleeps unless a wakeup is pending (which it uses up). Returns the thread's id.</summary>
    private void SleepThread()
    {
        if (CurrentThread is not { } thread)
        {
            Return(-1);
            return;
        }

        Return(_current);
        if (thread.WakeupCount > 0)
        {
            thread.WakeupCount--;
            return;
        }

        thread.State = ThreadState.Sleeping;
        Unlink(_current);
        Reschedule();
    }

    /// <summary>WakeupThread (51/-52): wakes a sleeping thread, or counts the wakeup.</summary>
    private void WakeupThread(bool fromInterrupt)
    {
        var id = Arg(0);
        if (id == 0 || id == _current || GetThread(id) is not { } thread || thread.State == ThreadState.Dormant)
        {
            Return(-1);
            return;
        }

        Return(id);
        switch (thread.State)
        {
            case ThreadState.Sleeping:
                thread.State = ThreadState.Ready;
                Link((int)id);
                if (!fromInterrupt)
                {
                    Reschedule();
                }

                break;
            case ThreadState.SuspendedSleeping:
                thread.State = ThreadState.Suspended;
                break;
            default:
                thread.WakeupCount++;
                break;
        }
    }

    /// <summary>CancelWakeupThread (53/-54): clears the pending wakeups; returns how many there were.</summary>
    private void CancelWakeupThread()
    {
        var id = Arg(0) == 0 ? (uint)_current : Arg(0);
        if (GetThread(id) is not { } thread)
        {
            Return(-1);
            return;
        }

        Return(thread.WakeupCount);
        thread.WakeupCount = 0;
    }

    /// <summary>SuspendThread (55/-56). The running thread may suspend itself: it carries on until rescheduled.</summary>
    private void SuspendThread(bool fromInterrupt)
    {
        var id = Arg(0);
        if (GetThread(id) is not { } thread)
        {
            Return(-1);
            return;
        }

        if (id == _current)
        {
            if (thread.SelfSuspended)
            {
                Return(-1);
                return;
            }

            thread.SelfSuspended = true;
            Return(id);
            return;
        }

        switch (thread.State)
        {
            case ThreadState.Ready:
                thread.State = ThreadState.Suspended;
                Unlink((int)id);
                break;
            case ThreadState.Waiting:
                thread.State = ThreadState.SuspendedWaiting;
                break;
            case ThreadState.Sleeping:
                thread.State = ThreadState.SuspendedSleeping;
                break;
            default:
                Return(-1);
                return;
        }

        Return(id);
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    /// <summary>ResumeThread (57/-58): a suspended thread carries on with what it was doing.</summary>
    private void ResumeThread(bool fromInterrupt)
    {
        var id = Arg(0);
        if (id == _current || GetThread(id) is not { } thread)
        {
            Return(-1);
            return;
        }

        switch (thread.State)
        {
            case ThreadState.Suspended:
                thread.State = ThreadState.Ready;
                Link((int)id);
                break;
            case ThreadState.SuspendedWaiting:
                thread.State = ThreadState.Waiting;
                break;
            case ThreadState.SuspendedSleeping:
                thread.State = ThreadState.Sleeping;
                break;
            default:
                Return(-1);
                return;
        }

        Return(id);
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    // ---- Semaphores ----

    /// <summary>CreateSema (64): from an ee_sema_t (count, max_count, init_count, wait_threads, attr, option).</summary>
    private void CreateSema()
    {
        var param = Arg(0);
        var initCount = (int)ReadWord(param + 8);
        var maxCount = (int)ReadWord(param + 4);
        if (initCount < 0 || maxCount <= 0)
        {
            Return(-1);
            return;
        }

        for (var id = 0; id < MaxSemaphores; id++)
        {
            if (_semaphores[id] is null)
            {
                _semaphores[id] = new Semaphore
                {
                    Count = initCount,
                    InitCount = initCount,
                    MaxCount = maxCount,
                    Attr = ReadWord(param + 16),
                    Option = ReadWord(param + 20),
                };
                Return(id);
                return;
            }
        }

        Return(-1);
    }

    private Semaphore? GetSemaphore(uint id) => id < MaxSemaphores ? _semaphores[id] : null;

    /// <summary>DeleteSema (65/-73): the waiting threads are released, their WaitSema returning -1.</summary>
    private void DeleteSema(bool fromInterrupt)
    {
        var id = Arg(0);
        if (GetSemaphore(id) is not { } sema)
        {
            Return(-1);
            return;
        }

        Return(id);
        while (sema.Waiting.Count > 0)
        {
            ReleaseWaiter(sema, -1);
        }

        _semaphores[id] = null;
        if (!fromInterrupt)
        {
            Reschedule();
        }
    }

    /// <summary>SignalSema (66/-67): wakes the first waiting thread, or counts up (to the maximum).</summary>
    private void SignalSema(bool fromInterrupt)
    {
        var id = Arg(0);
        if (GetSemaphore(id) is not { } sema)
        {
            Return(-1);
            return;
        }

        Return(id);
        if (sema.Waiting.Count > 0)
        {
            ReleaseWaiter(sema, (int)id);
            if (!fromInterrupt)
            {
                Reschedule();
            }
        }
        else if (sema.Count < sema.MaxCount)
        {
            sema.Count++;
        }
    }

    /// <summary>WaitSema (68): takes the semaphore, or waits for it. Returns its id (-1 if it is deleted meanwhile).</summary>
    private void WaitSema()
    {
        var id = Arg(0);
        if (GetSemaphore(id) is not { } sema)
        {
            Return(-1);
            return;
        }

        Return(id);
        if (sema.Count > 0)
        {
            sema.Count--;
            return;
        }

        if (CurrentThread is not { } thread)
        {
            return; // no thread to block (before SetupThread): the wait can't happen
        }

        thread.State = ThreadState.Waiting;
        thread.WaitSemaphore = (int)id;
        thread.SelfSuspended = false;
        sema.Waiting.Add(_current);
        Unlink(_current);
        Reschedule();
    }

    /// <summary>PollSema (69/-70): takes the semaphore if it can; -1 otherwise.</summary>
    private void PollSema()
    {
        var id = Arg(0);
        if (GetSemaphore(id) is not { } sema || sema.Count == 0)
        {
            Return(-1);
            return;
        }

        sema.Count--;
        Return(id);
    }

    /// <summary>ReferSemaStatus (71/-72): fills an ee_sema_t.</summary>
    private void ReferSemaStatus()
    {
        var id = Arg(0);
        var pointer = Arg(1);
        if (GetSemaphore(id) is not { } sema)
        {
            Return(-1);
            return;
        }

        WriteWord(pointer + 0, (uint)sema.Count);
        WriteWord(pointer + 4, (uint)sema.MaxCount);
        WriteWord(pointer + 8, (uint)sema.InitCount);
        WriteWord(pointer + 12, (uint)sema.Waiting.Count);
        WriteWord(pointer + 16, sema.Attr);
        WriteWord(pointer + 20, sema.Option);
        Return(id);
    }

    /// <summary>Releases the first thread waiting on <paramref name="sema"/>, its WaitSema returning <paramref name="result"/>.</summary>
    private void ReleaseWaiter(Semaphore sema, int result)
    {
        var id = sema.Waiting[0];
        sema.Waiting.RemoveAt(0);
        var thread = _threads[id]!;
        thread.WaitSemaphore = -1;
        thread.Context.Gpr[2] = (ulong)(long)result;
        if (thread.State == ThreadState.Waiting)
        {
            thread.State = ThreadState.Ready;
            Link(id);
        }
        else
        {
            thread.State = ThreadState.Suspended;
        }
    }

    private void RemoveWaiter(int id, Thread thread)
    {
        if (thread.WaitSemaphore >= 0 && _semaphores[thread.WaitSemaphore] is { } sema)
        {
            sema.Waiting.Remove(id);
        }

        thread.WaitSemaphore = -1;
    }

    // ---- State ----

    private void SaveThreads(StateWriter writer)
    {
        for (var id = 0; id < MaxThreads; id++)
        {
            var t = _threads[id];
            writer.WriteBool(t is not null);
            if (t is null)
            {
                continue;
            }

            writer.WriteInt32((int)t.State);
            writer.WriteUInt32(t.Entry);
            writer.WriteUInt32(t.StackBase);
            writer.WriteUInt32(t.StackSize);
            writer.WriteUInt32(t.Gp);
            writer.WriteUInt32(t.HeapBase);
            writer.WriteUInt32(t.Attr);
            writer.WriteUInt32(t.Option);
            writer.WriteInt32(t.InitPriority);
            writer.WriteInt32(t.Priority);
            writer.WriteInt32(t.WakeupCount);
            writer.WriteInt32(t.WaitSemaphore);
            writer.WriteBool(t.SelfSuspended);
            t.Context.Save(writer);
        }

        for (var id = 0; id < MaxSemaphores; id++)
        {
            var s = _semaphores[id];
            writer.WriteBool(s is not null);
            if (s is null)
            {
                continue;
            }

            writer.WriteInt32(s.Count);
            writer.WriteInt32(s.MaxCount);
            writer.WriteInt32(s.InitCount);
            writer.WriteUInt32(s.Attr);
            writer.WriteUInt32(s.Option);
            WriteList(writer, s.Waiting);
        }

        WriteList(writer, _ready);
        writer.WriteInt32(_current);
    }

    private void LoadThreads(StateReader reader)
    {
        for (var id = 0; id < MaxThreads; id++)
        {
            if (!reader.ReadBool())
            {
                _threads[id] = null;
                continue;
            }

            var t = new Thread
            {
                State = (ThreadState)reader.ReadInt32(),
                Entry = reader.ReadUInt32(),
                StackBase = reader.ReadUInt32(),
                StackSize = reader.ReadUInt32(),
                Gp = reader.ReadUInt32(),
                HeapBase = reader.ReadUInt32(),
                Attr = reader.ReadUInt32(),
                Option = reader.ReadUInt32(),
                InitPriority = reader.ReadInt32(),
                Priority = reader.ReadInt32(),
                WakeupCount = reader.ReadInt32(),
                WaitSemaphore = reader.ReadInt32(),
                SelfSuspended = reader.ReadBool(),
            };
            t.Context.Load(reader);
            _threads[id] = t;
        }

        for (var id = 0; id < MaxSemaphores; id++)
        {
            if (!reader.ReadBool())
            {
                _semaphores[id] = null;
                continue;
            }

            var s = new Semaphore
            {
                Count = reader.ReadInt32(),
                MaxCount = reader.ReadInt32(),
                InitCount = reader.ReadInt32(),
                Attr = reader.ReadUInt32(),
                Option = reader.ReadUInt32(),
            };
            ReadList(reader, s.Waiting);
            _semaphores[id] = s;
        }

        ReadList(reader, _ready);
        _current = reader.ReadInt32();
    }

    private static void WriteList(StateWriter writer, List<int> list)
    {
        writer.WriteInt32(list.Count);
        foreach (var value in list)
        {
            writer.WriteInt32(value);
        }
    }

    private static void ReadList(StateReader reader, List<int> list)
    {
        list.Clear();
        var count = reader.ReadInt32();
        if (count < 0 || count > 4096)
        {
            throw new StateFormatException("Kernel list size out of range.");
        }

        for (var i = 0; i < count; i++)
        {
            list.Add(reader.ReadInt32());
        }
    }
}
