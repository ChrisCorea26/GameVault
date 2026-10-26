using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

// Alarms (SetAlarm/ReleaseAlarm): one-shot callbacks after a number of H-blanks, on timer 3 as the BIOS's kernel does
// (Play!'s PS2OS.cpp, BSD-2-Clause: sc_SetAlarm, AlarmUpdateCompare, AssembleAlarmHandler).
public sealed partial class Ps2Kernel
{
    private const int MaxAlarms = 64;
    private const uint T3Count = 0x1000_1800;
    private const uint T3Mode = 0x1000_1810;
    private const uint T3Compare = 0x1000_1820;

    // T3_MODE: H-blank clock, counting, the compare interrupt; writing the flags clears them.
    private const uint T3Counting = 0x83;
    private const uint T3CompareInterrupt = 0x100;
    private const uint T3Flags = 0xC00;

    private sealed class Alarm
    {
        public int Id;
        public ushort Target;
        public uint Delay;
        public uint Handler;
        public uint Arg;
        public uint Gp;
    }

    private readonly List<Alarm> _alarms = [];

    /// <summary>
    /// SetAlarm (24/-30): calls <c>handler(id, time, arg)</c> once, <c>time</c> H-blanks from now, in interrupt
    /// context. Returns the alarm's id.
    /// </summary>
    private void SetAlarm()
    {
        var delay = Arg(0) & 0xFFFF;
        if (_alarms.Count >= MaxAlarms || delay == 0)
        {
            Return(-1);
            return;
        }

        var id = 1;
        while (_alarms.Exists(a => a.Id == id))
        {
            id++;
        }

        var count = _console.Timers.Read(T3Count);
        _alarms.Add(new Alarm
        {
            Id = id,
            Target = (ushort)(count + delay),
            Delay = delay,
            Handler = Arg(1),
            Arg = Arg(2),
            Gp = (uint)_cpu.GetGpr64(28),
        });
        UpdateAlarmTimer();
        Return(id);
    }

    /// <summary>ReleaseAlarm (25/-31): cancels an alarm that hasn't gone off.</summary>
    private void ReleaseAlarm()
    {
        var index = _alarms.FindIndex(a => a.Id == (int)Arg(0));
        if (index < 0)
        {
            Return(-1);
            return;
        }

        _alarms.RemoveAt(index);
        UpdateAlarmTimer();
        Return(0);
    }

    /// <summary>Timer 3 counts H-blanks and interrupts at the nearest alarm (INTC's timer 3 enabled while any is set).</summary>
    private void UpdateAlarmTimer()
    {
        var timers = _console.Timers;
        if (_alarms.Count == 0)
        {
            timers.Write(T3Mode, T3Counting | T3Flags);
            return;
        }

        var count = (ushort)timers.Read(T3Count);
        var nearest = _alarms[0];
        foreach (var alarm in _alarms)
        {
            if (Distance(count, alarm.Target) < Distance(count, nearest.Target))
            {
                nearest = alarm;
            }
        }

        timers.Write(T3Compare, nearest.Target);
        timers.Write(T3Mode, T3Counting | T3CompareInterrupt | T3Flags);
        var bit = 1u << (int)Ps2Interrupt.Timer3;
        if ((_console.Intc.Mask & bit) == 0)
        {
            _console.Intc.Write(Ps2Intc.MaskAddress, bit);
        }

        _console.UpdateInterruptLines();
    }

    private static int Distance(ushort from, ushort to) => ((to - from) & 0xFFFF) is var d && d == 0 ? 0x10000 : d;

    /// <summary>Timer 3's interrupt: the alarms that are due are called after the line's own handlers.</summary>
    private void QueueDueAlarms()
    {
        _console.Timers.Write(T3Mode, (_console.Timers.Read(T3Mode) & 0x3FF) | T3Flags); // clear the flags
        var compare = (ushort)_console.Timers.Read(T3Compare);
        var any = false;
        foreach (var alarm in _alarms)
        {
            if (alarm.Target == compare)
            {
                _callQueue.Add(-alarm.Id);
                any = true;
            }
        }

        if (!any)
        {
            UpdateAlarmTimer();
        }
    }

    /// <summary>Calls an alarm's handler (it is released first, being one-shot); false if it was released meanwhile.</summary>
    private bool CallAlarm(int id)
    {
        var index = _alarms.FindIndex(a => a.Id == id);
        if (index < 0)
        {
            return false;
        }

        var alarm = _alarms[index];
        _alarms.RemoveAt(index);
        UpdateAlarmTimer();
        _callingAlarm = true;
        _cpu.SetGpr64(4, (ulong)id);
        _cpu.SetGpr64(5, alarm.Delay);
        _cpu.SetGpr64(6, alarm.Arg);
        _cpu.SetGpr64(28, alarm.Gp);
        _cpu.SetGpr64(29, InterruptStackTop - 0x40);
        _cpu.SetGpr64(31, HandlerReturnTrap);
        _cpu.Jump(alarm.Handler);
        return true;
    }

    private void SaveAlarms(StateWriter writer)
    {
        writer.WriteInt32(_alarms.Count);
        foreach (var a in _alarms)
        {
            writer.WriteInt32(a.Id);
            writer.WriteUInt16(a.Target);
            writer.WriteUInt32(a.Delay);
            writer.WriteUInt32(a.Handler);
            writer.WriteUInt32(a.Arg);
            writer.WriteUInt32(a.Gp);
        }
    }

    private void LoadAlarms(StateReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > MaxAlarms)
        {
            throw new StateFormatException("Too many alarms.");
        }

        _alarms.Clear();
        for (var i = 0; i < count; i++)
        {
            _alarms.Add(new Alarm
            {
                Id = reader.ReadInt32(),
                Target = reader.ReadUInt16(),
                Delay = reader.ReadUInt32(),
                Handler = reader.ReadUInt32(),
                Arg = reader.ReadUInt32(),
                Gp = reader.ReadUInt32(),
            });
        }
    }
}
