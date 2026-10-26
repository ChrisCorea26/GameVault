namespace GameVault.Core.Nintendo64;

/// <summary>The vector unit's divide ROMs, computed from their documented formulas (n64brew: "Reality Signal Processor/CPU Core", VRCP and VRSQ).</summary>
public sealed partial class N64Rsp
{
    /// <summary>512 entries of 1/x for x in [1, 2), as the 16 fraction bits after an implicit leading 1.</summary>
    internal static readonly ushort[] Reciprocals = BuildReciprocals();

    /// <summary>512 entries of 1/√x: even indices for x in [1, 2), odd ones for [2, 4), as fraction bits after an implicit 1.</summary>
    internal static readonly ushort[] InverseSquareRoots = BuildInverseSquareRoots();

    private static ushort[] BuildReciprocals()
    {
        var table = new ushort[512];
        for (var index = 0; index < 512; index++)
        {
            var a = (ulong)(index + 512);
            var b = (1UL << 34) / a;
            table[index] = (ushort)((b + 1) >> 8);
        }

        // 1/1 doesn't fit below the implicit 1: the ROM holds the largest fraction instead.
        table[0] = 0xFFFF;
        return table;
    }

    private static ushort[] BuildInverseSquareRoots()
    {
        var table = new ushort[512];
        for (var index = 0; index < 512; index++)
        {
            var a = (ulong)(index + 512) >> (index % 2);
            var b = 1UL << 17;

            // The largest b with b < 2^22 / sqrt(a), found exactly.
            while (a * (b + 1) * (b + 1) < 1UL << 44)
            {
                b++;
            }

            table[index] = (ushort)(b >> 1);
        }

        return table;
    }
}
