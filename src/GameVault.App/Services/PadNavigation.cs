using System.Runtime.InteropServices;
using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GameVault.App.Services;

/// <summary>
/// Carries out pad navigation on every page and flyout (SPEC §9.3, docs/design/input.md §7.1).
/// <see cref="PadFocusPlanner"/> decides what a press does from a description of the focused element; this class
/// builds that description (from the element's type and UI Automation patterns) and does the step with WinUI's XY
/// focus, the element's automation provider, or a key press. Runs on the UI thread, only while GameVault is in front.
/// </summary>
public sealed partial class PadNavigation(INavigationService navigation)
{
    private const ushort VkReturn = 0x0D;
    private const ushort VkEscape = 0x1B;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;

    /// <summary>The most focus moves one trigger press makes while looking a screen away.</summary>
    private const int MaxScrollSteps = 40;

    private Window? _window;
    private Frame? _frame;
    private NavigationView? _menu;

    /// <summary>Called by <see cref="MainWindow"/> once its content has loaded.</summary>
    public void Attach(Window window, Frame frame, NavigationView menu)
    {
        _window = window;
        _frame = frame;
        _menu = menu;
    }

    public void Handle(NavigationAction action)
    {
        if (_window?.Content is not UIElement content || content.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var popupRoot = OpenPopupRoot(xamlRoot);
        var focused = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
        var kind = focused is null || (popupRoot is not null && !IsWithin(focused, popupRoot)) ? FocusedKind.None : Classify(focused);
        var context = new PadFocusContext(
            kind,
            InPopup: popupRoot is not null,
            CanGoBack: navigation.CanGoBack,
            HasContextMenu: ContextMenuOwner(focused) is not null,
            CanSwitchPage: navigation.CurrentPage != AppPage.Player);

        var step = PadFocusPlanner.Plan(action, context);
        var searchRoot = popupRoot ?? content;
        switch (step.Kind)
        {
            case PadFocusStepKind.MoveFocus:
                FocusManager.TryMoveFocus(ToDirection(step.Direction), new FindNextElementOptions { SearchRoot = searchRoot });
                break;
            case PadFocusStepKind.SendKey:
                Tap(ToVirtualKey(step.Key));
                break;
            case PadFocusStepKind.Invoke:
                (Pattern(focused, PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
                break;
            case PadFocusStepKind.Toggle:
                (Pattern(focused, PatternInterface.Toggle) as IToggleProvider)?.Toggle();
                break;
            case PadFocusStepKind.ExpandCollapse:
                if (Pattern(focused, PatternInterface.ExpandCollapse) is IExpandCollapseProvider expander)
                {
                    if (expander.ExpandCollapseState == Microsoft.UI.Xaml.Automation.ExpandCollapseState.Collapsed)
                    {
                        expander.Expand();
                    }
                    else
                    {
                        expander.Collapse();
                    }
                }

                break;
            case PadFocusStepKind.Select:
                (Pattern(focused, PatternInterface.SelectionItem) as ISelectionItemProvider)?.Select();
                break;
            case PadFocusStepKind.FocusFirst:
                FocusFirst(popupRoot ?? _frame?.Content as DependencyObject ?? content);
                break;
            case PadFocusStepKind.GoBack:
                navigation.GoBack();
                break;
            case PadFocusStepKind.ScrollPage:
                ScrollPage(focused, step.Delta, searchRoot);
                break;
            case PadFocusStepKind.SwitchPage:
                if (PadFocusPlanner.SwitchTarget(navigation.CurrentPage, step.Delta) is { } page)
                {
                    navigation.NavigateTo(page);
                }

                break;
            case PadFocusStepKind.ShowContextMenu:
                if (ContextMenuOwner(focused) is { } owner)
                {
                    owner.ContextFlyout.ShowAt(owner, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard });
                }

                break;
            case PadFocusStepKind.FocusMenu:
                FocusMenu();
                break;
            case PadFocusStepKind.ShowKeyboard:
                ShowTouchKeyboard();
                break;
        }
    }

    private static FocusedKind Classify(DependencyObject element)
    {
        switch (element)
        {
            case TextBox or PasswordBox or RichEditBox:
                return FocusedKind.TextInput;
            case Slider slider:
                return slider.Orientation == Orientation.Horizontal ? FocusedKind.Slider : FocusedKind.Other;
            case ComboBox:
                return FocusedKind.ComboBox;
            case RadioButton:
                return FocusedKind.Selectable;
            case SelectorItem: // list, grid and drop-down items: Enter raises ItemClick or picks the item
                return FocusedKind.ListItem;
        }

        if (Pattern(element, PatternInterface.Toggle) is not null)
        {
            return FocusedKind.Toggle;
        }

        if (Pattern(element, PatternInterface.Invoke) is not null)
        {
            return FocusedKind.Button;
        }

        if (Pattern(element, PatternInterface.ExpandCollapse) is not null)
        {
            return FocusedKind.Expander;
        }

        return Pattern(element, PatternInterface.SelectionItem) is not null ? FocusedKind.Selectable : FocusedKind.Other;
    }

    private static object? Pattern(DependencyObject? element, PatternInterface pattern)
    {
        if (element is not UIElement uiElement)
        {
            return null;
        }

        var peer = FrameworkElementAutomationPeer.FromElement(uiElement) ?? FrameworkElementAutomationPeer.CreatePeerForElement(uiElement);
        return peer?.GetPattern(pattern);
    }

    /// <summary>The child of the open flyout, menu or drop-down that pad navigation stays in, if any.</summary>
    private static UIElement? OpenPopupRoot(XamlRoot xamlRoot)
    {
        UIElement? root = null;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is { } child && child is not ToolTip)
            {
                root = child; // the last one is the topmost
            }
        }

        return root;
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The element whose context menu North opens: the control itself, or a list item's content (a game tile).</summary>
    private static UIElement? ContextMenuOwner(DependencyObject? focused) => focused switch
    {
        UIElement { ContextFlyout: not null } element => element,
        SelectorItem { ContentTemplateRoot: UIElement { ContextFlyout: not null } content } => content,
        _ => null,
    };

    private static void FocusFirst(DependencyObject root)
    {
        if (FocusManager.FindFirstFocusableElement(root) is { } first)
        {
            _ = FocusManager.TryFocusAsync(first, FocusState.Keyboard);
        }
    }

    /// <summary>
    /// Moves focus up or down until it has travelled about a screen of the scrolling area it's in. Each move brings the
    /// new control into view, so the page scrolls with it and focus is never left off-screen.
    /// </summary>
    private static void ScrollPage(DependencyObject? focused, int delta, UIElement searchRoot)
    {
        if (focused is not UIElement start || FindScrollViewer(start) is not { } scroller)
        {
            return;
        }

        var reference = scroller.Content as UIElement ?? scroller;
        var distance = Math.Max(scroller.ViewportHeight, 100) * 0.8;
        var startY = Top(start, reference);
        var direction = delta > 0 ? FocusNavigationDirection.Down : FocusNavigationDirection.Up;
        var options = new FindNextElementOptions { SearchRoot = searchRoot };
        for (var i = 0; i < MaxScrollSteps; i++)
        {
            if (!FocusManager.TryMoveFocus(direction, options)
                || FocusManager.GetFocusedElement(start.XamlRoot) is not UIElement now
                || !IsWithin(now, scroller))
            {
                return;
            }

            if (Math.Abs(Top(now, reference) - startY) >= distance)
            {
                return;
            }
        }
    }

    private static double Top(UIElement element, UIElement reference) =>
        element.TransformToVisual(reference).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroller)
            {
                return scroller;
            }
        }

        return null;
    }

    private void FocusMenu()
    {
        if (_menu is null)
        {
            return;
        }

        var target = _menu.SelectedItem as Control
            ?? _menu.MenuItems.OfType<Control>().FirstOrDefault();
        target?.Focus(FocusState.Keyboard);
    }

    /// <summary>The on-screen keyboard for the focused text box; does nothing where Windows has none (no touch keyboard service).</summary>
    private void ShowTouchKeyboard()
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd).TryShow();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            // No touch keyboard on this edition of Windows: a physical keyboard is still needed to type.
        }
    }

    private static FocusNavigationDirection ToDirection(NavigationAction direction) => direction switch
    {
        NavigationAction.Up => FocusNavigationDirection.Up,
        NavigationAction.Down => FocusNavigationDirection.Down,
        NavigationAction.Left => FocusNavigationDirection.Left,
        _ => FocusNavigationDirection.Right,
    };

    private static ushort ToVirtualKey(PadKey key) => key switch
    {
        PadKey.Up => VkUp,
        PadKey.Down => VkDown,
        PadKey.Left => VkLeft,
        PadKey.Right => VkRight,
        PadKey.Escape => VkEscape,
        _ => VkReturn,
    };

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
