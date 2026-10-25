namespace GameVault.Core.Apu;

/// <summary>Lookup tables from the NESdev Wiki APU pages.</summary>
internal static class ApuTables
{
    /// <summary>"APU Length Counter": index written to bits 3-7 of $4003/$4007/$400B/$400F.</summary>
    public static readonly byte[] Length =
    [
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];

    /// <summary>"APU Pulse": duty waveforms, read with a sequencer that counts down.</summary>
    public static readonly byte[,] Duty =
    {
        { 0, 1, 0, 0, 0, 0, 0, 0 },
        { 0, 1, 1, 0, 0, 0, 0, 0 },
        { 0, 1, 1, 1, 1, 0, 0, 0 },
        { 1, 0, 0, 1, 1, 1, 1, 1 },
    };

    /// <summary>"APU Triangle": 32-step sequence.</summary>
    public static readonly byte[] Triangle =
    [
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    /// <summary>"APU Noise": timer periods in APU cycles.</summary>
    public static readonly ushort[] NoiseNtsc = [4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068];

    public static readonly ushort[] NoisePal = [4, 8, 14, 30, 60, 88, 118, 148, 188, 236, 354, 472, 708, 944, 1890, 3778];

    /// <summary>"APU DMC": output rates in CPU cycles.</summary>
    public static readonly ushort[] DmcNtsc = [428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54];

    public static readonly ushort[] DmcPal = [398, 354, 316, 298, 276, 236, 210, 198, 176, 148, 132, 118, 98, 78, 66, 50];

    /// <summary>"APU Mixer" lookup tables: pulse_table[p1 + p2] and tnd_table[3t + 2n + d].</summary>
    public static readonly float[] PulseMix = BuildPulseMix();

    public static readonly float[] TndMix = BuildTndMix();

    private static float[] BuildPulseMix()
    {
        var table = new float[31];
        for (var n = 1; n < table.Length; n++)
        {
            table[n] = (float)(95.52 / (8128.0 / n + 100));
        }

        return table;
    }

    private static float[] BuildTndMix()
    {
        var table = new float[203];
        for (var n = 1; n < table.Length; n++)
        {
            table[n] = (float)(163.67 / (24329.0 / n + 100));
        }

        return table;
    }
}
