using GameVault.Core.Audio;
using GameVault.Core.State;

namespace GameVault.Core.Cartridge.Mappers;

/// <summary>
/// VRC7 FM sound (NESdev Wiki: "VRC7 audio"), a YM2413 (OPLL) derivative.
/// <list type="bullet">
/// <item>Six two-operator channels (modulator → carrier, with modulator self-feedback).</item>
/// <item>15 built-in instruments plus one custom instrument in registers $00-$07.</item>
/// <item>Each operator has ADSR envelope, key scaling, half-sine waveform option, tremolo and vibrato.</item>
/// </list>
/// The chip produces a sample every 36 CPU cycles (about 49.7 kHz). The FM engine is <see cref="Opll"/>, shared with
/// the Master System's YM2413.
/// </summary>
public sealed class Vrc7Audio : IExpansionAudio
{
    private const int CyclesPerSample = 36;
    private const float ChannelScale = 0.075f;

    /// <summary>
    /// The VRC7's built-in instruments 1-15 (8 bytes each, same layout as registers $00-$07), as read from the
    /// chip and published on the NESdev Wiki ("VRC7 audio", instrument ROM).
    /// </summary>
    private static readonly byte[,] Patches =
    {
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, // 0: custom (registers $00-$07)
        { 0x03, 0x21, 0x05, 0x06, 0xE8, 0x81, 0x42, 0x27 },
        { 0x13, 0x41, 0x14, 0x0D, 0xD8, 0xF6, 0x23, 0x12 },
        { 0x11, 0x11, 0x08, 0x08, 0xFA, 0xB2, 0x20, 0x12 },
        { 0x31, 0x61, 0x0C, 0x07, 0xA8, 0x64, 0x61, 0x27 },
        { 0x32, 0x21, 0x1E, 0x06, 0xE1, 0x76, 0x01, 0x28 },
        { 0x02, 0x01, 0x06, 0x00, 0xA3, 0xE2, 0xF4, 0xF4 },
        { 0x21, 0x61, 0x1D, 0x07, 0x82, 0x81, 0x11, 0x07 },
        { 0x23, 0x21, 0x22, 0x17, 0xA2, 0x72, 0x01, 0x17 },
        { 0x35, 0x11, 0x25, 0x00, 0x40, 0x73, 0x72, 0x01 },
        { 0xB5, 0x01, 0x0F, 0x0F, 0xA8, 0xA5, 0x51, 0x02 },
        { 0x17, 0xC1, 0x24, 0x07, 0xF8, 0xF8, 0x22, 0x12 },
        { 0x71, 0x23, 0x11, 0x06, 0x65, 0x74, 0x18, 0x16 },
        { 0x01, 0x02, 0xD3, 0x05, 0xC9, 0x95, 0x03, 0x02 },
        { 0x61, 0x63, 0x0C, 0x00, 0x94, 0xC0, 0x33, 0xF6 },
        { 0x21, 0x72, 0x0D, 0x00, 0xC1, 0xD5, 0x56, 0x06 },
    };

    private readonly Opll _opll = new(Patches, 6);
    private int _divider;

    public float Output { get; private set; }

    public void SelectRegister(byte value) => _opll.SelectRegister(value);

    public void WriteData(byte value) => _opll.WriteData(value);

    public void Clock()
    {
        if (++_divider < CyclesPerSample)
        {
            return;
        }

        _divider = 0;
        Output = (float)(_opll.Step() * ChannelScale);
    }

    public void Save(StateWriter writer)
    {
        _opll.SaveRegisters(writer);
        writer.WriteInt32(_divider);
        _opll.SaveVoices(writer);
    }

    public void Load(StateReader reader)
    {
        _opll.LoadRegisters(reader);
        _divider = reader.ReadInt32();
        _opll.LoadVoices(reader);
    }
}
