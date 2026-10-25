using GameVault.Core.Input;

namespace GameVault.Platform.Input;

/// <summary>Left stick as an 8-way D-pad with a radial dead zone (docs/design/input.md §6).</summary>
public static class StickInput
{
    public const double DefaultDeadZone = 0.5;

    /// <summary>Converts SDL axis values (-32768..32767, +Y is down) to directions.</summary>
    public static ControllerState ToDirections(short x, short y, double deadZone = DefaultDeadZone)
    {
        var fx = x / 32768.0;
        var fy = y / 32768.0;
        if (Math.Sqrt(fx * fx + fy * fy) < deadZone)
        {
            return ControllerState.None;
        }

        // Eight 45-degree sectors centered on the cardinal and diagonal directions.
        var angle = Math.Atan2(-fy, fx) * 180 / Math.PI; // 0 = right, 90 = up
        var sector = (((int)Math.Round(angle / 45.0) % 8) + 8) % 8;
        return sector switch
        {
            0 => ControllerState.Right,
            1 => ControllerState.Right | ControllerState.Up,
            2 => ControllerState.Up,
            3 => ControllerState.Up | ControllerState.Left,
            4 => ControllerState.Left,
            5 => ControllerState.Left | ControllerState.Down,
            6 => ControllerState.Down,
            _ => ControllerState.Down | ControllerState.Right,
        };
    }
}
