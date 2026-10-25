using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>
/// "APU DMC": 1-bit delta playback. When its one-byte sample buffer empties, the memory reader asks the CPU
/// for a DMA read, which stalls the CPU (see <see cref="Cpu.Cpu6502"/>).
/// </summary>
internal sealed class DmcChannel(ushort[] rates)
{
    private bool _irqEnabled;
    private bool _loop;
    private ushort _rate = rates[0];
    private ushort _timer;

    private ushort _sampleAddress = 0xC000;
    private ushort _sampleLength = 1;
    private ushort _currentAddress;
    private ushort _bytesRemaining;

    private byte _buffer;
    private bool _bufferEmpty = true;
    private bool _dmaRequested;
    private int _startDelay;

    private byte _shift;
    private byte _bitsRemaining = 8;
    private bool _silence = true;

    public byte Level { get; private set; }

    public bool IrqFlag { get; set; }

    public bool IsActive => _bytesRemaining > 0;

    /// <summary>Set when the memory reader needs a byte; the APU forwards it to the CPU.</summary>
    public bool TakeDmaRequest()
    {
        var requested = _dmaRequested;
        _dmaRequested = false;
        return requested;
    }

    public ushort DmaAddress => _currentAddress;

    public void WriteControl(byte value)
    {
        _irqEnabled = (value & 0x80) != 0;
        _loop = (value & 0x40) != 0;
        _rate = rates[value & 0x0F];
        if (!_irqEnabled)
        {
            IrqFlag = false;
        }
    }

    public void WriteLevel(byte value) => Level = (byte)(value & 0x7F);

    public void WriteAddress(byte value) => _sampleAddress = (ushort)(0xC000 | (value << 6));

    public void WriteLength(byte value) => _sampleLength = (ushort)((value << 4) | 1);

    /// <summary>$4015 bit 4. Enabling with nothing left restarts the sample after a short delay (2-3 cycles).</summary>
    public void SetEnabled(bool enabled, long cpuCycle)
    {
        IrqFlag = false;
        if (!enabled)
        {
            _bytesRemaining = 0;
            _startDelay = 0;
        }
        else if (_bytesRemaining == 0)
        {
            Restart();
            _startDelay = (cpuCycle & 1) == 0 ? 2 : 3;
        }
    }

    /// <summary>Delivers the byte the CPU fetched by DMA.</summary>
    public void OnDmaRead(byte value)
    {
        _buffer = value;
        _bufferEmpty = false;
        _currentAddress = _currentAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(_currentAddress + 1);
        if (--_bytesRemaining == 0)
        {
            if (_loop)
            {
                Restart();
            }
            else if (_irqEnabled)
            {
                IrqFlag = true;
            }
        }
    }

    /// <summary>Clocked every CPU cycle.</summary>
    public void Clock()
    {
        if (_startDelay > 0 && --_startDelay == 0)
        {
            RequestByteIfNeeded();
        }

        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = (ushort)(_rate - 1);
        if (!_silence)
        {
            if ((_shift & 1) != 0)
            {
                if (Level <= 125)
                {
                    Level += 2;
                }
            }
            else if (Level >= 2)
            {
                Level -= 2;
            }
        }

        _shift >>= 1;
        if (--_bitsRemaining == 0)
        {
            _bitsRemaining = 8;
            if (_bufferEmpty)
            {
                _silence = true;
            }
            else
            {
                _silence = false;
                _shift = _buffer;
                _bufferEmpty = true;
                RequestByteIfNeeded();
            }
        }
    }

    private void RequestByteIfNeeded()
    {
        if (_bufferEmpty && _bytesRemaining > 0)
        {
            _dmaRequested = true;
        }
    }

    private void Restart()
    {
        _currentAddress = _sampleAddress;
        _bytesRemaining = _sampleLength;
    }

    public void Save(StateWriter w)
    {
        w.WriteBool(_irqEnabled);
        w.WriteBool(_loop);
        w.WriteUInt16(_rate);
        w.WriteUInt16(_timer);
        w.WriteUInt16(_sampleAddress);
        w.WriteUInt16(_sampleLength);
        w.WriteUInt16(_currentAddress);
        w.WriteUInt16(_bytesRemaining);
        w.WriteByte(_buffer);
        w.WriteBool(_bufferEmpty);
        w.WriteBool(_dmaRequested);
        w.WriteInt32(_startDelay);
        w.WriteByte(_shift);
        w.WriteByte(_bitsRemaining);
        w.WriteBool(_silence);
        w.WriteByte(Level);
        w.WriteBool(IrqFlag);
    }

    public void Restore(StateReader r)
    {
        _irqEnabled = r.ReadBool();
        _loop = r.ReadBool();
        _rate = r.ReadUInt16();
        _timer = r.ReadUInt16();
        _sampleAddress = r.ReadUInt16();
        _sampleLength = r.ReadUInt16();
        _currentAddress = r.ReadUInt16();
        _bytesRemaining = r.ReadUInt16();
        _buffer = r.ReadByte();
        _bufferEmpty = r.ReadBool();
        _dmaRequested = r.ReadBool();
        _startDelay = r.ReadInt32();
        _shift = r.ReadByte();
        _bitsRemaining = r.ReadByte();
        _silence = r.ReadBool();
        Level = r.ReadByte();
        IrqFlag = r.ReadBool();
    }
}
