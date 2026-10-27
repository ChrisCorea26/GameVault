using GameVault.ViewModels.Services;

namespace GameVault.Platform.Input.Controllers;

/// <summary>What the focused element is, as far as pad navigation cares (docs/design/input.md §7.1).</summary>
public enum FocusedKind
{
    /// <summary>Nothing has focus, or focus is outside the open popup.</summary>
    None,
    /// <summary>Invokable: a button, hyperlink, menu item or navigation item.</summary>
    Button,
    /// <summary>A toggle switch, check box or toggle button.</summary>
    Toggle,
    /// <summary>Opens and closes in place: an expander, a split button's menu.</summary>
    Expander,
    /// <summary>A closed drop-down list.</summary>
    ComboBox,
    /// <summary>Selected rather than invoked: a radio button.</summary>
    Selectable,
    /// <summary>An item of a list, grid or open drop-down; Enter raises its click.</summary>
    ListItem,
    TextInput,
    /// <summary>A horizontal slider: left and right change its value.</summary>
    Slider,
    Other,
}

/// <summary>The focused element and its surroundings when a navigation press arrives.</summary>
/// <param name="InPopup">A flyout, menu or drop-down is open; navigation stays inside it.</param>
/// <param name="CanSwitchPage">The shoulders may switch between the main pages (not while the Player is shown).</param>
public readonly record struct PadFocusContext(
    FocusedKind Kind,
    bool InPopup = false,
    bool CanGoBack = false,
    bool HasContextMenu = false,
    bool CanSwitchPage = true);

public enum PadFocusStepKind
{
    None,
    /// <summary>Move focus to the nearest control in <see cref="PadFocusStep.Direction"/> (XY focus), within the popup if one is open.</summary>
    MoveFocus,
    /// <summary>Send <see cref="PadFocusStep.Key"/> to the focused control, which handles it itself.</summary>
    SendKey,
    Invoke,
    Toggle,
    /// <summary>Open the control if closed, close it if open.</summary>
    ExpandCollapse,
    Select,
    /// <summary>Focus the first control of the open popup, or of the window.</summary>
    FocusFirst,
    GoBack,
    /// <summary>Move focus about a screen's height up (<see cref="PadFocusStep.Delta"/> −1) or down (+1).</summary>
    ScrollPage,
    /// <summary>Show the previous (−1) or next (+1) main page.</summary>
    SwitchPage,
    ShowContextMenu,
    /// <summary>Focus the main menu (the navigation pane).</summary>
    FocusMenu,
    /// <summary>Show the on-screen keyboard for the focused text box.</summary>
    ShowKeyboard,
}

public enum PadKey
{
    Up,
    Down,
    Left,
    Right,
    Enter,
    Escape,
}

public readonly record struct PadFocusStep(PadFocusStepKind Kind, NavigationAction Direction = NavigationAction.Up, PadKey Key = PadKey.Enter, int Delta = 0)
{
    public static readonly PadFocusStep Nothing = new(PadFocusStepKind.None);

    public static PadFocusStep Move(NavigationAction direction) => new(PadFocusStepKind.MoveFocus, Direction: direction);

    public static PadFocusStep Send(PadKey key) => new(PadFocusStepKind.SendKey, Key: key);

    public static PadFocusStep Of(PadFocusStepKind kind, int delta = 0) => new(kind, Delta: delta);
}

/// <summary>
/// Decides what a pad's navigation press does on the focused control (SPEC §9.3, docs/design/input.md §7.1). The app
/// describes the focused element; this returns one step, which the app carries out with WinUI's focus manager and UI
/// Automation. Kept free of WinUI so every rule is unit tested.
/// </summary>
public static class PadFocusPlanner
{
    /// <summary>The pages the shoulders cycle through, in the navigation pane's order.</summary>
    public static IReadOnlyList<AppPage> MainPages { get; } = [AppPage.Library, AppPage.Settings];

    public static PadFocusStep Plan(NavigationAction action, PadFocusContext context)
    {
        switch (action)
        {
            case NavigationAction.Up or NavigationAction.Down or NavigationAction.Left or NavigationAction.Right:
                return Direction(action, context);

            case NavigationAction.Activate:
                return Activate(context);

            case NavigationAction.Back:
                // Close the open flyout or drop-down first; then go back a page; on the first page Esc closes what's left.
                if (context.InPopup)
                {
                    return PadFocusStep.Send(PadKey.Escape);
                }

                return context.CanGoBack ? PadFocusStep.Of(PadFocusStepKind.GoBack) : PadFocusStep.Send(PadKey.Escape);

            case NavigationAction.PreviousPage or NavigationAction.NextPage:
                return context.InPopup || !context.CanSwitchPage
                    ? PadFocusStep.Nothing
                    : PadFocusStep.Of(PadFocusStepKind.SwitchPage, action == NavigationAction.NextPage ? 1 : -1);

            case NavigationAction.PageUp or NavigationAction.PageDown:
                if (context.InPopup)
                {
                    return PadFocusStep.Nothing;
                }

                return context.Kind == FocusedKind.None
                    ? PadFocusStep.Of(PadFocusStepKind.FocusFirst)
                    : PadFocusStep.Of(PadFocusStepKind.ScrollPage, action == NavigationAction.PageDown ? 1 : -1);

            case NavigationAction.ContextMenu:
                return context.HasContextMenu && !context.InPopup ? PadFocusStep.Of(PadFocusStepKind.ShowContextMenu) : PadFocusStep.Nothing;

            case NavigationAction.Menu:
                return context.InPopup ? PadFocusStep.Send(PadKey.Escape) : PadFocusStep.Of(PadFocusStepKind.FocusMenu);

            default:
                return PadFocusStep.Nothing;
        }
    }

    /// <summary>The page the shoulders lead to from <paramref name="current"/>, or null to stay.</summary>
    public static AppPage? SwitchTarget(AppPage? current, int delta)
    {
        if (current == AppPage.Player)
        {
            return null; // leaving the Player would end the game; its overlay has its own way out
        }

        var index = current is { } page ? IndexOf(page) : -1;
        int target;
        if (index < 0)
        {
            // From Game Details or a wizard: right goes to the first page, left to the last.
            target = delta > 0 ? 0 : MainPages.Count - 1;
        }
        else
        {
            target = Math.Clamp(index + Math.Sign(delta), 0, MainPages.Count - 1);
            if (target == index)
            {
                return null;
            }
        }

        return MainPages[target];
    }

    private static int IndexOf(AppPage page)
    {
        for (var i = 0; i < MainPages.Count; i++)
        {
            if (MainPages[i] == page)
            {
                return i;
            }
        }

        return -1;
    }

    private static PadFocusStep Direction(NavigationAction direction, PadFocusContext context)
    {
        var horizontal = direction is NavigationAction.Left or NavigationAction.Right;
        return context.Kind switch
        {
            FocusedKind.None => PadFocusStep.Of(PadFocusStepKind.FocusFirst),
            // An open drop-down or menu moves through its own items (and scrolls a long list).
            FocusedKind.ListItem when context.InPopup => PadFocusStep.Send(ToKey(direction)),
            FocusedKind.Slider when horizontal => PadFocusStep.Send(ToKey(direction)),
            // Everything else, text boxes included, hands focus to the nearest control that way.
            _ => PadFocusStep.Move(direction),
        };
    }

    private static PadFocusStep Activate(PadFocusContext context) => context.Kind switch
    {
        FocusedKind.None => PadFocusStep.Of(PadFocusStepKind.FocusFirst),
        FocusedKind.Button => PadFocusStep.Of(PadFocusStepKind.Invoke),
        FocusedKind.Toggle => PadFocusStep.Of(PadFocusStepKind.Toggle),
        FocusedKind.Expander or FocusedKind.ComboBox => PadFocusStep.Of(PadFocusStepKind.ExpandCollapse),
        FocusedKind.Selectable => PadFocusStep.Of(PadFocusStepKind.Select),
        FocusedKind.TextInput => PadFocusStep.Of(PadFocusStepKind.ShowKeyboard),
        FocusedKind.Slider => PadFocusStep.Nothing,
        // List items (ItemClick, choosing in a drop-down) and anything unrecognised take Enter.
        _ => PadFocusStep.Send(PadKey.Enter),
    };

    private static PadKey ToKey(NavigationAction direction) => direction switch
    {
        NavigationAction.Up => PadKey.Up,
        NavigationAction.Down => PadKey.Down,
        NavigationAction.Left => PadKey.Left,
        _ => PadKey.Right,
    };
}
