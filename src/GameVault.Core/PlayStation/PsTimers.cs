using GameVault.Core.State;

namespace GameVault.Core.PlayStation;

/// <summary>
/// The three root counters at $1F801100 (SPEC §7.11, Phase 15). Timer 0 counts the system clock or the GPU's dot
/// clock, timer 1 the system clock or H-blanks, timer 2 the system clock or system clock / 8. Each can reset at a
/// target, raise its interrupt at the target or at $FFFF (once or repeatedly, as a pulse or a toggle), and
/// synchronise to H-blank (timer 0) or V-blank (timer 1).
/// </summary>
/// <remarks>
/// Reference: psx-spx "Timers". The console advances the timers in system-clock cycles and reports H-blanks,
/// V-blank edges and dots; H-blank is treated as an instant at the start of each line.
/// </remarks>
public sealed class PsTimers : IStateful
{
    private static readonly StateTag Tag = StateTag.From("PTIM");

    private readonly ushort[] _counter = new ushort[3];
    private readonly ushort[] _mode = new ushort[3];
    private readonly ushort[] _target = new ushort[3];
    private readonly bool[] _paused = new bool[3];
    private readonly bool[] _irqDone = new bool[3];
    private int _sysclock8Remainder;
    private bool _inVBlank;

    /// <summary>Raised with the timer's number (0–2) when it requests an interrupt.</summary>
    public Action<int>? Interrupt { get; set; }

    public void Reset()
    {
        for (var i = 0; i < 3; i++)
        {
            _counter[i] = _target[i] = 0;
            _mode[i] = 0x400;
            _paused[i] = false;
            _irqDone[i] = false;
        }

        _sysclock8Remainder = 0;
        _inVBlank = false;
    }

    // ---- Registers ----

    public ushort Read(int timer, int register)
    {
        switch (register)
        {
            case 0:
                return _counter[timer];
            case 1:
            {
                // Reading the mode clears the "reached" bits.
                var value = _mode[timer];
                _mode[timer] &= unchecked((ushort)~0x1800);
                return value;
            }

            default:
                return _target[timer];
        }
    }

    public void Write(int timer, int register, ushort value)
    {
        switch (register)
        {
            case 0:
                _counter[timer] = value;
                break;
            case 1:
                // Writing the mode resets the counter and sets the IRQ bit (bit 10, active low) back to 1.
                _mode[timer] = (ushort)((value & 0x3FF) | 0x400);
                _counter[timer] = 0;
                _irqDone[timer] = false;
                _paused[timer] = SyncEnabled(timer) && StartsPaused(timer);
                break;
            default:
                _target[timer] = value;
                break;
        }
    }

    private bool SyncEnabled(int timer) => (_mode[timer] & 1) != 0;

    private int SyncMode(int timer) => (_mode[timer] >> 1) & 3;

    private bool StartsPaused(int timer) => timer switch
    {
        // Mode 3 pauses until the first blank; timer 2's modes 0 and 3 stop it.
        0 or 1 => SyncMode(timer) == 3 || (timer == 1 && SyncMode(timer) == 2 && !_inVBlank),
        _ => SyncMode(timer) is 0 or 3,
    };

    /// <summary>Timer 0 counts dots (clock source 1 or 3).</summary>
    public bool Timer0CountsDots => (_mode[0] & 0x100) != 0;

    /// <summary>Timer 1 counts H-blanks (clock source 1 or 3).</summary>
    public bool Timer1CountsHBlanks => (_mode[1] & 0x100) != 0;

    // ---- Clocks ----

    /// <summary>Advances the timers that count the system clock by <paramref name="cycles"/>.</summary>
    public void AdvanceCycles(int cycles)
    {
        if (!Timer0CountsDots)
        {
            Count(0, cycles);
        }

        if (!Timer1CountsHBlanks)
        {
            Count(1, cycles);
        }

        if ((_mode[2] & 0x200) != 0)
        {
            var total = _sysclock8Remainder + cycles;
            _sysclock8Remainder = total & 7;
            Count(2, total >> 3);
        }
        else
        {
            Count(2, cycles);
        }
    }

    /// <summary>Dots of the GPU's dot clock (timer 0's alternative source).</summary>
    public void AdvanceDots(int dots)
    {
        if (Timer0CountsDots)
        {
            Count(0, dots);
        }
    }

    /// <summary>An H-blank: counted by timer 1, and timer 0's synchronisation point.</summary>
    public void HBlank()
    {
        if (Timer1CountsHBlanks)
        {
            Count(1, 1);
        }

        if (SyncEnabled(0))
        {
            switch (SyncMode(0))
            {
                case 1:
                case 2:
                    _counter[0] = 0;
                    break;
                case 3:
                    _paused[0] = false;
                    _mode[0] &= unchecked((ushort)~1); // free run from now on
                    break;
            }
        }
    }

    /// <summary>The start or end of V-blank: timer 1's synchronisation.</summary>
    public void VBlank(bool active)
    {
        _inVBlank = active;
        if (!SyncEnabled(1))
        {
            return;
        }

        switch (SyncMode(1))
        {
            case 0: // pause during V-blank
                _paused[1] = active;
                break;
            case 1: // reset at V-blank
                if (active)
                {
                    _counter[1] = 0;
                }

                break;
            case 2: // reset at V-blank and pause outside it
                if (active)
                {
                    _counter[1] = 0;
                }

                _paused[1] = !active;
                break;
            case 3: // pause until V-blank, then free run
                if (active)
                {
                    _paused[1] = false;
                    _mode[1] &= unchecked((ushort)~1);
                }

                break;
        }
    }

    /// <summary>
    /// System-clock cycles until a timer counting the system clock next reaches its target or $FFFF, so the console
    /// can run exactly to that point. <see cref="int.MaxValue"/> when none will.
    /// </summary>
    public int CyclesUntilEvent()
    {
        var best = int.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            if (_paused[i] || (i == 0 && Timer0CountsDots) || (i == 1 && Timer1CountsHBlanks))
            {
                continue;
            }

            var toTarget = (_target[i] - _counter[i]) & 0xFFFF;
            var toWrap = 0xFFFF - _counter[i];
            var ticks = Math.Min(toTarget == 0 ? 0x10000 : toTarget, toWrap == 0 ? 0x10000 : toWrap);
            var cycles = i == 2 && (_mode[2] & 0x200) != 0 ? ticks * 8 - _sysclock8Remainder : ticks;
            best = Math.Min(best, Math.Max(1, cycles));
        }

        return best;
    }

    private void Count(int timer, int ticks)
    {
        if (_paused[timer] || ticks <= 0)
        {
            return;
        }

        var target = _target[timer];
        var old = (int)_counter[timer];
        var counter = old + ticks;

        // Each event fires as the counter passes it (psx-spx: "Timers").
        if (old < target && counter >= target)
        {
            _mode[timer] |= 0x800;
            Fire(timer, 0x10);
        }

        if (old < 0xFFFF && counter >= 0xFFFF)
        {
            _mode[timer] |= 0x1000;
            Fire(timer, 0x20);
        }

        if ((_mode[timer] & 0x08) != 0 && counter >= target)
        {
            // Reset at target: the counter goes back to 0 as it reaches the target (a target of 0 holds it at 0).
            counter = target == 0 ? 0 : counter % target;
        }

        _counter[timer] = (ushort)counter;
    }

    /// <summary>Raises the interrupt for an event enabled by <paramref name="enableBit"/> (bit 4: target, bit 5: $FFFF).</summary>
    private void Fire(int timer, int enableBit)
    {
        var mode = _mode[timer];
        if ((mode & enableBit) == 0)
        {
            return;
        }

        var repeat = (mode & 0x40) != 0;
        if (!repeat && _irqDone[timer])
        {
            return;
        }

        _irqDone[timer] = true;
        if ((mode & 0x80) != 0)
        {
            // Toggle mode: bit 10 flips, and the interrupt fires when it goes low.
            _mode[timer] ^= 0x400;
            if ((_mode[timer] & 0x400) == 0)
            {
                Interrupt?.Invoke(timer);
            }
        }
        else
        {
            // Pulse mode: bit 10 drops for a moment, which fires the interrupt.
            Interrupt?.Invoke(timer);
        }
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var i = 0; i < 3; i++)
        {
            writer.WriteUInt16(_counter[i]);
            writer.WriteUInt16(_mode[i]);
            writer.WriteUInt16(_target[i]);
            writer.WriteBool(_paused[i]);
            writer.WriteBool(_irqDone[i]);
        }

        writer.WriteInt32(_sysclock8Remainder);
        writer.WriteBool(_inVBlank);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 3; i++)
        {
            _counter[i] = reader.ReadUInt16();
            _mode[i] = reader.ReadUInt16();
            _target[i] = reader.ReadUInt16();
            _paused[i] = reader.ReadBool();
            _irqDone[i] = reader.ReadBool();
        }

        _sysclock8Remainder = reader.ReadInt32() & 7;
        _inVBlank = reader.ReadBool();
        reader.EndChunk();
    }
}
