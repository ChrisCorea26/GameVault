namespace GameVault.Platform.Input;

/// <summary>
/// Turns PC keys into the Apple II's keyboard codes (SPEC §7.11, Phase 12), US layout: Return is 13, the left and
/// right arrows 8 and 21, up and down 11 and 10, Esc is F1 (GameVault's Esc pauses), Ctrl+letter the control codes.
/// </summary>
public static class AppleKeyboard
{
    public const int F1 = 0x70;

    /// <summary>The Apple code for a key, or null for keys the Apple doesn't have.</summary>
    public static byte? ToAscii(int virtualKey, bool shift, bool control)
    {
        switch (virtualKey)
        {
            case >= 0x41 and <= 0x5A:
                return control ? (byte)(virtualKey & 0x1F) : (byte)(shift ? virtualKey : virtualKey + 0x20);
            case >= 0x30 and <= 0x39:
                return shift ? (byte)")!@#$%^&*("[virtualKey - 0x30] : (byte)virtualKey;
            case 0x20:
                return 0x20;
            case VirtualKeys.Enter:
                return 13;
            case F1:
                return 27;
            case VirtualKeys.Backspace or VirtualKeys.Left:
                return 8;
            case VirtualKeys.Right:
                return 21;
            case VirtualKeys.Up:
                return 11;
            case VirtualKeys.Down:
                return 10;
            case 0x2E: // Delete
                return 127;
        }

        var (plain, shifted) = virtualKey switch
        {
            0xBA => (';', ':'),
            0xBB => ('=', '+'),
            0xBC => (',', '<'),
            0xBD => ('-', '_'),
            0xBE => ('.', '>'),
            0xBF => ('/', '?'),
            0xC0 => ('`', '~'),
            0xDB => ('[', '{'),
            0xDC => ('\\', '|'),
            0xDD => (']', '}'),
            0xDE => ('\'', '"'),
            _ => ('\0', '\0'),
        };

        return plain == '\0' ? null : (byte)(shift ? shifted : plain);
    }
}
