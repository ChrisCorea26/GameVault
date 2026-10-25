using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>"APU Triangle": timer clocked every CPU cycle, gated by the linear and length counters.</summary>
internal sealed class TriangleChannel
{
    public LengthCounter Length;

    private bool _control;
    private byte _linearReload;
    private byte _linearCounter;
    private bool _linearReloadFlag;
    private ushort _period;
    private ushort _timer;
    private byte _sequence;

    public void WriteLinear(byte value)
    {
        _control = (value & 0x80) != 0;
        _linearReload = (byte)(value & 0x7F);
        Length.SetHalt(_control);
    }

    public void WriteTimerLow(byte value) => _period = (ushort)((_period & 0x0700) | value);

    public void WriteTimerHigh(byte value)
    {
        _period = (ushort)((_period & 0x00FF) | ((value & 0x07) << 8));
        Length.Load(value >> 3);
        _linearReloadFlag = true;
    }

    public void ClockTimer()
    {
        if (_timer == 0)
        {
            _timer = _period;
            if (_linearCounter > 0 && Length.Counter > 0)
            {
                _sequence = (byte)((_sequence + 1) & 31);
            }
        }
        else
        {
            _timer--;
        }
    }

    public void ClockLinear()
    {
        if (_linearReloadFlag)
        {
            _linearCounter = _linearReload;
        }
        else if (_linearCounter > 0)
        {
            _linearCounter--;
        }

        if (!_control)
        {
            _linearReloadFlag = false;
        }
    }

    /// <summary>
    /// Periods below 2 are ultrasonic; real hardware outputs them, but they only add a DC offset and aliasing,
    /// so (like most emulators) we hold the current step instead.
    /// </summary>
    public int Output => _period < 2 ? 7 : ApuTables.Triangle[_sequence];

    public void Save(StateWriter w)
    {
        Length.Save(w);
        w.WriteBool(_control);
        w.WriteByte(_linearReload);
        w.WriteByte(_linearCounter);
        w.WriteBool(_linearReloadFlag);
        w.WriteUInt16(_period);
        w.WriteUInt16(_timer);
        w.WriteByte(_sequence);
    }

    public void Restore(StateReader r)
    {
        Length.Restore(r);
        _control = r.ReadBool();
        _linearReload = r.ReadByte();
        _linearCounter = r.ReadByte();
        _linearReloadFlag = r.ReadBool();
        _period = r.ReadUInt16();
        _timer = r.ReadUInt16();
        _sequence = r.ReadByte();
    }
}
