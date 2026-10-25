namespace GameVault.Core.Audio;

/// <summary>Receives the APU's output as amplitude changes stamped with the CPU cycle (within the current frame).</summary>
public interface IAudioSink
{
    void AddDelta(int clock, float delta);

    /// <summary>Ends an audio frame of <paramref name="clocks"/> CPU cycles; resamples it to output samples.</summary>
    void EndFrame(int clocks);
}
