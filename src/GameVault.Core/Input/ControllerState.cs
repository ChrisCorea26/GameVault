namespace GameVault.Core.Input;

/// <summary>
/// One controller's buttons. The low byte is the NES pad, in the order the hardware shifts them out through
/// $4016/$4017 (docs/design/input.md §2; NESdev Wiki: "Standard controller"). The high byte holds the buttons
/// other systems add: C, X, Y, Z and Mode for the Genesis 6-button pad (Phase 13), L and R for the SNES (Phase 14),
/// and L2 and R2 for the PlayStation (Phase 15).
/// </summary>
/// <remarks>
/// Each console maps these to its own pad. On the Genesis, B is the pad's A button, A is its B button and Select
/// is Mode, so the keyboard keys sit in the same places (Z, X, then C).
/// </remarks>
[Flags]
public enum ControllerState : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
    C = 1 << 8,
    X = 1 << 9,
    Y = 1 << 10,
    Z = 1 << 11,
    L = 1 << 12,
    R = 1 << 13,
    L2 = 1 << 14,
    R2 = 1 << 15,
}
