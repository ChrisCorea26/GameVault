using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The EE's four timers at $10000000, $10000800, $10001000 and $10001800 (ps2tek: "EE Timers"): Tn_COUNT (+$00),
/// Tn_MODE (+$10), Tn_COMP (+$20) and, on T0 and T1, Tn_HOLD (+$30). Each 16-bit counter counts the bus clock
/// (147.456 MHz, half the EE's), the bus clock / 16 or / 256, or H-blanks; it can be gated by H-blank or V-blank,
/// cleared on reaching its compare value (ZRET), and raise Timer0–3 on the INTC on reaching the compare value or on
/// overflowing.
/// </summary>
/// <remarks>
/// The console advances the timers in bus cycles and reports the blanking edges. Interrupts are edge-triggered: one is
/// sent only when an enabled flag goes from 0 to 1 (ps2tek). When the clock source and the gate are both H-blank the
/// gate is ignored (as PCSX2 documents). The gate modes follow the EE User's Manual as ps2tek lists them: mode 0 counts
/// only while the gate signal is inactive (outside the blank), modes 1–3 clear the counter on the blank's start, end,
/// or both.
/// </remarks>
public sealed class Ps2EeTimers : IStateful
{
    public const uint BaseAddress = 0x10000000;
    public const int Count = 4;

    // Tn_MODE bits (ps2tek: "EE Timers").
    private const uint ModeClock = 0x3;
    private const uint ModeGateEnable = 1 << 2;
    private const uint ModeGateVBlank = 1 << 3;
    private const int ModeGateModeShift = 4;
    private const uint ModeZeroReturn = 1 << 6;
    private const uint ModeEnable = 1 << 7;
    private const uint ModeCompareIrq = 1 << 8;
    private const uint ModeOverflowIrq = 1 << 9;
    private const uint ModeCompareFlag = 1 << 10;
    private const uint ModeOverflowFlag = 1 << 11;
    private const uint ModeWritable = 0x3FF;

    private const int ClockHBlank = 3;

    private static readonly StateTag Tag = StateTag.From("ETIM");
    private static readonly int[] Dividers = [1, 16, 256, 0];

    private readonly Ps2Intc _intc;
    private readonly uint[] _count = new uint[Count];
    private readonly uint[] _mode = new uint[Count];
    private readonly uint[] _compare = new uint[Count];
    private readonly uint[] _hold = new uint[Count];
    private readonly long[] _prescale = new long[Count];
    private bool _inHBlank;
    private bool _inVBlank;

    public Ps2EeTimers(Ps2Intc intc)
    {
        _intc = intc;
    }

    public void Reset()
    {
        Array.Clear(_count);
        Array.Clear(_mode);
        Array.Clear(_compare);
        Array.Clear(_hold);
        Array.Clear(_prescale);
        _inHBlank = false;
        _inVBlank = false;
    }

    // ---- Registers ----

    /// <summary>Reads Tn_COUNT, Tn_MODE, Tn_COMP or Tn_HOLD; <paramref name="address"/> is the physical address.</summary>
    public uint Read(uint address)
    {
        var timer = (int)((address >> 11) & 3);
        return (address & 0x7F0) switch
        {
            0x00 => _count[timer],
            0x10 => _mode[timer],
            0x20 => _compare[timer],
            0x30 when timer < 2 => _hold[timer],
            _ => 0,
        };
    }

    public void Write(uint address, uint value)
    {
        var timer = (int)((address >> 11) & 3);
        switch (address & 0x7F0)
        {
            case 0x00:
                _count[timer] = value & 0xFFFF;
                break;
            case 0x10:
            {
                // Writing 1 to a flag clears it; the other bits are set as written (ps2tek).
                var flags = _mode[timer] & (ModeCompareFlag | ModeOverflowFlag) & ~value;
                if (((_mode[timer] ^ value) & ModeClock) != 0)
                {
                    _prescale[timer] = 0;
                }

                _mode[timer] = (value & ModeWritable) | flags;
                break;
            }

            case 0x20:
                _compare[timer] = value & 0xFFFF;
                break;
            case 0x30 when timer < 2:
                _hold[timer] = value & 0xFFFF;
                break;
        }
    }

    /// <summary>Copies T0's and T1's counters into their Tn_HOLD registers (on an SBUS interrupt).</summary>
    public void LatchHold()
    {
        _hold[0] = _count[0];
        _hold[1] = _count[1];
    }

    // ---- Clocking ----

    /// <summary>Advances the bus-clocked timers by <paramref name="busCycles"/> bus cycles.</summary>
    public void Advance(long busCycles)
    {
        if (busCycles <= 0)
        {
            return;
        }

        for (var i = 0; i < Count; i++)
        {
            var mode = _mode[i];
            var clock = (int)(mode & ModeClock);
            if ((mode & ModeEnable) == 0 || clock == ClockHBlank || GateHolds(i))
            {
                continue;
            }

            var divider = Dividers[clock];
            long ticks;
            if (divider == 1)
            {
                ticks = busCycles;
            }
            else
            {
                var total = _prescale[i] + busCycles;
                ticks = total / divider;
                _prescale[i] = total % divider;
            }

            if (ticks > 0)
            {
                Tick(i, ticks);
            }
        }
    }

    /// <summary>The start of H-blank: the H-blank clock source and the H-blank gate's rising edge.</summary>
    public void HBlankStart()
    {
        _inHBlank = true;
        GateEdge(vblank: false, rising: true);
        for (var i = 0; i < Count; i++)
        {
            var mode = _mode[i];
            if ((mode & ModeEnable) != 0 && (mode & ModeClock) == ClockHBlank && !GateHolds(i))
            {
                Tick(i, 1);
            }
        }
    }

    public void HBlankEnd()
    {
        _inHBlank = false;
        GateEdge(vblank: false, rising: false);
    }

    public void VBlankStart()
    {
        _inVBlank = true;
        GateEdge(vblank: true, rising: true);
    }

    public void VBlankEnd()
    {
        _inVBlank = false;
        GateEdge(vblank: true, rising: false);
    }

    /// <summary>
    /// The bus cycles until a bus-clocked timer can next raise an interrupt (an enabled compare or overflow whose flag is
    /// clear), or <see cref="long.MaxValue"/> if none can. H-blank-clocked timers only change at the console's H-blanks.
    /// </summary>
    public long CyclesUntilNextEvent()
    {
        var best = long.MaxValue;
        for (var i = 0; i < Count; i++)
        {
            var mode = _mode[i];
            var clock = (int)(mode & ModeClock);
            if ((mode & ModeEnable) == 0 || clock == ClockHBlank || GateHolds(i))
            {
                continue;
            }

            long ticks = long.MaxValue;
            var count = _count[i];
            if ((mode & ModeCompareIrq) != 0 && (mode & ModeCompareFlag) == 0)
            {
                ticks = Math.Min(ticks, TicksToCompare(count, _compare[i]));
            }

            if ((mode & ModeOverflowIrq) != 0 && (mode & ModeOverflowFlag) == 0)
            {
                ticks = Math.Min(ticks, 0x10000 - count);
            }

            if (ticks == long.MaxValue)
            {
                continue;
            }

            var divider = Dividers[clock];
            var cycles = (ticks * divider) - _prescale[i];
            best = Math.Min(best, Math.Max(1, cycles));
        }

        return best;
    }

    // Mode 0 of a gate stops the count while its blank is active. A gate on the timer's own H-blank clock is ignored.
    private bool GateHolds(int timer)
    {
        var mode = _mode[timer];
        if ((mode & ModeGateEnable) == 0 || ((mode >> ModeGateModeShift) & 3) != 0)
        {
            return false;
        }

        var vblank = (mode & ModeGateVBlank) != 0;
        if (!vblank && (mode & ModeClock) == ClockHBlank)
        {
            return false;
        }

        return vblank ? _inVBlank : _inHBlank;
    }

    private void GateEdge(bool vblank, bool rising)
    {
        for (var i = 0; i < Count; i++)
        {
            var mode = _mode[i];
            if ((mode & ModeEnable) == 0 || (mode & ModeGateEnable) == 0 || ((mode & ModeGateVBlank) != 0) != vblank)
            {
                continue;
            }

            if (!vblank && (mode & ModeClock) == ClockHBlank)
            {
                continue;
            }

            var gateMode = (mode >> ModeGateModeShift) & 3;
            if (gateMode == 3 || (gateMode == 1 && rising) || (gateMode == 2 && !rising))
            {
                _count[i] = 0;
                _prescale[i] = 0;
            }
        }
    }

    private static long TicksToCompare(uint count, uint compare) =>
        compare > count ? compare - count : 0x10000 - count + compare;

    private void Tick(int timer, long ticks)
    {
        var count = _count[timer];
        var compare = _compare[timer];
        var mode = _mode[timer];
        var raised = 0u;
        while (ticks > 0)
        {
            var step = Math.Min(ticks, Math.Min(TicksToCompare(count, compare), 0x10000 - count));
            count += (uint)step;
            ticks -= step;
            if (count == 0x10000)
            {
                count = 0;
                raised |= ModeOverflowFlag;
            }

            if (count == compare)
            {
                raised |= ModeCompareFlag;
                if ((mode & ModeZeroReturn) != 0)
                {
                    count = 0;
                }
            }

            // Once both flags are set only the count can change, and it repeats: skip whole periods.
            const uint bothFlags = ModeCompareFlag | ModeOverflowFlag;
            if (ticks > 0x10000 && ((mode | raised) & bothFlags) == bothFlags)
            {
                if ((mode & ModeZeroReturn) == 0 || compare == 0)
                {
                    ticks %= 0x10000;
                }
                else if (count < compare)
                {
                    ticks %= compare;
                }
            }
        }

        _count[timer] = count;
        var newFlags = raised & ~mode;
        _mode[timer] = mode | raised;

        // Edge-triggered: only flags that went from 0 to 1 with their interrupt enabled (ps2tek).
        if (((newFlags & ModeCompareFlag) != 0 && (mode & ModeCompareIrq) != 0) ||
            ((newFlags & ModeOverflowFlag) != 0 && (mode & ModeOverflowIrq) != 0))
        {
            _intc.Raise(Ps2Interrupt.Timer0 + timer);
        }
    }

    // ---- Save states ----

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var i = 0; i < Count; i++)
        {
            writer.WriteUInt32(_count[i]);
            writer.WriteUInt32(_mode[i]);
            writer.WriteUInt32(_compare[i]);
            writer.WriteUInt32(_hold[i]);
            writer.WriteInt64(_prescale[i]);
        }

        writer.WriteBool(_inHBlank);
        writer.WriteBool(_inVBlank);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        if (reader.BeginChunk(Tag) != 1)
        {
            throw new StateFormatException("Unsupported EE timer state version.");
        }

        for (var i = 0; i < Count; i++)
        {
            _count[i] = reader.ReadUInt32() & 0xFFFF;
            _mode[i] = reader.ReadUInt32() & 0xFFF;
            _compare[i] = reader.ReadUInt32() & 0xFFFF;
            _hold[i] = reader.ReadUInt32() & 0xFFFF;
            _prescale[i] = reader.ReadInt64();
        }

        _inHBlank = reader.ReadBool();
        _inVBlank = reader.ReadBool();
        reader.EndChunk();
    }
}
