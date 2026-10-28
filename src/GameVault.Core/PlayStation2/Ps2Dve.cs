using GameVault.Core.State;

namespace GameVault.Core.PlayStation2;

/// <summary>
/// The DVE, the video encoder behind the GS, as the EE sees it at $1A000000 (a docs/bugs.md GV-024 boot log: the BIOS's
/// "Initialize GS" sends it commands and waits on its status at $1A000006). Modelled on PCSX2's <c>ba0R16</c> and
/// <c>ba0W16</c> (read for facts only): 16-bit registers indexed by their byte offset; $00 runs a command (bit 7 starts
/// it, the low bits give its length) with $02 the mode (43h write, 42h read; 41h and 4Fh are errors), $10 the DVE
/// register and $12 onwards the data; $0A powers the encoder (0 is off: the error bit); $06 is the status, bit 1 ready
/// and bit 0 error, ready coming a few reads after a command starts. The encoder's own registers have no effect on the
/// picture here.
/// </summary>
public sealed class Ps2Dve : IStateful
{
    public const uint Base = 0x1A00_0000;
    public const uint Size = 0x1_0000;

    private readonly ushort[] _registers = new ushort[0x100];
    private readonly ushort[] _encoder = new ushort[0x100];
    private int _current;
    private bool _executing;
    private bool _error;

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_encoder);
        _current = 0;
        _executing = _error = false;
    }

    public ushort Read16(uint address)
    {
        if ((address & 0xFFFF) == 0x6)
        {
            var status = (ushort)(_registers[0x6] & 2);
            if (_error)
            {
                status |= 1;
            }

            if (_registers[0x6] < 3 && _executing)
            {
                _registers[0x6]++;
            }
            else
            {
                _executing = false;
            }

            return status;
        }

        return _registers[address & 0x1F];
    }

    public void Write16(uint address, ushort value)
    {
        var offset = (int)(address & 0xFF);
        if (offset == 0x6)
        {
            _registers[0x6] &= unchecked((ushort)~3);
        }
        else
        {
            _registers[offset] = value;
        }

        switch (offset)
        {
            case 0x0:
                Execute();
                break;
            case 0xA:
                _error = value == 0;
                break;
        }
    }

    private void Execute()
    {
        var command = _registers[0x0];
        var mode = _registers[0x2];
        if (mode is 0x4F or 0x41)
        {
            _error = true;
            return;
        }

        if ((command & 0x80) == 0)
        {
            return;
        }

        var size = command & 0xF;
        if (mode == 0x43)
        {
            _current = _registers[0x10] & 0xFF;
            for (var i = 0; i < size - 1; i++)
            {
                _encoder[_current] = _registers[(0x12 + i) & 0xFF];
            }
        }
        else if (mode == 0x42)
        {
            for (var i = 0; i < size; i++)
            {
                _registers[(0x10 + i) & 0xFF] = _encoder[_current];
            }
        }
        else
        {
            return;
        }

        _executing = true;
        _error = false;
    }

    public void SaveState(StateWriter writer)
    {
        foreach (var value in _registers)
        {
            writer.WriteUInt16(value);
        }

        foreach (var value in _encoder)
        {
            writer.WriteUInt16(value);
        }

        writer.WriteInt32(_current);
        writer.WriteBool(_executing);
        writer.WriteBool(_error);
    }

    public void LoadState(StateReader reader)
    {
        for (var i = 0; i < _registers.Length; i++)
        {
            _registers[i] = reader.ReadUInt16();
        }

        for (var i = 0; i < _encoder.Length; i++)
        {
            _encoder[i] = reader.ReadUInt16();
        }

        _current = reader.ReadInt32() & 0xFF;
        _executing = reader.ReadBool();
        _error = reader.ReadBool();
    }
}
