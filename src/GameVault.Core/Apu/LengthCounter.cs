using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>
/// "APU Length Counter". Halt and reload writes take effect after the frame counter's clock in the same
/// CPU cycle: a reload is dropped if the counter was clocked in between (blargg 11.len_reload_timing), and
/// a new halt flag only applies from the next clock (10.len_halt_timing).
/// </summary>
internal struct LengthCounter
{
    public byte Counter;
    public bool Halt;
    public bool Enabled;
    private bool _newHalt;
    private byte _reloadValue;
    private byte _previousCounter;

    public void SetHalt(bool halt) => _newHalt = halt;

    public void Load(int index)
    {
        if (Enabled)
        {
            _reloadValue = ApuTables.Length[index];
            _previousCounter = Counter;
        }
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            Counter = 0;
        }
    }

    public void Clock()
    {
        if (Counter > 0 && !Halt)
        {
            Counter--;
        }
    }

    /// <summary>Applies pending writes at the end of the CPU cycle.</summary>
    public void Commit()
    {
        if (_reloadValue != 0)
        {
            if (Counter == _previousCounter)
            {
                Counter = _reloadValue;
            }

            _reloadValue = 0;
        }

        Halt = _newHalt;
    }

    public void Save(StateWriter w)
    {
        w.WriteByte(Counter);
        w.WriteBool(Halt);
        w.WriteBool(Enabled);
        w.WriteBool(_newHalt);
        w.WriteByte(_reloadValue);
        w.WriteByte(_previousCounter);
    }

    public void Restore(StateReader r)
    {
        Counter = r.ReadByte();
        Halt = r.ReadBool();
        Enabled = r.ReadBool();
        _newHalt = r.ReadBool();
        _reloadValue = r.ReadByte();
        _previousCounter = r.ReadByte();
    }
}
