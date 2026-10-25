using GameVault.Core.Audio;
using GameVault.Core.Cpu;
using GameVault.Core.State;

namespace GameVault.Core.Apu;

/// <summary>
/// The 2A03's audio processing unit: two pulses, triangle, noise, DMC and the frame counter
/// (NESdev Wiki: "APU", "APU Frame Counter"). Ticks at the end of every CPU cycle, after any register
/// write in that cycle.
/// </summary>
public sealed class Apu2A03 : IStateful, IDmcDmaSource
{
    private static readonly StateTag Tag = StateTag.From("APU ");

    // "APU Frame Counter": CPU cycles from the start of the sequence for each step, NTSC.
    private static readonly int[][] StepCycles =
    [
        [7457, 14913, 22371, 29828, 29829, 29830],
        [7457, 14913, 22371, 29829, 37281, 37282],
    ];

    private const int None = 0, Quarter = 1, Half = 2;

    private static readonly int[][] StepClocks =
    [
        [Quarter, Half, Quarter, None, Half, None],
        [Quarter, Half, Quarter, None, Half, None],
    ];

    private readonly PulseChannel _pulse1 = new(isPulse1: true);
    private readonly PulseChannel _pulse2 = new(isPulse1: false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise;
    private readonly DmcChannel _dmc;

    // Frame counter
    private int _mode;
    private int _step;
    private int _sequenceCycle;
    private bool _irqInhibit;
    private int _pendingMode = -1;
    private int _writeDelay;
    private int _blockClock;
    private byte _last4017;

    private long _cycle;
    private int _frameClock;
    private float _lastOutput;

    public Apu2A03(bool pal = false)
    {
        _noise = new NoiseChannel(pal ? ApuTables.NoisePal : ApuTables.NoiseNtsc);
        _dmc = new DmcChannel(pal ? ApuTables.DmcPal : ApuTables.DmcNtsc);
    }

    /// <summary>Where amplitude changes go; null to skip audio (for example during run-ahead frames).</summary>
    public IAudioSink? AudioSink { get; set; }

    public IExpansionAudio? ExpansionAudio { get; set; }

    public bool FrameIrq { get; private set; }

    public bool DmcIrq => _dmc.IrqFlag;

    /// <summary>The CPU cycle count, used for the parity-dependent $4017 and DMC start delays.</summary>
    public Func<long>? CpuCycle { get; set; }

    ushort IDmcDmaSource.DmcDmaAddress => _dmc.DmaAddress;

    void IDmcDmaSource.OnDmcDmaRead(byte value) => _dmc.OnDmaRead(value);

    /// <summary>True once per DMC buffer refill; the bus then asks the CPU for a DMA read.</summary>
    public bool TakeDmcDmaRequest() => _dmc.TakeDmaRequest();

    public void Power()
    {
        foreach (var address in new ushort[] { 0x4000, 0x4001, 0x4002, 0x4003, 0x4004, 0x4005, 0x4006, 0x4007, 0x4008, 0x400A, 0x400B, 0x400C, 0x400E, 0x400F, 0x4010, 0x4011, 0x4012, 0x4013 })
        {
            WriteRegister(address, 0);
        }

        WriteRegister(0x4015, 0);
        _mode = 0;
        _last4017 = 0;
        Reset();
    }

    /// <summary>
    /// "After reset or power-up, the APU acts as if $4017 were written with $00 from 9 to 12 clocks before
    /// the first instruction begins." Reset keeps the mode and silences the channels via $4015.
    /// </summary>
    public void Reset()
    {
        WriteRegister(0x4015, 0);
        _step = 0;
        _sequenceCycle = 0;
        FrameIrq = false;
        _blockClock = 0;

        // Reset rewrites $4017 with the last value written (blargg apu_reset/4017_written). The rewrite
        // takes effect during the CPU's 7-cycle reset sequence, 9-12 cycles before the first instruction
        // counting the usual write delay (blargg 09.reset_timing).
        _irqInhibit = (_last4017 & 0x40) != 0;
        _pendingMode = (_last4017 & 0x80) != 0 ? 1 : 0;
        _writeDelay = 1;
    }

    public byte ReadStatus(byte openBus)
    {
        var value = (byte)((openBus & 0x20)
            | (_pulse1.Length.Counter > 0 ? 0x01 : 0)
            | (_pulse2.Length.Counter > 0 ? 0x02 : 0)
            | (_triangle.Length.Counter > 0 ? 0x04 : 0)
            | (_noise.Length.Counter > 0 ? 0x08 : 0)
            | (_dmc.IsActive ? 0x10 : 0)
            | (FrameIrq ? 0x40 : 0)
            | (_dmc.IrqFlag ? 0x80 : 0));
        FrameIrq = false;
        return value;
    }

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000: _pulse1.WriteControl(value); break;
            case 0x4001: _pulse1.WriteSweep(value); break;
            case 0x4002: _pulse1.WriteTimerLow(value); break;
            case 0x4003: _pulse1.WriteTimerHigh(value); break;
            case 0x4004: _pulse2.WriteControl(value); break;
            case 0x4005: _pulse2.WriteSweep(value); break;
            case 0x4006: _pulse2.WriteTimerLow(value); break;
            case 0x4007: _pulse2.WriteTimerHigh(value); break;
            case 0x4008: _triangle.WriteLinear(value); break;
            case 0x400A: _triangle.WriteTimerLow(value); break;
            case 0x400B: _triangle.WriteTimerHigh(value); break;
            case 0x400C: _noise.WriteControl(value); break;
            case 0x400E: _noise.WritePeriod(value); break;
            case 0x400F: _noise.WriteLength(value); break;
            case 0x4010: _dmc.WriteControl(value); break;
            case 0x4011: _dmc.WriteLevel(value); break;
            case 0x4012: _dmc.WriteAddress(value); break;
            case 0x4013: _dmc.WriteLength(value); break;
            case 0x4015:
                _pulse1.Length.SetEnabled((value & 0x01) != 0);
                _pulse2.Length.SetEnabled((value & 0x02) != 0);
                _triangle.Length.SetEnabled((value & 0x04) != 0);
                _noise.Length.SetEnabled((value & 0x08) != 0);
                _dmc.SetEnabled((value & 0x10) != 0, CpuCycle?.Invoke() ?? _cycle);
                break;
            case 0x4017:
                _last4017 = value;

                // The new mode takes effect 3 or 4 CPU cycles later, depending on the APU cycle phase.
                _pendingMode = (value & 0x80) != 0 ? 1 : 0;
                _writeDelay = ((CpuCycle?.Invoke() ?? _cycle) & 1) == 1 ? 4 : 3;
                _irqInhibit = (value & 0x40) != 0;
                if (_irqInhibit)
                {
                    FrameIrq = false;
                }

                break;
        }
    }

    /// <summary>One CPU cycle.</summary>
    public void Tick()
    {
        ClockFrameCounter();

        _triangle.ClockTimer();
        if ((_cycle & 1) == 1)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
        }

        _dmc.Clock();
        ExpansionAudio?.Clock();

        _pulse1.Length.Commit();
        _pulse2.Length.Commit();
        _triangle.Length.Commit();
        _noise.Length.Commit();

        if (AudioSink is { } sink)
        {
            var output = Mix();
            if (output != _lastOutput)
            {
                sink.AddDelta(_frameClock, output - _lastOutput);
                _lastOutput = output;
            }
        }

        _frameClock++;
        _cycle++;
    }

    /// <summary>Closes the current audio frame and returns how many CPU cycles it lasted.</summary>
    public int EndAudioFrame()
    {
        var clocks = _frameClock;
        AudioSink?.EndFrame(clocks);
        _frameClock = 0;
        return clocks;
    }

    private float Mix()
    {
        var pulse = ApuTables.PulseMix[_pulse1.Output + _pulse2.Output];
        var tnd = ApuTables.TndMix[3 * _triangle.Output + 2 * _noise.Output + _dmc.Level];
        return pulse + tnd + (ExpansionAudio?.Output ?? 0f);
    }

    private void ClockFrameCounter()
    {
        _sequenceCycle++;
        if (_sequenceCycle == StepCycles[_mode][_step])
        {
            if (_mode == 0 && _step >= 3 && !_irqInhibit)
            {
                FrameIrq = true;
            }

            var clock = StepClocks[_mode][_step];
            if (clock != None && _blockClock == 0)
            {
                ClockQuarterFrame();
                if (clock == Half)
                {
                    ClockHalfFrame();
                }

                _blockClock = 2;
            }

            if (++_step == 6)
            {
                _step = 0;
                _sequenceCycle = 0;
            }
        }

        if (_pendingMode >= 0 && --_writeDelay == 0)
        {
            _mode = _pendingMode;
            _pendingMode = -1;
            _step = 0;
            _sequenceCycle = 0;
            if (_mode == 1 && _blockClock == 0)
            {
                // Writing mode 1 clocks the quarter and half frame units immediately.
                ClockQuarterFrame();
                ClockHalfFrame();
                _blockClock = 2;
            }
        }

        if (_blockClock > 0)
        {
            _blockClock--;
        }
    }

    private void ClockQuarterFrame()
    {
        _pulse1.Envelope.Clock();
        _pulse2.Envelope.Clock();
        _noise.Envelope.Clock();
        _triangle.ClockLinear();
    }

    private void ClockHalfFrame()
    {
        _pulse1.Length.Clock();
        _pulse2.Length.Clock();
        _triangle.Length.Clock();
        _noise.Length.Clock();
        _pulse1.ClockSweep();
        _pulse2.ClockSweep();
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        _pulse1.Save(writer);
        _pulse2.Save(writer);
        _triangle.Save(writer);
        _noise.Save(writer);
        _dmc.Save(writer);
        writer.WriteInt32(_mode);
        writer.WriteInt32(_step);
        writer.WriteInt32(_sequenceCycle);
        writer.WriteBool(_irqInhibit);
        writer.WriteInt32(_pendingMode);
        writer.WriteInt32(_writeDelay);
        writer.WriteInt32(_blockClock);
        writer.WriteByte(_last4017);
        writer.WriteBool(FrameIrq);
        writer.WriteInt64(_cycle);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _pulse1.Restore(reader);
        _pulse2.Restore(reader);
        _triangle.Restore(reader);
        _noise.Restore(reader);
        _dmc.Restore(reader);
        _mode = reader.ReadInt32();
        _step = reader.ReadInt32();
        _sequenceCycle = reader.ReadInt32();
        _irqInhibit = reader.ReadBool();
        _pendingMode = reader.ReadInt32();
        _writeDelay = reader.ReadInt32();
        _blockClock = reader.ReadInt32();
        _last4017 = reader.ReadByte();
        FrameIrq = reader.ReadBool();
        _cycle = reader.ReadInt64();
        reader.EndChunk();

        // Audio output isn't state: restart the frame so the next delta is relative to "now".
        _frameClock = 0;
        _lastOutput = Mix();
    }
}
