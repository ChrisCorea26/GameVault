using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>"APU Envelope": clocked on quarter frames.</summary>
internal struct Envelope
{
    public bool Start;
    public bool Loop;
    public bool Constant;
    public byte Volume;
    private byte _divider;
    private byte _decay;

    public readonly byte Output => Constant ? Volume : _decay;

    public void Write(byte value)
    {
        Loop = (value & 0x20) != 0;
        Constant = (value & 0x10) != 0;
        Volume = (byte)(value & 0x0F);
    }

    public void Clock()
    {
        if (Start)
        {
            Start = false;
            _decay = 15;
            _divider = Volume;
            return;
        }

        if (_divider > 0)
        {
            _divider--;
            return;
        }

        _divider = Volume;
        if (_decay > 0)
        {
            _decay--;
        }
        else if (Loop)
        {
            _decay = 15;
        }
    }

    public void Save(StateWriter w)
    {
        w.WriteBool(Start);
        w.WriteBool(Loop);
        w.WriteBool(Constant);
        w.WriteByte(Volume);
        w.WriteByte(_divider);
        w.WriteByte(_decay);
    }

    public void Restore(StateReader r)
    {
        Start = r.ReadBool();
        Loop = r.ReadBool();
        Constant = r.ReadBool();
        Volume = r.ReadByte();
        _divider = r.ReadByte();
        _decay = r.ReadByte();
    }
}
