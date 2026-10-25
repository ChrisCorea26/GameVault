using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>"APU Pulse" with "APU Sweep". Pulse 1 negates with ones' complement, pulse 2 with two's complement.</summary>
internal sealed class PulseChannel(bool isPulse1)
{
    public LengthCounter Length;
    public Envelope Envelope;

    private byte _duty;
    private byte _sequence;
    private ushort _period;
    private ushort _timer;

    private bool _sweepEnabled;
    private byte _sweepPeriod;
    private bool _sweepNegate;
    private byte _sweepShift;
    private byte _sweepDivider;
    private bool _sweepReload;

    public void WriteControl(byte value)
    {
        _duty = (byte)(value >> 6);
        Length.SetHalt((value & 0x20) != 0);
        Envelope.Write(value);
    }

    public void WriteSweep(byte value)
    {
        _sweepEnabled = (value & 0x80) != 0;
        _sweepPeriod = (byte)((value >> 4) & 0x07);
        _sweepNegate = (value & 0x08) != 0;
        _sweepShift = (byte)(value & 0x07);
        _sweepReload = true;
    }

    public void WriteTimerLow(byte value) => _period = (ushort)((_period & 0x0700) | value);

    public void WriteTimerHigh(byte value)
    {
        _period = (ushort)((_period & 0x00FF) | ((value & 0x07) << 8));
        Length.Load(value >> 3);
        _sequence = 0;
        Envelope.Start = true;
    }

    /// <summary>Clocked every APU cycle (every other CPU cycle).</summary>
    public void ClockTimer()
    {
        if (_timer == 0)
        {
            _timer = _period;
            _sequence = (byte)((_sequence - 1) & 7);
        }
        else
        {
            _timer--;
        }
    }

    public void ClockSweep()
    {
        if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !IsMuted())
        {
            _period = (ushort)TargetPeriod();
        }

        if (_sweepDivider == 0 || _sweepReload)
        {
            _sweepDivider = _sweepPeriod;
            _sweepReload = false;
        }
        else
        {
            _sweepDivider--;
        }
    }

    public int Output
    {
        get
        {
            if (Length.Counter == 0 || IsMuted() || ApuTables.Duty[_duty, _sequence] == 0)
            {
                return 0;
            }

            return Envelope.Output;
        }
    }

    private int TargetPeriod()
    {
        var change = _period >> _sweepShift;
        if (_sweepNegate)
        {
            return _period - change - (isPulse1 ? 1 : 0);
        }

        return _period + change;
    }

    /// <summary>The sweep unit mutes the channel when the period is too low or the target overflows.</summary>
    private bool IsMuted() => _period < 8 || (!_sweepNegate && TargetPeriod() > 0x7FF);

    public void Save(StateWriter w)
    {
        Length.Save(w);
        Envelope.Save(w);
        w.WriteByte(_duty);
        w.WriteByte(_sequence);
        w.WriteUInt16(_period);
        w.WriteUInt16(_timer);
        w.WriteBool(_sweepEnabled);
        w.WriteByte(_sweepPeriod);
        w.WriteBool(_sweepNegate);
        w.WriteByte(_sweepShift);
        w.WriteByte(_sweepDivider);
        w.WriteBool(_sweepReload);
    }

    public void Restore(StateReader r)
    {
        Length.Restore(r);
        Envelope.Restore(r);
        _duty = r.ReadByte();
        _sequence = r.ReadByte();
        _period = r.ReadUInt16();
        _timer = r.ReadUInt16();
        _sweepEnabled = r.ReadBool();
        _sweepPeriod = r.ReadByte();
        _sweepNegate = r.ReadBool();
        _sweepShift = r.ReadByte();
        _sweepDivider = r.ReadByte();
        _sweepReload = r.ReadBool();
    }
}
