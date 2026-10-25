namespace GameVault.Platform.Input;

/// <summary>Windows virtual-key codes used by the keyboard bindings (WinUI's <c>VirtualKey</c> values).</summary>
public static class VirtualKeys
{
    public const int Backspace = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Escape = 0x1B;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int A = 0x41;
    public const int D = 0x44;
    public const int E = 0x45;
    public const int F = 0x46;
    public const int G = 0x47;
    public const int Q = 0x51;
    public const int S = 0x53;
    public const int W = 0x57;
    public const int X = 0x58;
    public const int Z = 0x5A;
    public const int Comma = 0xBC;
    public const int Period = 0xBE;
    public const int F4 = 0x73;
    public const int F5 = 0x74;
    public const int F6 = 0x75;
    public const int F7 = 0x76;
    public const int F8 = 0x77;
    public const int F9 = 0x78;
    public const int F12 = 0x7B;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;

    /// <summary>Set-1 scan code of the right Shift key; WinUI reports both Shift keys as <see cref="Shift"/>.</summary>
    public const uint RightShiftScanCode = 0x36;

    /// <summary>
    /// Tells left and right Shift apart. WinUI's <c>KeyDown</c> reports the generic Shift key, so the
    /// scan code decides.
    /// </summary>
    public static int Normalize(int virtualKey, uint scanCode) =>
        virtualKey == Shift ? (scanCode == RightShiftScanCode ? RightShift : LeftShift) : virtualKey;
}
