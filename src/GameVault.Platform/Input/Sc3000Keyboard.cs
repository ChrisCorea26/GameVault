namespace GameVault.Platform.Input;

/// <summary>
/// PC keys on the SC-3000's keyboard matrix (outstanding-work plan §8): seven rows of twelve keys, by position as on
/// the SC-3000's own layout (SMS Power! "SC-3000 keyboard"). Bits 0–7 of a row are PA0–PA7, bits 8–11 PB0–PB3.
/// </summary>
/// <remarks>
/// Keys the PC lacks: the SC-3000's @, [ and ] are the PC's [, ] and \ (where they sit on the SC-3000), ^ is =, ¥ is
/// End, Home/Clr is Home, Ins/Del is Backspace or Delete, π is F4, Graph is Alt, Eng Dier's is F2 and Func is F3.
/// </remarks>
public static class Sc3000Keyboard
{
    public const int Rows = 7;

    private static readonly Dictionary<int, (int Row, int Bit)> Keys = Build();

    /// <summary>The matrix for the keys held: one mask per row.</summary>
    public static ushort[] Matrix(IEnumerable<int> heldKeys)
    {
        var rows = new ushort[Rows];
        foreach (var key in heldKeys)
        {
            if (Keys.TryGetValue(key, out var at))
            {
                rows[at.Row] |= (ushort)(1 << at.Bit);
            }
        }

        return rows;
    }

    /// <summary>Where a PC key sits on the matrix, or null for keys the SC-3000 doesn't have.</summary>
    public static (int Row, int Bit)? Position(int virtualKey) => Keys.TryGetValue(virtualKey, out var at) ? at : null;

    private static Dictionary<int, (int Row, int Bit)> Build()
    {
        var keys = new Dictionary<int, (int, int)>();
        void Column(int bit, params int[] perRow)
        {
            for (var row = 0; row < perRow.Length; row++)
            {
                if (perRow[row] != 0)
                {
                    keys[perRow[row]] = (row, bit);
                }
            }
        }

        const int F2 = 0x71, F3 = 0x72, F4 = 0x73, Home = 0x24, End = 0x23, Delete = 0x2E, Alt = 0x12, Control = 0x11;
        Column(0, '1', '2', '3', '4', '5', '6', '7');
        Column(1, 'Q', 'W', 'E', 'R', 'T', 'Y', 'U');
        Column(2, 'A', 'S', 'D', 'F', 'G', 'H', 'J');
        Column(3, 'Z', 'X', 'C', 'V', 'B', 'N', 'M');
        Column(4, F2, 0x20, Home, VirtualKeys.Backspace, 0, 0, 0);
        Column(5, 0xBC, 0xBE, 0xBF, F4, VirtualKeys.Down, VirtualKeys.Left, VirtualKeys.Right); // , . / π ↓ ← →
        Column(6, 'K', 'L', 0xBA, 0xDE, 0xDC, VirtualKeys.Enter, VirtualKeys.Up); // K L ; : ] Return ↑
        Column(7, 'I', 'O', 'P', 0xDB, 0xDD, 0, 0); // I O P @ [
        Column(8, '8', '9', '0', 0xBD, 0xBB, End, F3); // 8 9 0 - ^ ¥ Func
        keys[Alt] = (6, 9);       // Graph
        keys[Control] = (6, 10);  // Ctrl
        keys[VirtualKeys.Shift] = (6, 11);
        keys[VirtualKeys.LeftShift] = (6, 11);
        keys[VirtualKeys.RightShift] = (6, 11);
        keys[Delete] = (3, 4);
        return keys;
    }
}
