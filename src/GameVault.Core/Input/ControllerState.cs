namespace GameVault.Core.Input;

/// <summary>
/// One controller's buttons and sticks, in 64 bits. The low byte is the NES pad, in the order the hardware shifts them
/// out through $4016/$4017 (docs/design/input.md §2; NESdev Wiki: "Standard controller"). The second byte holds the
/// buttons other systems add: C, X, Y, Z and Mode for the Genesis 6-button pad (Phase 13), L and R for the SNES
/// (Phase 14), and L2 and R2 for the PlayStation (Phase 15). Bits 16–31 hold the left analog stick for the Nintendo 64
/// (Phase 16): X in bits 16–23 and Y in bits 24–31, each a signed byte (up is positive, as the N64 reports it).
/// Bits 32–47 hold the right stick the same way, and bits 48 and 49 the stick clicks L3 and R3, for the PlayStation 2's
/// DualShock 2 (Phase 17).
/// </summary>
/// <remarks>
/// Each console maps these to its own pad and reads only its bits. On the Genesis, B is the pad's A button, A is its B
/// button and Select is Mode, so the keyboard keys sit in the same places (Z, X, then C).
/// </remarks>
[Flags]
public enum ControllerState : ulong
{
    None = 0,
    A = 1UL << 0,
    B = 1UL << 1,
    Select = 1UL << 2,
    Start = 1UL << 3,
    Up = 1UL << 4,
    Down = 1UL << 5,
    Left = 1UL << 6,
    Right = 1UL << 7,
    C = 1UL << 8,
    X = 1UL << 9,
    Y = 1UL << 10,
    Z = 1UL << 11,
    L = 1UL << 12,
    R = 1UL << 13,
    L2 = 1UL << 14,
    R2 = 1UL << 15,

    /// <summary>The left stick's click (DualShock 2).</summary>
    L3 = 1UL << 48,

    /// <summary>The right stick's click (DualShock 2).</summary>
    R3 = 1UL << 49,
}

/// <summary>The analog sticks packed into <see cref="ControllerState"/>: the left in bits 16–31, the right in bits 32–47.</summary>
public static class ControllerStick
{
    public const int StickShift = 16;

    public const int RightStickShift = 32;

    /// <summary>The bits that hold the left stick.</summary>
    public const ControllerState StickMask = (ControllerState)0x0000_0000_FFFF_0000UL;

    /// <summary>The bits that hold the right stick.</summary>
    public const ControllerState RightStickMask = (ControllerState)0x0000_FFFF_0000_0000UL;

    /// <summary>The bits that hold either stick.</summary>
    public const ControllerState SticksMask = StickMask | RightStickMask;

    public static sbyte StickX(this ControllerState state) => (sbyte)((ulong)state >> 16);

    public static sbyte StickY(this ControllerState state) => (sbyte)((ulong)state >> 24);

    public static sbyte RightStickX(this ControllerState state) => (sbyte)((ulong)state >> 32);

    public static sbyte RightStickY(this ControllerState state) => (sbyte)((ulong)state >> 40);

    /// <summary>The buttons only (every bit but the sticks').</summary>
    public static ControllerState Buttons(this ControllerState state) => state & ~SticksMask;

    /// <summary>The same buttons and right stick with the left stick at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static ControllerState WithStick(this ControllerState state, sbyte x, sbyte y) =>
        (state & ~StickMask) | (ControllerState)(((ulong)(byte)x << 16) | ((ulong)(byte)y << 24));

    /// <summary>The same buttons and left stick with the right stick at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static ControllerState WithRightStick(this ControllerState state, sbyte x, sbyte y) =>
        (state & ~RightStickMask) | (ControllerState)(((ulong)(byte)x << 32) | ((ulong)(byte)y << 40));
}
