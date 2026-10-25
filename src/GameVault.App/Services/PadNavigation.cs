using System.Runtime.InteropServices;
using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels.Services;

namespace GameVault.App.Services;

/// <summary>
/// Turns pad navigation into the equivalent keyboard keys (arrows, Enter), so every control's built-in
/// keyboard and XY-focus behavior applies (docs/design/input.md §7, SPEC §9.3). East / "back" goes back a
/// page when possible, else sends Esc to close flyouts. Only used while GameVault is the foreground app.
/// </summary>
public sealed partial class PadNavigation(INavigationService navigation)
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;

    public void Handle(NavigationAction action)
    {
        switch (action)
        {
            case NavigationAction.Up: Tap(VkUp); break;
            case NavigationAction.Down: Tap(VkDown); break;
            case NavigationAction.Left: Tap(VkLeft); break;
            case NavigationAction.Right: Tap(VkRight); break;
            case NavigationAction.Activate: Tap(VkReturn); break;
            case NavigationAction.Back:
                if (navigation.CanGoBack)
                {
                    navigation.GoBack();
                }
                else
                {
                    Tap(VkEscape);
                }

                break;
        }
    }

    private static void Tap(ushort virtualKey)
    {
        Span<Input> inputs = stackalloc Input[2];
        inputs[0] = Input.Key(virtualKey, keyUp: false);
        inputs[1] = Input.Key(virtualKey, keyUp: true);
        SendInput(2, ref inputs[0], Marshal.SizeOf<Input>());
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, ref Input inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        private const uint KeyboardType = 1;
        private const uint KeyUpFlag = 0x0002;

        public uint Type;
        public KeyboardInput Data;

        public static Input Key(ushort vk, bool keyUp) => new()
        {
            Type = KeyboardType,
            Data = new KeyboardInput { VirtualKey = vk, Flags = keyUp ? KeyUpFlag : 0 },
        };
    }

    // INPUT's union is sized by MOUSEINPUT (the largest member); pad KEYBDINPUT to match.
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
        public uint Padding1;
        public uint Padding2;
    }
}
