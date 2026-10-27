using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Sega;

/// <summary>
/// The Master System's FM Sound Unit (outstanding-work plan §8): a YM2413 on ports $F0 (register), $F1 (data) and
/// $F2 (audio control, which games read back to detect the unit), built into the Japanese Master System. Nine
/// channels, or six and the five rhythm sounds. A sample every 72 Z80 cycles (49,716 Hz). The FM engine is
/// <see cref="Opll"/>, shared with the NES's VRC7.
/// </summary>
/// <remarks>
/// The built-in instruments are the YM2413's, as dumped from the chip and published with emu2413 (Mitsutaka Okazaki,
/// MIT licence; the melodic table by plgDavid), with emu2413's rhythm instruments. References: SMS Power! "FM Sound
/// Unit"; the YM2413 application manual.
/// </remarks>
public sealed class Ym2413 : IStateful
{
    public const int CyclesPerSample = 72;
    private const float ChannelScale = 0.075f;

    private static readonly StateTag Tag = StateTag.From("OPLL");

    private static readonly byte[,] Patches =
    {
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, // 0: custom (registers $00-$07)
        { 0x71, 0x61, 0x1E, 0x17, 0xD0, 0x78, 0x00, 0x17 }, // 1: violin
        { 0x13, 0x41, 0x1A, 0x0D, 0xD8, 0xF7, 0x23, 0x13 }, // 2: guitar
        { 0x13, 0x01, 0x99, 0x00, 0xF2, 0xC4, 0x21, 0x23 }, // 3: piano
        { 0x11, 0x61, 0x0E, 0x07, 0x8D, 0x64, 0x70, 0x27 }, // 4: flute
        { 0x32, 0x21, 0x1E, 0x06, 0xE1, 0x76, 0x01, 0x28 }, // 5: clarinet
        { 0x31, 0x22, 0x16, 0x05, 0xE0, 0x71, 0x00, 0x18 }, // 6: oboe
        { 0x21, 0x61, 0x1D, 0x07, 0x82, 0x81, 0x11, 0x07 }, // 7: trumpet
        { 0x33, 0x21, 0x2D, 0x13, 0xB0, 0x70, 0x00, 0x07 }, // 8: organ
        { 0x61, 0x61, 0x1B, 0x06, 0x64, 0x65, 0x10, 0x17 }, // 9: horn
        { 0x41, 0x61, 0x0B, 0x18, 0x85, 0xF0, 0x81, 0x07 }, // 10: synthesizer
        { 0x33, 0x01, 0x83, 0x11, 0xEA, 0xEF, 0x10, 0x04 }, // 11: harpsichord
        { 0x17, 0xC1, 0x24, 0x07, 0xF8, 0xF8, 0x22, 0x12 }, // 12: vibraphone
        { 0x61, 0x50, 0x0C, 0x05, 0xD2, 0xF5, 0x40, 0x42 }, // 13: synthesizer bass
        { 0x01, 0x01, 0x55, 0x03, 0xE9, 0x90, 0x03, 0x02 }, // 14: acoustic bass
        { 0x41, 0x41, 0x89, 0x03, 0xF1, 0xE4, 0xC0, 0x13 }, // 15: electric guitar
        { 0x01, 0x01, 0x18, 0x0F, 0xDF, 0xF8, 0x6A, 0x6D }, // rhythm: bass drum
        { 0x01, 0x01, 0x00, 0x00, 0xC8, 0xD8, 0xA7, 0x68 }, // rhythm: hi-hat (modulator), snare drum (carrier)
        { 0x05, 0x01, 0x00, 0x00, 0xF8, 0xAA, 0x59, 0x55 }, // rhythm: tom-tom (modulator), top cymbal (carrier)
    };

    private Opll _opll = new(Patches, 9);
    private int _divider;
    private byte _control;

    /// <summary>The unit's output, about ±1 at full volume when every channel plays.</summary>
    public float Output { get; private set; }

    /// <summary>
    /// $F2: bit 0 turns the FM output on (the Japanese Master System's audio switch; the PSG stays on). Games detect
    /// the unit by reading back what they wrote.
    /// </summary>
    public byte Control => _control;

    public void Power()
    {
        _opll = new Opll(Patches, 9);
        _divider = 0;
        _control = 0;
        Output = 0;
    }

    public void WriteRegister(byte value) => _opll.SelectRegister(value);

    public void WriteData(byte value) => _opll.WriteData(value);

    public void WriteControl(byte value) => _control = (byte)(value & 0x03);

    /// <summary>Runs one Z80 cycle; returns true when a new sample is ready in <see cref="Output"/>.</summary>
    public bool Clock()
    {
        if (++_divider < CyclesPerSample)
        {
            return false;
        }

        _divider = 0;
        var sample = (float)(_opll.Step() * ChannelScale);
        Output = (_control & 1) != 0 ? sample : 0;
        return true;
    }

    public void SaveState(StateWriter writer)
    {
        writer.BeginChunk(Tag, 1);
        _opll.SaveRegisters(writer);
        _opll.SaveVoices(writer);
        _opll.SaveRhythm(writer);
        writer.WriteInt32(_divider);
        writer.WriteByte(_control);
        writer.WriteInt32(BitConverter.SingleToInt32Bits(Output));
        writer.EndChunk();
    }

    public void LoadState(StateReader reader)
    {
        reader.BeginChunk(Tag);
        _opll.LoadRegisters(reader);
        _opll.LoadVoices(reader);
        _opll.LoadRhythm(reader);
        _divider = reader.ReadInt32() % CyclesPerSample;
        _control = (byte)(reader.ReadByte() & 3);
        Output = BitConverter.Int32BitsToSingle(reader.ReadInt32());
        reader.EndChunk();
    }
}
