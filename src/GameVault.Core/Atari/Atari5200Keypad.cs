using GameVault.Core.Input;

namespace GameVault.Core.Atari;

/// <summary>The 5200 controller's keypad keys besides Start (<see cref="ControllerState.Start"/>) and Pause (Select).</summary>
public enum Atari5200Key
{
    None,
    One,
    Two,
    Three,
    Four,
    Five,
    Six,
    Seven,
    Eight,
    Nine,
    Star,
    Zero,
    Hash,
    Reset,
}

/// <summary>
/// The keypad key packed into <see cref="ControllerState"/> bits 50–53 (only the 5200 reads them), and the codes POKEY's
/// keypad scan reports for each key.
/// </summary>
/// <remarks>
/// The codes follow the keypad's wiring as a 4 × 4 matrix: 1, 2, 3, Start; 4, 5, 6, Pause; 7, 8, 9, Reset; *, 0, #
/// counting down from 15 (Dan Boris's 5200 hardware notes), with 0 for no key.
/// </remarks>
public static class Atari5200Keypad
{
    public const int KeyShift = 50;

    public const ControllerState KeyMask = (ControllerState)(0xFUL << KeyShift);

    private static readonly int[] Codes = [Pokey.NoKey, 0xF, 0xE, 0xD, 0xB, 0xA, 0x9, 0x7, 0x6, 0x5, 0x3, 0x2, 0x1, 0x4];

    public static Atari5200Key Key(this ControllerState state)
    {
        var key = (int)(((ulong)state >> KeyShift) & 0xF);
        return key <= (int)Atari5200Key.Reset ? (Atari5200Key)key : Atari5200Key.None;
    }

    public static ControllerState WithKey(this ControllerState state, Atari5200Key key) =>
        (state & ~KeyMask) | (ControllerState)((ulong)key << KeyShift);

    /// <summary>The code POKEY scans for the key held (a keypad key first, then Start, then Pause), or <see cref="Pokey.NoKey"/>.</summary>
    public static int PokeyCode(ControllerState state)
    {
        var key = state.Key();
        if (key != Atari5200Key.None)
        {
            return Codes[(int)key];
        }

        return (state & ControllerState.Start) != 0 ? 0xC
            : (state & ControllerState.Select) != 0 ? 0x8
            : Pokey.NoKey;
    }
}
