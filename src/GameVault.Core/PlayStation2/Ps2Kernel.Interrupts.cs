using GameVault.Core.Cpu.R5900;
using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// Interrupt handlers: AddIntcHandler/AddDmacHandler's queues and their dispatch (Play!'s PS2OS.cpp, BSD-2-Clause:
// AssembleInterruptHandler, AssembleIntcHandler, AssembleDmacHandler; ps2autotests' kernel/ee/interrupt results).
public sealed partial class Ps2Kernel
{
    private const int MaxHandlers = 128;

    private sealed class Handler
    {
        public int Id;
        public int Cause;
        public uint Address;
        public uint Arg;
        public uint Gp;
    }

    // The queues in calling order (AddXxxHandler's "next": 0 puts a handler first, -1 last, an id before that handler).
    private readonly List<Handler> _intcHandlers = [];
    private readonly List<Handler> _dmacHandlers = [];

    // The interrupt being dispatched: the interrupted code's registers, where it resumes, the causes still to handle,
    // and the handlers still to call for the current cause.
    private bool _inInterrupt;
    private readonly Context _interrupted = new();
    private uint _intcPending;
    private uint _dmacPending;
    private bool _causeIsDmac;
    private int _cause;
    /// <summary>The handlers still to call for the current cause: handler ids, or alarm ids negated.</summary>
    private readonly List<int> _callQueue = [];
    private bool _callingAlarm;

    // ---- Handler registration ----

    /// <summary>AddIntcHandler / AddDmacHandler (16/18): (cause, handler, next, arg); returns the handler's id.</summary>
    private void AddHandler(List<Handler> list, int cause)
    {
        var id = 1;
        while (id <= MaxHandlers && list.Exists(h => h.Id == id))
        {
            id++;
        }

        if (id > MaxHandlers)
        {
            Return(-1);
            return;
        }

        var handler = new Handler { Id = id, Cause = cause, Address = Arg(1), Arg = Arg(3), Gp = (uint)_cpu.GetGpr64(28) };
        var next = (int)Arg(2);
        if (next == 0)
        {
            list.Insert(0, handler);
        }
        else
        {
            var before = next == -1 ? -1 : list.FindIndex(h => h.Id == next);
            if (before < 0)
            {
                list.Add(handler);
            }
            else
            {
                list.Insert(before, handler);
            }
        }

        Return(id);
    }

    /// <summary>RemoveIntcHandler / RemoveDmacHandler (17/19): returns how many handlers the cause has left.</summary>
    private void RemoveHandler(List<Handler> list, int cause)
    {
        var id = (int)Arg(1);
        var index = list.FindIndex(h => h.Id == id);
        if (index < 0)
        {
            Return(-1);
            return;
        }

        list.RemoveAt(index);
        Return(list.FindAll(h => h.Cause == cause).Count);
    }

    /// <summary>EnableIntc / DisableIntc (20/21 and their i-forms): returns 1 if the mask changed.</summary>
    private void EnableIntc(bool enable)
    {
        var bit = 1u << (int)(Arg(0) & 31);
        var intc = _console.Intc;
        var enabled = (intc.Mask & bit) != 0;
        if (enabled != enable && (bit & Ps2Intc.ValidBits) != 0)
        {
            intc.Write(Ps2Intc.MaskAddress, bit); // writing 1 toggles the mask bit
            _console.UpdateInterruptLines();
            Return(1);
        }
        else
        {
            Return(0);
        }
    }

    /// <summary>EnableDmac / DisableDmac (22/23 and their i-forms): D_STAT's mask bit; returns 1 if it changed.</summary>
    private void EnableDmac(bool enable)
    {
        var bit = 0x1_0000u << (int)(Arg(0) & 15);
        var dmac = _console.Dmac;
        var enabled = (dmac.Stat & bit) != 0;
        if (enabled != enable)
        {
            dmac.WriteRegister(0x1000_E010, bit); // writing 1 toggles the mask bit
            _console.UpdateInterruptLines();
            Return(1);
        }
        else
        {
            Return(0);
        }
    }

    // ---- Dispatch ----

    /// <summary>
    /// The interrupt exception: the interrupted code's registers are saved, then each raised and enabled INTC line (from
    /// line 0 up) and DMAC channel (from the highest down, as Play!'s kernel does) has its status bit cleared and its
    /// handlers called in queue order, until one returns a negative value. Handlers run in kernel mode with
    /// interrupts off, on the kernel's stack: <c>handler(cause, arg, 0)</c> with the gp they were added with.
    /// </summary>
    private bool BeginInterrupt()
    {
        var intc = _console.Intc;
        var dmac = _console.Dmac;
        var intcPending = _cpu.Int0 ? intc.Stat & intc.Mask : 0;
        var dmacPending = _cpu.Int1 ? dmac.Stat & (dmac.Stat >> 16) & 0x63FF : 0;
        if (intcPending == 0 && dmacPending == 0)
        {
            return true; // nothing to do (the line dropped): carry on
        }

        // Where the interrupted code resumes: an instruction in a delay slot re-runs its branch.
        SaveContext(_interrupted);
        _interrupted.Pc = _cpu.InDelaySlot ? _cpu.CurrentPc - 4 : _cpu.CurrentPc;
        _interrupted.Status = _cpu.Status;
        _inInterrupt = true;
        _intcPending = intcPending;
        _dmacPending = dmacPending;
        _callQueue.Clear();
        _cpu.Status = (_cpu.Status | R5900Cpu.StatusExl) & ~R5900Cpu.StatusIe;
        CallNextHandler();
        return true;
    }

    /// <summary>A handler returned to the kernel's trap: its $v0 decides whether the cause's other handlers run.</summary>
    private void HandlerReturned()
    {
        if (!_inInterrupt)
        {
            RaiseFault("a return to the interrupt handler trap outside an interrupt");
            return;
        }

        if (!_callingAlarm && (int)_cpu.GetGpr64(2) < 0)
        {
            _callQueue.Clear(); // a negative result stops the cause's chain (an alarm's result means nothing)
        }

        CallNextHandler();
    }

    private void CallNextHandler()
    {
        while (true)
        {
            while (_callQueue.Count > 0)
            {
                var id = _callQueue[0];
                _callQueue.RemoveAt(0);
                if (id < 0)
                {
                    if (CallAlarm(-id))
                    {
                        return;
                    }

                    continue;
                }

                _callingAlarm = false;
                var list = _causeIsDmac ? _dmacHandlers : _intcHandlers;
                var handler = list.Find(h => h.Id == id);
                if (handler is null)
                {
                    continue; // removed by an earlier handler
                }

                _cpu.SetGpr64(4, (ulong)_cause);
                _cpu.SetGpr64(5, handler.Arg);
                _cpu.SetGpr64(6, 0);
                _cpu.SetGpr64(28, handler.Gp);
                _cpu.SetGpr64(29, InterruptStackTop - 0x40);
                _cpu.SetGpr64(31, HandlerReturnTrap);
                _cpu.Jump(handler.Address);
                return;
            }

            if (!NextCause())
            {
                EndInterrupt();
                return;
            }
        }
    }

    /// <summary>Takes the next cause: clears its status bit and queues its handlers. False when none is left.</summary>
    private bool NextCause()
    {
        if (_intcPending != 0)
        {
            var line = System.Numerics.BitOperations.TrailingZeroCount(_intcPending);
            _intcPending &= ~(1u << line);
            _console.Intc.Write(Ps2Intc.StatAddress, 1u << line);
            QueueHandlers(_intcHandlers, line, dmac: false);
            if (line == (int)Ps2Interrupt.Timer3)
            {
                QueueDueAlarms();
            }

            return true;
        }

        if (_dmacPending != 0)
        {
            var channel = 31 - System.Numerics.BitOperations.LeadingZeroCount(_dmacPending);
            _dmacPending &= ~(1u << channel);
            _console.Dmac.WriteRegister(0x1000_E010, 1u << channel);
            QueueHandlers(_dmacHandlers, channel, dmac: true);
            return true;
        }

        return false;
    }

    private void QueueHandlers(List<Handler> list, int cause, bool dmac)
    {
        _causeIsDmac = dmac;
        _cause = cause;
        _callQueue.Clear();
        foreach (var handler in list)
        {
            if (handler.Cause == cause)
            {
                _callQueue.Add(handler.Id);
            }
        }
    }

    /// <summary>Every handler has run: the interrupted code resumes (interrupts on), then the scheduler picks a thread.</summary>
    private void EndInterrupt()
    {
        _inInterrupt = false;
        var resume = _interrupted.Pc;
        LoadContext(_interrupted);
        _cpu.Status = (_interrupted.Status & ~(R5900Cpu.StatusExl | R5900Cpu.StatusErl)) | R5900Cpu.StatusIe;
        _cpu.Jump(resume);
        _console.UpdateInterruptLines();
        Reschedule();
    }

    // ---- State ----

    private void SaveInterrupts(StateWriter writer)
    {
        SaveHandlers(writer, _intcHandlers);
        SaveHandlers(writer, _dmacHandlers);
        writer.WriteBool(_inInterrupt);
        _interrupted.Save(writer);
        writer.WriteUInt32(_intcPending);
        writer.WriteUInt32(_dmacPending);
        writer.WriteBool(_causeIsDmac);
        writer.WriteInt32(_cause);
        WriteList(writer, _callQueue);
        writer.WriteBool(_callingAlarm);
        SaveAlarms(writer);
    }

    private void LoadInterrupts(StateReader reader)
    {
        LoadHandlers(reader, _intcHandlers);
        LoadHandlers(reader, _dmacHandlers);
        _inInterrupt = reader.ReadBool();
        _interrupted.Load(reader);
        _intcPending = reader.ReadUInt32();
        _dmacPending = reader.ReadUInt32();
        _causeIsDmac = reader.ReadBool();
        _cause = reader.ReadInt32();
        ReadList(reader, _callQueue);
        _callingAlarm = reader.ReadBool();
        LoadAlarms(reader);
    }

    private static void SaveHandlers(StateWriter writer, List<Handler> list)
    {
        writer.WriteInt32(list.Count);
        foreach (var h in list)
        {
            writer.WriteInt32(h.Id);
            writer.WriteInt32(h.Cause);
            writer.WriteUInt32(h.Address);
            writer.WriteUInt32(h.Arg);
            writer.WriteUInt32(h.Gp);
        }
    }

    private static void LoadHandlers(StateReader reader, List<Handler> list)
    {
        list.Clear();
        var count = reader.ReadInt32();
        if (count is < 0 or > MaxHandlers)
        {
            throw new StateFormatException("Too many interrupt handlers.");
        }

        for (var i = 0; i < count; i++)
        {
            list.Add(new Handler
            {
                Id = reader.ReadInt32(),
                Cause = reader.ReadInt32(),
                Address = reader.ReadUInt32(),
                Arg = reader.ReadUInt32(),
                Gp = reader.ReadUInt32(),
            });
        }
    }
}
