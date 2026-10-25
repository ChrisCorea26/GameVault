namespace GameVault.Core.Input;

/// <summary>
/// One standard controller's buttons, in the order the hardware shifts them out through $4016/$4017
/// (docs/design/input.md §2; NESdev Wiki: "Standard controller").
/// </summary>
[Flags]
public enum ControllerState : byte
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
}
