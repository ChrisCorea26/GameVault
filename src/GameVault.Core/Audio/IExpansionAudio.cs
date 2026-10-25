namespace GameVault.Core.Audio;

/// <summary>Extra sound chips on some cartridges (VRC6, VRC7, Sunsoft 5B, Namco 163, MMC5, FDS; SPEC §4.4).</summary>
public interface IExpansionAudio
{
    /// <summary>Clocked once per CPU cycle.</summary>
    void Clock();

    /// <summary>Current output, already scaled relative to the 2A03's mixer output (0 to about 1).</summary>
    float Output { get; }
}
