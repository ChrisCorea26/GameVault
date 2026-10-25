using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>"APU Noise": 15-bit LFSR, clocked every APU cycle through a period table.</summary>
internal sealed class NoiseChannel(ushort[] periods)
{
    public LengthCounter Length;
    public Envelope Envelope;

    private bool _mode;
    private ushort _period = periods[0];
    private ushort _timer;
    private ushort _shift = 1;

    public void WriteControl(byte value)
    {
        Length.SetHalt((value & 0x20) != 0);
        Envelope.Write(value);
    }

    public void WritePeriod(byte value)
    {
        _mode = (value & 0x80) != 0;
        _period = periods[value & 0x0F];
    }

    public void WriteLength(byte value)
    {
        Length.Load(value >> 3);
        Envelope.Start = true;
    }

    public void ClockTimer()
    {
        if (_timer == 0)
        {
            _timer = (ushort)(_period - 1);
            var feedback = (_shift ^ (_shift >> (_mode ? 6 : 1))) & 1;
            _shift = (ushort)((_shift >> 1) | (feedback << 14));
        }
        else
        {
            _timer--;
        }
    }

    public int Output => Length.Counter == 0 || (_shift & 1) != 0 ? 0 : Envelope.Output;

    public void Save(StateWriter w)
    {
        Length.Save(w);
        Envelope.Save(w);
        w.WriteBool(_mode);
        w.WriteUInt16(_period);
        w.WriteUInt16(_timer);
        w.WriteUInt16(_shift);
    }

    public void Restore(StateReader r)
    {
        Length.Restore(r);
        Envelope.Restore(r);
        _mode = r.ReadBool();
        _period = r.ReadUInt16();
        _timer = r.ReadUInt16();
        _shift = r.ReadUInt16();
    }
}
