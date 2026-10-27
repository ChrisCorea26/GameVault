namespace GameVault.Core.Audio;

/// <summary>
/// Turns APU amplitude changes (stamped in CPU cycles) into output samples, typically at 48 kHz (SPEC §4.4), in
/// stereo: <see cref="AddDelta(int, float)"/> changes both channels (the NES, the SN76489, the Apple II speaker), and
/// <see cref="AddDelta(int, float, float)"/> each one (the YM2612, S-DSP, SPU, AI and SPU2).
/// </summary>
/// <remarks>
/// Each change is added as a band-limited step ("BLEP"): a windowed-sinc impulse, chosen from 64 sub-sample
/// phases and integrated at output time. Resampling from 1.79 MHz this way doesn't alias, and costs work
/// only for each change, not for every CPU cycle. The NES's own analog filters (NESdev Wiki: "APU Mixer")
/// follow: high-pass at 90 Hz and 440 Hz, low-pass at 14 kHz. The input clock rate can change per frame;
/// dynamic rate control uses that to nudge the ratio (SPEC §5.3).
/// Until a stereo change arrives only the left channel is integrated and the right copies it, so mono systems cost
/// what they did before stereo.
/// </remarks>
public sealed class BandLimitedResampler : IAudioSink
{
    private const int Phases = 64;
    private const int Taps = 16;
    private const double Cutoff = 0.90; // fraction of the output Nyquist frequency
    private const int MaxFrameSamples = 8192;

    private static readonly float[][] Kernel = BuildKernel();

    private readonly float[] _left = new float[MaxFrameSamples + Taps + 1];
    private readonly float[] _right = new float[MaxFrameSamples + Taps + 1];
    private readonly float[] _output; // interleaved left, right
    private readonly int _sampleRate;
    private double _samplesPerClock;
    private double _offset;
    private int _available;
    private bool _stereo;
    private float _integratorLeft;
    private float _integratorRight;

    // Filter state, one per channel
    private readonly float _hp90;
    private readonly float _hp440;
    private readonly float _lp14k;
    private FilterState _filterLeft;
    private FilterState _filterRight;

    public BandLimitedResampler(double clockRate, int sampleRate = 48000)
    {
        _sampleRate = sampleRate;
        _output = new float[MaxFrameSamples * 4 * 2];
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

    /// <summary>Frames (a left and a right sample) ready to read.</summary>
    public int Available => _available;

    /// <summary>True once a change has reached one channel only; until then both channels carry the same signal.</summary>
    public bool IsStereo => _stereo;

    /// <summary>A change on both channels at once (a mono source).</summary>
    public void AddDelta(int clock, float delta)
    {
        if (!Locate(clock, out var index, out var kernel))
        {
            return;
        }

        AddStep(_left.AsSpan(index, Taps), kernel, delta);
        if (_stereo)
        {
            AddStep(_right.AsSpan(index, Taps), kernel, delta);
        }
    }

    /// <summary>A change of <paramref name="left"/> on the left channel and <paramref name="right"/> on the right.</summary>
    public void AddDelta(int clock, float left, float right)
    {
        if (!Locate(clock, out var index, out var kernel))
        {
            return;
        }

        if (!_stereo)
        {
            // Until now the right channel has been the left one: give it its own copy, then carry on in stereo.
            Array.Copy(_left, _right, _left.Length);
            _integratorRight = _integratorLeft;
            _filterRight = _filterLeft;
            _stereo = true;
        }

        if (left != 0)
        {
            AddStep(_left.AsSpan(index, Taps), kernel, left);
        }

        if (right != 0)
        {
            AddStep(_right.AsSpan(index, Taps), kernel, right);
        }
    }

    public void EndFrame(int clocks)
    {
        var end = _offset + clocks * _samplesPerClock;
        var count = Math.Min((int)end, MaxFrameSamples);
        if ((_available + count) * 2 > _output.Length)
        {
            // Nobody is reading (e.g. muted): keep only the newest audio.
            _available = 0;
        }

        var output = _available * 2;
        for (var i = 0; i < count; i++)
        {
            _integratorLeft += _left[i];
            var left = Filter(_integratorLeft, ref _filterLeft) * Volume;
            float right;
            if (_stereo)
            {
                _integratorRight += _right[i];
                right = Filter(_integratorRight, ref _filterRight) * Volume;
            }
            else
            {
                right = left;
            }

            _output[output++] = left;
            _output[output++] = right;
        }

        _available += count;

        // Carry the tails of impulses that extend past this frame into the next one.
        Carry(_left, count);
        if (_stereo)
        {
            Carry(_right, count);
        }

        _offset = end - count;
    }

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length / 2 frames out as interleaved left and right samples and
    /// returns how many frames.
    /// </summary>
    public int ReadStereo(Span<float> destination)
    {
        var count = Math.Min(destination.Length / 2, _available);
        _output.AsSpan(0, count * 2).CopyTo(destination);
        _output.AsSpan(count * 2, (_available - count) * 2).CopyTo(_output);
        _available -= count;
        return count;
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length frames out mixed to mono (the average of left and right) and returns how many.</summary>
    public int Read(Span<float> destination)
    {
        var count = Math.Min(destination.Length, _available);
        for (var i = 0; i < count; i++)
        {
            destination[i] = (_output[2 * i] + _output[2 * i + 1]) * 0.5f;
        }

        _output.AsSpan(count * 2, (_available - count) * 2).CopyTo(_output);
        _available -= count;
        return count;
    }

    private bool Locate(int clock, out int index, out float[] kernel)
    {
        var position = _offset + clock * _samplesPerClock;
        index = (int)position;
        if ((uint)index >= MaxFrameSamples)
        {
            kernel = Kernel[0];
            return false; // frame far longer than any real one; drop rather than overrun
        }

        kernel = Kernel[(int)((position - index) * Phases)];
        return true;
    }

    private static void AddStep(Span<float> target, float[] kernel, float delta)
    {
        for (var t = 0; t < Taps; t++)
        {
            target[t] += delta * kernel[t];
        }
    }

    private static void Carry(float[] buffer, int count)
    {
        Array.Copy(buffer, count, buffer, 0, Taps);
        Array.Clear(buffer, Taps, buffer.Length - Taps);
    }

    private float Filter(float x, ref FilterState state)
    {
        if (!ApplyNesFilters)
        {
            return x;
        }

        var hp1 = _hp90 * (state.Hp90PrevOut + x - state.Hp90PrevIn);
        state.Hp90PrevIn = x;
        state.Hp90PrevOut = hp1;

        var hp2 = _hp440 * (state.Hp440PrevOut + hp1 - state.Hp440PrevIn);
        state.Hp440PrevIn = hp1;
        state.Hp440PrevOut = hp2;

        state.LpPrevOut += _lp14k * (hp2 - state.LpPrevOut);
        return state.LpPrevOut;
    }

    private struct FilterState
    {
        public float Hp90PrevIn;
        public float Hp90PrevOut;
        public float Hp440PrevIn;
        public float Hp440PrevOut;
        public float LpPrevOut;
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
