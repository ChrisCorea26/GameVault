using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Sega;

/// <summary>
/// Texas Instruments SN76489 PSG, as built into the SG-1000 and the Master System's VDP (SPEC §7.11, Phase 11):
/// three square-wave channels and a noise channel, each with 16 attenuation steps of 2 dB. Clocked with the
/// Z80 (3.579545 MHz NTSC) and divided by 16 internally. The noise shift register is the Sega variant: 16 bits,
/// white noise tapped from bits 0 and 3 (SMS Power!: "SN76489").
/// </summary>
public sealed class Sn76489 : IStateful
{
    private static readonly StateTag Tag = StateTag.From("PSG ");

    /// <summary>Output level per attenuation step: 2 dB apart, 15 is silent. Four channels at full volume stay below 1.</summary>
    private static readonly float[] Volumes = BuildVolumes();

    private readonly ushort[] _tone = new ushort[3];
    private readonly byte[] _attenuation = [0x0F, 0x0F, 0x0F, 0x0F];
    private readonly int[] _counter = new int[4];
    private readonly bool[] _output = new bool[3];
    private byte _noiseControl;
    private ushort _lfsr = 0x8000;
    private bool _noiseFlip;
    private int _latchedChannel;
    private bool _latchedVolume;
    private int _divider;
    private int _frameClock;
    private float _lastLevel;

    public IAudioSink? AudioSink { get; set; }

    public void Power()
    {
        Array.Clear(_tone);
        Array.Fill(_attenuation, (byte)0x0F);
        Array.Clear(_counter);
        Array.Clear(_output);
        _noiseControl = 0;
        _lfsr = 0x8000;
        _noiseFlip = false;
        _latchedChannel = 0;
        _latchedVolume = false;
        _divider = 0;
        _frameClock = 0;
        _lastLevel = 0;
    }

    /// <summary>A byte written to the PSG port ($7E/$7F on the SMS).</summary>
    public void Write(byte value)
    {
        if ((value & 0x80) != 0)
        {
            _latchedChannel = (value >> 5) & 3;
            _latchedVolume = (value & 0x10) != 0;
            SetLow(value & 0x0F);
        }
        else if (_latchedVolume || _latchedChannel == 3)
        {
            SetLow(value & 0x0F);
        }
        else
        {
            // A data byte sets a tone register's upper six bits.
            _tone[_latchedChannel] = (ushort)((_tone[_latchedChannel] & 0x0F) | (value & 0x3F) << 4);
        }
    }

    private void SetLow(int data)
    {
        if (_latchedVolume)
        {
            _attenuation[_latchedChannel] = (byte)data;
        }
        else if (_latchedChannel == 3)
        {
            _noiseControl = (byte)(data & 7);
            _lfsr = 0x8000; // writing the noise register resets the shift register
        }
        else
        {
            _tone[_latchedChannel] = (ushort)((_tone[_latchedChannel] & 0x3F0) | data);
        }
    }

    /// <summary>Advances by <paramref name="cycles"/> Z80 cycles.</summary>
    public void Run(int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            if (++_divider == 16)
            {
                _divider = 0;
                Tick();
            }

            _frameClock++;
        }
    }

    private void Tick()
    {
        for (var channel = 0; channel < 3; channel++)
        {
            if (--_counter[channel] <= 0)
            {
                _counter[channel] = _tone[channel];
                _output[channel] = !_output[channel];
            }
        }

        if (--_counter[3] <= 0)
        {
            _counter[3] = (_noiseControl & 3) switch
            {
                0 => 0x10,
                1 => 0x20,
                2 => 0x40,
                _ => _tone[2],
            };
            _noiseFlip = !_noiseFlip;
            if (_noiseFlip)
            {
                var feedback = (_noiseControl & 4) != 0
                    ? ((_lfsr ^ (_lfsr >> 3)) & 1) // white noise: bits 0 and 3
                    : _lfsr & 1;                   // periodic noise
                _lfsr = (ushort)((_lfsr >> 1) | (feedback << 15));
            }
        }

        if (AudioSink is { } sink)
        {
            var level = 0f;
            for (var channel = 0; channel < 3; channel++)
            {
                // A period of 0 or 1 holds the output high: games play samples by changing the volume.
                if (_tone[channel] <= 1 || _output[channel])
                {
                    level += Volumes[_attenuation[channel]];
                }
            }

            if ((_lfsr & 1) != 0)
            {
                level += Volumes[_attenuation[3]];
            }

            if (level != _lastLevel)
            {
                sink.AddDelta(_frameClock, level - _lastLevel);
                _lastLevel = level;
            }
        }
    }

    /// <summary>Closes the audio frame; returns its length in Z80 cycles.</summary>
    public int EndAudioFrame()
    {
        var clocks = _frameClock;
        AudioSink?.EndFrame(clocks);
        _frameClock = 0;
        return clocks;
    }

    private static float[] BuildVolumes()
    {
        var table = new float[16];
        for (var i = 0; i < 15; i++)
        {
            table[i] = 0.22f * MathF.Pow(10, -0.1f * i);
        }

        return table;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        for (var i = 0; i < 3; i++)
        {
            writer.WriteUInt16(_tone[i]);
            writer.WriteBool(_output[i]);
        }

        for (var i = 0; i < 4; i++)
        {
            writer.WriteByte(_attenuation[i]);
            writer.WriteInt32(_counter[i]);
        }

        writer.WriteByte(_noiseControl);
        writer.WriteUInt16(_lfsr);
        writer.WriteBool(_noiseFlip);
        writer.WriteByte((byte)_latchedChannel);
        writer.WriteBool(_latchedVolume);
        writer.WriteByte((byte)_divider);
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        for (var i = 0; i < 3; i++)
        {
            _tone[i] = reader.ReadUInt16();
            _output[i] = reader.ReadBool();
        }

        for (var i = 0; i < 4; i++)
        {
            _attenuation[i] = reader.ReadByte();
            _counter[i] = reader.ReadInt32();
        }

        _noiseControl = reader.ReadByte();
        _lfsr = reader.ReadUInt16();
        _noiseFlip = reader.ReadBool();
        _latchedChannel = reader.ReadByte();
        _latchedVolume = reader.ReadBool();
        _divider = reader.ReadByte();
        reader.EndChunk();
    }
}
