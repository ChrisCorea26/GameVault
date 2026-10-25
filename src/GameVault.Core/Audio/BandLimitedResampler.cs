namespace GameVault.Core.Audio;

/// <summary>
/// Turns APU amplitude changes (stamped in CPU cycles) into output samples, typically at 48 kHz (SPEC §4.4).
/// </summary>
/// <remarks>
/// Each change is added as a band-limited step ("BLEP"): a windowed-sinc impulse, chosen from 64 sub-sample
/// phases and integrated at output time. Resampling from 1.79 MHz this way doesn't alias, and costs work
/// only for each change, not for every CPU cycle. The NES's own analog filters (NESdev Wiki: "APU Mixer")
/// follow: high-pass at 90 Hz and 440 Hz, low-pass at 14 kHz. The input clock rate can change per frame;
/// dynamic rate control uses that to nudge the ratio (SPEC §5.3).
/// </remarks>
public sealed class BandLimitedResampler : IAudioSink
{
    private const int Phases = 64;
    private const int Taps = 16;
    private const double Cutoff = 0.90; // fraction of the output Nyquist frequency
    private const int MaxFrameSamples = 8192;

    private static readonly float[][] Kernel = BuildKernel();

    private readonly float[] _buffer = new float[MaxFrameSamples + Taps + 1];
    private readonly float[] _output;
    private readonly int _sampleRate;
    private double _samplesPerClock;
    private double _offset;
    private int _available;
    private float _integrator;

    // Filter state
    private readonly float _hp90;
    private readonly float _hp440;
    private readonly float _lp14k;
    private float _hp90PrevIn;
    private float _hp90PrevOut;
    private float _hp440PrevIn;
    private float _hp440PrevOut;
    private float _lpPrevOut;

    public BandLimitedResampler(double clockRate, int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        _output = new float[MaxFrameSamples * 4];
        ClockRate = clockRate;

        var dt = 1.0 / sampleRate;
        _hp90 = (float)HighPassCoefficient(90, dt);
        _hp440 = (float)HighPassCoefficient(440, dt);
        _lp14k = (float)LowPassCoefficient(14000, dt);
    }

    /// <summary>Input clock in Hz (the CPU clock, possibly nudged by rate control).</summary>
    public double ClockRate
    {
        get => _sampleRate / _samplesPerClock;
        set => _samplesPerClock = _sampleRate / value;
    }

    public int SampleRate => _sampleRate;

    /// <summary>Apply the NES's analog filter chain. On by default; tests turn it off to see the raw signal.</summary>
    public bool ApplyNesFilters { get; set; } = true;

    /// <summary>Output gain applied after filtering.</summary>
    public float Volume { get; set; } = 1.0f;

    /// <summary>Samples ready to read.</summary>
    public int Available => _available;

    public void AddDelta(int clock, float delta)
    {
        var position = _offset + clock * _samplesPerClock;
        var index = (int)position;
        var phase = (int)((position - index) * Phases);
        if ((uint)index >= MaxFrameSamples)
        {
            return; // frame far longer than any real one; drop rather than overrun
        }

        var kernel = Kernel[phase];
        var target = _buffer.AsSpan(index, Taps);
        for (var t = 0; t < Taps; t++)
        {
            target[t] += delta * kernel[t];
        }
    }

    public void EndFrame(int clocks)
    {
        var end = _offset + clocks * _samplesPerClock;
        var count = Math.Min((int)end, MaxFrameSamples);
        if (_available + count > _output.Length)
        {
            // Nobody is reading (e.g. muted): keep only the newest audio.
            _available = 0;
        }

        for (var i = 0; i < count; i++)
        {
            _integrator += _buffer[i];
            _output[_available++] = Filter(_integrator) * Volume;
        }

        // Carry the tails of impulses that extend past this frame into the next one.
        Array.Copy(_buffer, count, _buffer, 0, Taps);
        Array.Clear(_buffer, Taps, _buffer.Length - Taps);
        _offset = end - count;
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length samples out and returns how many.</summary>
    public int Read(Span<float> destination)
    {
        var count = Math.Min(destination.Length, _available);
        _output.AsSpan(0, count).CopyTo(destination);
        _output.AsSpan(count, _available - count).CopyTo(_output);
        _available -= count;
        return count;
    }

    private float Filter(float x)
    {
        if (!ApplyNesFilters)
        {
            return x;
        }

        var hp1 = _hp90 * (_hp90PrevOut + x - _hp90PrevIn);
        _hp90PrevIn = x;
        _hp90PrevOut = hp1;

        var hp2 = _hp440 * (_hp440PrevOut + hp1 - _hp440PrevIn);
        _hp440PrevIn = hp1;
        _hp440PrevOut = hp2;

        _lpPrevOut += _lp14k * (hp2 - _lpPrevOut);
        return _lpPrevOut;
    }

    private static double HighPassCoefficient(double hz, double dt)
    {
        var rc = 1.0 / (2 * Math.PI * hz);
        return rc / (rc + dt);
    }

    private static double LowPassCoefficient(double hz, double dt)
    {
        var rc = 1.0 / (2 * Math.PI * hz);
        return dt / (rc + dt);
    }

    /// <summary>Windowed-sinc impulses (Blackman window), one per sub-sample phase, each summing to 1.</summary>
    private static float[][] BuildKernel()
    {
        var kernel = new float[Phases][];
        for (var p = 0; p < Phases; p++)
        {
            var row = new double[Taps];
            var center = Taps / 2.0 - 1 + (double)p / Phases;
            double sum = 0;
            for (var t = 0; t < Taps; t++)
            {
                var x = t - center;
                var sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * Cutoff * x) / (Math.PI * Cutoff * x);
                var w = (t - center + Taps / 2.0) / Taps; // 0..1 across the window
                var window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * w) + 0.08 * Math.Cos(4 * Math.PI * w);
                row[t] = sinc * Math.Max(0, window);
                sum += row[t];
            }

            kernel[p] = row.Select(v => (float)(v / sum)).ToArray();
        }

        return kernel;
    }
}
