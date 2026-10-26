using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The IOP's six root counters (ps2tek: "IOP Timers"): 0–2 are 16-bit at $1F801100, 3–5 32-bit at $1F801480, each
/// with a count, mode and target register. Sources: the IOP clock; timer 0 the 13.5 MHz pixel clock, timers 1 and 3
/// H-blanks (mode bit 8); timer 2 divided by 8 (bit 9); timers 4 and 5 divided by 1, 8, 16 or 256 (bits 13–14).
/// Gates (bit 0, mode in bits 1–2) follow H-blank for timer 0 and V-blank for timers 1 and 3: 0 counts outside the
/// blank, 1 counts always and clears when the blank ends, 2 counts only inside it and clears at both ends, 3 waits for
/// the blank to end, then counts. Timers 2, 4 and 5 count with the gate on only in modes 1 and 3.
/// </summary>
/// <remarks>
/// Reaching the target (with bit 4) or overflowing (with bit 5) sets bit 11 or 12 and interrupts while bit 10 is set;
/// without bit 6 (repeat) the interrupt is one-shot (bit 10 clears), with bits 6 and 7 bit 10 toggles. Bit 3 returns
/// the count to 0 at the target. Writing the mode clears the count and sets bit 10; reading it clears bits 11 and 12.
/// Gate details follow PCSX2's IopCounters.cpp (read for behaviour only).
/// </remarks>
public sealed class Ps2IopTimers : IStateful
{
    public const int Count = 6;

    private static readonly StateTag Tag = StateTag.From("IOPT");

    private readonly long[] _count = new long[Count];
    private readonly uint[] _mode = new uint[Count];
    private readonly long[] _target = new long[Count];
    private readonly long[] _fraction = new long[Count];
    private readonly bool[] _stopped = new bool[Count];
    private bool _hblank;
    private bool _vblank;

    /// <summary>Raised with the timer's number when it interrupts (IOP IRQ 4–6 for 0–2, 14–16 for 3–5).</summary>
    public Action<int>? Interrupt { get; set; }

    public void Reset()
    {
        Array.Clear(_count);
        Array.Clear(_mode);
        Array.Clear(_target);
        Array.Clear(_fraction);
        Array.Clear(_stopped);
        _hblank = _vblank = false;
    }

    private static long Wrap(int timer) => timer < 3 ? 0x10000 : 0x1_0000_0000;

    /// <summary>Counts H-blanks rather than cycles.</summary>
    private bool CountsHBlanks(int timer) => (timer == 1 || timer == 3) && (_mode[timer] & 0x100) != 0;

    /// <summary>The source's rate as IOP cycles per tick, a fraction: <paramref name="num"/> ticks every <paramref name="den"/> cycles.</summary>
    private void Rate(int timer, out long num, out long den)
    {
        var mode = _mode[timer];
        num = 1;
        den = 1;
        switch (timer)
        {
            case 0 when (mode & 0x100) != 0:
                // 13.5 MHz from 36.864 MHz: 375 ticks every 1,024 cycles.
                num = 375;
                den = 1024;
                break;
            case 2 when (mode & 0x200) != 0:
                den = 8;
                break;
            case 4:
            case 5:
                den = ((mode >> 13) & 3) switch { 1 => 8, 2 => 16, 3 => 256, _ => 1 };
                break;
        }
    }

    private bool CanCount(int timer)
    {
        if (_stopped[timer])
        {
            return false;
        }

        var mode = _mode[timer];
        if ((mode & 1) == 0)
        {
            return true;
        }

        var gate = (mode >> 1) & 3;
        if (timer is 2 or 4 or 5)
        {
            return (gate & 1) != 0;
        }

        var blank = timer == 0 ? _hblank : _vblank;
        return gate switch
        {
            0 => !blank,
            2 => blank,
            _ => true,
        };
    }

    /// <summary>Advances the cycle-clocked timers by <paramref name="cycles"/> IOP cycles.</summary>
    public void Advance(long cycles)
    {
        for (var t = 0; t < Count; t++)
        {
            if (CountsHBlanks(t) || !CanCount(t))
            {
                continue;
            }

            Rate(t, out var num, out var den);
            var total = _fraction[t] + cycles * num;
            _fraction[t] = total % den;
            Tick(t, total / den);
        }
    }

    /// <summary>IOP cycles until the next target or overflow of a cycle-clocked timer.</summary>
    public long CyclesUntilEvent()
    {
        var best = long.MaxValue;
        for (var t = 0; t < Count; t++)
        {
            if (CountsHBlanks(t) || !CanCount(t) || (_mode[t] & 0x30) == 0)
            {
                continue;
            }

            var ticks = TicksToEvent(t);
            Rate(t, out var num, out var den);
            var cycles = (ticks * den - _fraction[t] + num - 1) / num;
            best = Math.Min(best, Math.Max(1, cycles));
        }

        return best;
    }

    private long TicksToEvent(int timer)
    {
        var wrap = Wrap(timer);
        var toTarget = (_target[timer] - _count[timer]) & (wrap - 1);
        if (toTarget == 0)
        {
            toTarget = wrap;
        }

        return Math.Min(toTarget, wrap - _count[timer]);
    }

    private void Tick(int timer, long ticks)
    {
        var wrap = Wrap(timer);
        while (ticks > 0)
        {
            var step = Math.Min(ticks, TicksToEvent(timer));
            ticks -= step;
            _count[timer] += step;
            if (_count[timer] >= wrap)
            {
                _count[timer] = 0;
                if ((_mode[timer] & 0x20) != 0)
                {
                    _mode[timer] |= 0x1000;
                    Fire(timer);
                }
            }

            if (_count[timer] == _target[timer] && step > 0)
            {
                if ((_mode[timer] & 0x10) != 0)
                {
                    _mode[timer] |= 0x800;
                    Fire(timer);
                }

                if ((_mode[timer] & 0x08) != 0)
                {
                    _count[timer] = 0;
                }
            }
        }
    }

    private void Fire(int timer)
    {
        var mode = _mode[timer];
        if ((mode & 0x400) != 0)
        {
            Interrupt?.Invoke(timer);
        }

        if ((mode & 0x40) == 0)
        {
            _mode[timer] &= ~0x400u; // one-shot
        }
        else if ((mode & 0x80) != 0)
        {
            _mode[timer] ^= 0x400; // toggle
        }
    }

    /// <summary>The H-blank signal from the video timing: counts timers 1 and 3 on H-blank and gates timer 0.</summary>
    public void SetHBlank(bool active)
    {
        if (active == _hblank)
        {
            return;
        }

        _hblank = active;
        if (active)
        {
            for (var t = 1; t <= 3; t += 2)
            {
                if (CountsHBlanks(t) && CanCount(t))
                {
                    Tick(t, 1);
                }
            }
        }

        Gate(0, active);
    }

    /// <summary>The V-blank signal: gates timers 1 and 3.</summary>
    public void SetVBlank(bool active)
    {
        if (active == _vblank)
        {
            return;
        }

        _vblank = active;
        Gate(1, active);
        Gate(3, active);
    }

    private void Gate(int timer, bool start)
    {
        var mode = _mode[timer];
        if ((mode & 1) == 0)
        {
            return;
        }

        switch ((mode >> 1) & 3)
        {
            case 1 when !start:
            case 2:
                _count[timer] = 0;
                break;
            case 3 when !start:
                _stopped[timer] = false;
                break;
        }
    }

    /// <summary>A register read: <paramref name="register"/> 0 the count, 1 the mode, 2 the target.</summary>
    public uint Read(int timer, int register)
    {
        switch (register)
        {
            case 0:
                return (uint)_count[timer];
            case 1:
            {
                var mode = _mode[timer];
                _mode[timer] &= ~0x1800u;
                return mode;
            }

            case 2:
                return (uint)_target[timer];
            default:
                return 0;
        }
    }

    public void Write(int timer, int register, uint value)
    {
        var mask = timer < 3 ? 0xFFFFu : 0xFFFFFFFFu;
        switch (register)
        {
            case 0:
                _count[timer] = value & mask;
                break;
            case 1:
                _mode[timer] = (value & 0x63FF) | 0x400;
                _count[timer] = 0;
                _fraction[timer] = 0;
                // Gate mode 3 waits for the end of the next blank.
                _stopped[timer] = (value & 1) != 0 && ((value >> 1) & 3) == 3 && timer is 0 or 1 or 3;
                break;
            case 2:
                _target[timer] = value & mask;
                if ((_mode[timer] & 0x80) == 0)
                {
                    _mode[timer] |= 0x400;
                }

                break;
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var t = 0; t < Count; t++)
        {
            writer.WriteInt64(_count[t]);
            writer.WriteUInt32(_mode[t]);
            writer.WriteInt64(_target[t]);
            writer.WriteInt64(_fraction[t]);
            writer.WriteBool(_stopped[t]);
        }

        writer.WriteBool(_hblank);
        writer.WriteBool(_vblank);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported IOP timer state version.");
        }

        for (var t = 0; t < Count; t++)
        {
            _count[t] = reader.ReadInt64() & (Wrap(t) - 1);
            _mode[t] = reader.ReadUInt32();
            _target[t] = reader.ReadInt64() & (Wrap(t) - 1);
            _fraction[t] = reader.ReadInt64();
            _stopped[t] = reader.ReadBool();
        }

        _hblank = reader.ReadBool();
        _vblank = reader.ReadBool();
        reader.EndChunk();
    }
}
