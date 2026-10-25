using GameVault.Core.Apu;
using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// MMC5 sound (NESdev Wiki: "MMC5 audio"): two pulse channels like the 2A03's but without sweep, whose
/// envelopes and length counters run from the MMC5's own 240 Hz sequencer, plus an 8-bit PCM register.
/// </summary>
public sealed class Mmc5Audio : IExpansionAudio
{
    /// <summary>CPU cycles per sequencer step (about 240 Hz on NTSC).</summary>
    private const int SequencerPeriod = 7457;

    /// <summary>Per pulse output unit; the MMC5 pulses mix at about the 2A03 pulses' level.</summary>
    private const float PulseScale = 0.00752f;

    private const float PcmScale = 0.0011f;

    private readonly PulseChannel _pulse1 = new(isPulse1: false);
    private readonly PulseChannel _pulse2 = new(isPulse1: false);
    private int _sequencer;
    private bool _odd;
    private byte _pcm;
    private bool _pcmReadMode;

    public float Output { get; private set; }

    public void Write(ushort address, byte value)
    {
        switch (address)
        {
            case 0x5000:
                _pulse1.WriteControl(value);
                break;
            case 0x5002:
                _pulse1.WriteTimerLow(value);
                break;
            case 0x5003:
                _pulse1.WriteTimerHigh(value);
                break;
            case 0x5004:
                _pulse2.WriteControl(value);
                break;
            case 0x5006:
                _pulse2.WriteTimerLow(value);
                break;
            case 0x5007:
                _pulse2.WriteTimerHigh(value);
                break;
            case 0x5010:
                _pcmReadMode = (value & 1) != 0;
                break;
            case 0x5011:
                if (!_pcmReadMode && value != 0)
                {
                    _pcm = value; // writing 0 is ignored in write mode
                }

                break;
            case 0x5015:
                _pulse1.Length.SetEnabled((value & 1) != 0);
                _pulse2.Length.SetEnabled((value & 2) != 0);
                break;
        }

        // The 2A03 defers length reloads and halts to the end of its cycle to get the frame-counter
        // race right; the MMC5's sequencer is separate, so writes take effect at once.
        _pulse1.Length.Commit();
        _pulse2.Length.Commit();
    }

    /// <summary>$5015: which length counters are still running.</summary>
    public byte ReadStatus() => (byte)((_pulse1.Length.Counter > 0 ? 1 : 0) | (_pulse2.Length.Counter > 0 ? 2 : 0));

    public void Clock()
    {
        if (_odd)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
        }

        _odd = !_odd;
        if (++_sequencer >= SequencerPeriod)
        {
            _sequencer = 0;
            _pulse1.Envelope.Clock();
            _pulse2.Envelope.Clock();
            _pulse1.Length.Clock();
            _pulse2.Length.Clock();
        }

        _pulse1.Length.Commit();
        _pulse2.Length.Commit();
        Output = (_pulse1.Output + _pulse2.Output) * PulseScale + _pcm * PcmScale;
    }

    public void Save(StateWriter writer)
    {
        _pulse1.Save(writer);
        _pulse2.Save(writer);
        writer.WriteInt32(_sequencer);
        writer.WriteBool(_odd);
        writer.WriteByte(_pcm);
        writer.WriteBool(_pcmReadMode);
    }

    public void Load(StateReader reader)
    {
        _pulse1.Restore(reader);
        _pulse2.Restore(reader);
        _sequencer = reader.ReadInt32();
        _odd = reader.ReadBool();
        _pcm = reader.ReadByte();
        _pcmReadMode = reader.ReadBool();
    }
}
