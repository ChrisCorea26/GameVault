using GameVault.Platform.Input.Controllers;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

public class PadFocusPlannerTests
{
    private static PadFocusStep Plan(NavigationAction action, FocusedKind kind, bool inPopup = false, bool canGoBack = false,
        bool hasContextMenu = false, bool canSwitchPage = true) =>
        PadFocusPlanner.Plan(action, new PadFocusContext(kind, inPopup, canGoBack, hasContextMenu, canSwitchPage));

    [Theory]
    [InlineData(FocusedKind.Button)]
    [InlineData(FocusedKind.Toggle)]
    [InlineData(FocusedKind.TextInput)]
    [InlineData(FocusedKind.ComboBox)]
    [InlineData(FocusedKind.ListItem)]
    [InlineData(FocusedKind.Expander)]
    [InlineData(FocusedKind.Other)]
    public void Directions_move_focus_between_controls_even_out_of_text_boxes_and_lists(FocusedKind kind)
    {
        foreach (var direction in new[] { NavigationAction.Up, NavigationAction.Down, NavigationAction.Left, NavigationAction.Right })
        {
            Plan(direction, kind).Should().Be(PadFocusStep.Move(direction));
        }
    }

    [Fact]
    public void Inside_a_flyout_directions_move_focus_within_it()
    {
        Plan(NavigationAction.Down, FocusedKind.Button, inPopup: true).Should().Be(PadFocusStep.Move(NavigationAction.Down));
    }

    [Fact]
    public void An_open_drop_down_or_menu_takes_the_arrow_keys()
    {
        Plan(NavigationAction.Down, FocusedKind.ListItem, inPopup: true).Should().Be(PadFocusStep.Send(PadKey.Down));
        Plan(NavigationAction.Up, FocusedKind.ListItem, inPopup: true).Should().Be(PadFocusStep.Send(PadKey.Up));
    }

    [Fact]
    public void A_slider_takes_left_and_right_as_its_value_and_lets_up_and_down_leave()
    {
        Plan(NavigationAction.Left, FocusedKind.Slider).Should().Be(PadFocusStep.Send(PadKey.Left));
        Plan(NavigationAction.Right, FocusedKind.Slider).Should().Be(PadFocusStep.Send(PadKey.Right));
        Plan(NavigationAction.Down, FocusedKind.Slider).Should().Be(PadFocusStep.Move(NavigationAction.Down));
        Plan(NavigationAction.Activate, FocusedKind.Slider).Should().Be(PadFocusStep.Nothing);
    }

    [Fact]
    public void With_nothing_focused_the_first_press_focuses_the_first_control()
    {
        Plan(NavigationAction.Down, FocusedKind.None).Kind.Should().Be(PadFocusStepKind.FocusFirst);
        Plan(NavigationAction.Activate, FocusedKind.None).Kind.Should().Be(PadFocusStepKind.FocusFirst);
        Plan(NavigationAction.PageDown, FocusedKind.None).Kind.Should().Be(PadFocusStepKind.FocusFirst);
        Plan(NavigationAction.Right, FocusedKind.None, inPopup: true).Kind.Should().Be(PadFocusStepKind.FocusFirst,
            "focus outside an open flyout goes into it");
    }

    [Theory]
    [InlineData(FocusedKind.Button, PadFocusStepKind.Invoke)]
    [InlineData(FocusedKind.Toggle, PadFocusStepKind.Toggle)]
    [InlineData(FocusedKind.Expander, PadFocusStepKind.ExpandCollapse)]
    [InlineData(FocusedKind.ComboBox, PadFocusStepKind.ExpandCollapse)]
    [InlineData(FocusedKind.Selectable, PadFocusStepKind.Select)]
    [InlineData(FocusedKind.TextInput, PadFocusStepKind.ShowKeyboard)]
    public void Activate_uses_what_the_control_supports(FocusedKind kind, PadFocusStepKind expected)
    {
        Plan(NavigationAction.Activate, kind).Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData(FocusedKind.ListItem)]
    [InlineData(FocusedKind.Other)]
    public void Activate_sends_enter_to_list_items_and_unknown_controls(FocusedKind kind)
    {
        Plan(NavigationAction.Activate, kind).Should().Be(PadFocusStep.Send(PadKey.Enter));
        Plan(NavigationAction.Activate, kind, inPopup: true).Should().Be(PadFocusStep.Send(PadKey.Enter));
    }

    [Fact]
    public void Back_closes_a_flyout_then_goes_back_then_sends_escape()
    {
        Plan(NavigationAction.Back, FocusedKind.Button, inPopup: true, canGoBack: true).Should().Be(PadFocusStep.Send(PadKey.Escape));
        Plan(NavigationAction.Back, FocusedKind.Button, canGoBack: true).Kind.Should().Be(PadFocusStepKind.GoBack);
        Plan(NavigationAction.Back, FocusedKind.Button).Should().Be(PadFocusStep.Send(PadKey.Escape));
    }

    [Fact]
    public void Shoulders_switch_pages_except_in_a_flyout_or_the_player()
    {
        Plan(NavigationAction.NextPage, FocusedKind.Button).Should().Be(PadFocusStep.Of(PadFocusStepKind.SwitchPage, 1));
        Plan(NavigationAction.PreviousPage, FocusedKind.None).Should().Be(PadFocusStep.Of(PadFocusStepKind.SwitchPage, -1));
        Plan(NavigationAction.NextPage, FocusedKind.Button, inPopup: true).Should().Be(PadFocusStep.Nothing);
        Plan(NavigationAction.NextPage, FocusedKind.Button, canSwitchPage: false).Should().Be(PadFocusStep.Nothing);
    }

    [Fact]
    public void Triggers_scroll_a_page_outside_flyouts()
    {
        Plan(NavigationAction.PageDown, FocusedKind.Toggle).Should().Be(PadFocusStep.Of(PadFocusStepKind.ScrollPage, 1));
        Plan(NavigationAction.PageUp, FocusedKind.ListItem).Should().Be(PadFocusStep.Of(PadFocusStepKind.ScrollPage, -1));
        Plan(NavigationAction.PageUp, FocusedKind.ListItem, inPopup: true).Should().Be(PadFocusStep.Nothing);
    }

    [Fact]
    public void North_opens_a_context_menu_only_where_there_is_one()
    {
        Plan(NavigationAction.ContextMenu, FocusedKind.ListItem, hasContextMenu: true).Kind.Should().Be(PadFocusStepKind.ShowContextMenu);
        Plan(NavigationAction.ContextMenu, FocusedKind.ListItem).Should().Be(PadFocusStep.Nothing);
        Plan(NavigationAction.ContextMenu, FocusedKind.ListItem, inPopup: true, hasContextMenu: true).Should().Be(PadFocusStep.Nothing);
    }

    [Fact]
    public void Start_focuses_the_main_menu_or_closes_a_flyout()
    {
        Plan(NavigationAction.Menu, FocusedKind.Button).Kind.Should().Be(PadFocusStepKind.FocusMenu);
        Plan(NavigationAction.Menu, FocusedKind.Button, inPopup: true).Should().Be(PadFocusStep.Send(PadKey.Escape));
    }

    [Theory]
    [InlineData(AppPage.Library, 1, AppPage.Settings)]
    [InlineData(AppPage.Settings, -1, AppPage.Library)]
    [InlineData(AppPage.GameDetails, 1, AppPage.Library)]
    [InlineData(AppPage.CatalogImport, -1, AppPage.Settings)]
    [InlineData(null, 1, AppPage.Library)]
    public void Switching_pages_follows_the_navigation_pane(AppPage? current, int delta, AppPage expected)
    {
        PadFocusPlanner.SwitchTarget(current, delta).Should().Be(expected);
    }

    [Theory]
    [InlineData(AppPage.Library, -1)]
    [InlineData(AppPage.Settings, 1)]
    [InlineData(AppPage.Player, 1)]
    public void Switching_pages_stops_at_the_ends_and_never_leaves_the_player(AppPage current, int delta)
    {
        PadFocusPlanner.SwitchTarget(current, delta).Should().BeNull();
    }
}
