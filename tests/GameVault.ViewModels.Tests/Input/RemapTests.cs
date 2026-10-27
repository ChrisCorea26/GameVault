using FluentAssertions;
using GameVault.Core.Input;
using GameVault.ViewModels;
using GameVault.ViewModels.SampleData;
using GameVault.ViewModels.Services;
using GameVault.ViewModels.Services.Input;

namespace GameVault.ViewModels.Tests.Input;

/// <summary>Remap… (SPEC §5.4, docs/design/input.md §6; GV-001, GV-018).</summary>
public sealed class RemapTests
{
    private const string Keyboard = IInputDevicesService.KeyboardId;
    private const int KeyK = 0x4B, KeyZ = 0x5A, KeyX = 0x58;

    [Fact]
    public void Binding_a_key_unbinds_whatever_it_did_and_only_changes_are_resolved()
    {
        var set = InputRemapSet.Empty.Bind(Keyboard, 0, InputSystem.Nes, null, "B", InputCodes.Key(KeyX));

        var effective = set.Effective(Keyboard, 0, InputSystem.Nes, null);
        effective["B"].Should().Be(InputCodes.Key(KeyX));
        effective["A"].Should().Be(InputCodes.None, "X was A, and one key does one thing");
        set.Resolve(Keyboard, 0, InputSystem.Nes, null).Should().BeEquivalentTo(
        [
            new ResolvedRemap(ControllerState.B, false, KeyKind.Normal, InputCodes.Key(KeyX)),
            new ResolvedRemap(ControllerState.A, false, KeyKind.Normal, InputCodes.None),
        ]);
        set.Resolve(Keyboard, 0, InputSystem.Snes, null).Should().BeEmpty("remaps are per system");

        // Binding back to the default leaves nothing stored.
        set = set.Bind(Keyboard, 0, InputSystem.Nes, null, "B", InputCodes.Key(KeyZ))
            .Bind(Keyboard, 0, InputSystem.Nes, null, "A", InputCodes.Key(KeyX));
        set.Entries.Should().BeEmpty();
    }

    [Fact]
    public void A_games_own_remap_sits_on_top_of_the_one_for_every_game_and_survives_json()
    {
        var set = InputRemapSet.Empty
            .Bind("guid-1", 1, InputSystem.Genesis, null, "C", InputCodes.Pad("RightShoulder"))
            .Bind("guid-1", 1, InputSystem.Genesis, "sha1:aa", "Z", InputCodes.Pad("East"));

        set = InputRemapSet.Parse(set.ToJson());

        set.Effective("guid-1", 1, InputSystem.Genesis, null)["C"].Should().Be(InputCodes.Pad("RightShoulder"));
        set.Effective("guid-1", 1, InputSystem.Genesis, null)["Z"].Should().Be(InputCodes.None);
        var game = set.Effective("guid-1", 1, InputSystem.Genesis, "sha1:aa");
        game["Z"].Should().Be(InputCodes.Pad("East"));
        game["C"].Should().Be(InputCodes.Pad("RightShoulder"));
        set.Effective("guid-2", 1, InputSystem.Genesis, null)["C"].Should().Be(InputCodes.Pad("East"), "remaps are per controller model");
        set.Effective("guid-1", 0, InputSystem.Genesis, null)["C"].Should().Be(InputCodes.Pad("East"), "and per player");
        InputRemapSet.Parse("not json").Entries.Should().BeEmpty();
    }

    [Fact]
    public void One_handed_presets_cover_every_system_with_the_stick_on_the_n64_and_ps2()
    {
        var set = InputRemapSet.Empty.ApplyKeyboardPreset(0, 2); // right hand

        set.Effective(Keyboard, 0, InputSystem.Nes, null)["Up"].Should().Be(InputCodes.Key(0x49)); // I
        set.Effective(Keyboard, 0, InputSystem.Nes, null)["B"].Should().Be(InputCodes.Key(0x55)); // U
        set.Effective(Keyboard, 0, InputSystem.Nintendo64, null)["StickUp"].Should().Be(InputCodes.Key(0x49));
        set.Effective(Keyboard, 0, InputSystem.Nintendo64, null)["C"].Should().Be(InputCodes.None, "I was C-up");
        set.ApplyKeyboardPreset(0, 0).Entries.Should().BeEmpty("Standard is the defaults");
    }

    [Fact]
    public void Buttons_are_named_the_way_each_system_names_them()
    {
        InputActions.For(InputSystem.Genesis).Single(a => a.Id == "B").Label.Should().Be("A");
        InputActions.For(InputSystem.Sega).Single(a => a.Id == "Start").Label.Should().Be("Pause");
        InputActions.For(InputSystem.PlayStation).Single(a => a.Id == "B").Label.Should().Be("Cross");
        InputActions.For(InputSystem.Nintendo64).Single(a => a.Id == "C").Label.Should().Be("C-up");
        InputActions.For(InputSystem.PlayStation2).Should().Contain(a => a.Label == "R3");
        foreach (var system in InputActions.Systems)
        {
            InputActions.For(system).Select(a => a.Id).Should().OnlyHaveUniqueItems(system.ToString());
        }
    }

    [Fact]
    public async Task The_editor_waits_for_a_key_saves_and_applies_it()
    {
        var devices = new SampleInputDevicesService();
        var store = new InMemorySettingsStore();
        var vm = new InputSettingsViewModel(devices, store);
        var keyboard = vm.Ports[0].Sources.Single(s => s.IsKeyboard);

        keyboard.RemapCommand.Execute(null);
        vm.Remap.IsOpen.Should().BeTrue();
        vm.Remap.Title.Should().Be("Remap Keyboard for Player 1");
        vm.Remap.Rows.Select(r => r.Label).Should().StartWith(["Up", "Down", "Left", "Right", "B", "A"]);
        var b = vm.Remap.Rows.Single(r => r.Id == "B");
        b.BindingText.Should().Be("Z");

        b.ChangeCommand.Execute(null);
        vm.Remap.IsListening.Should().BeTrue();
        vm.Remap.CaptureKey(0x74).Should().BeTrue(); // F5 is a hotkey
        vm.Remap.Message.Should().Contain("GameVault's own keys");
        vm.Remap.IsListening.Should().BeTrue();
        vm.Remap.CaptureKey(KeyX).Should().BeTrue();

        vm.Remap.IsListening.Should().BeFalse();
        vm.Remap.Message.Should().Be("X was A; that's unassigned now.");
        vm.Remap.Rows.Single(r => r.Id == "B").BindingText.Should().Be("X");
        vm.Remap.Rows.Single(r => r.Id == "A").BindingText.Should().Be("—");
        devices.Remaps.Effective(Keyboard, 0, InputSystem.Nes, null)["B"].Should().Be(InputCodes.Key(KeyX));
        await Task.Yield();
        InputRemapSet.Parse(store.Current.InputRemaps).Entries.Should().ContainSingle();

        vm.Remap.ResetToDefaultsCommand.Execute(null);
        vm.Remap.Rows.Single(r => r.Id == "B").BindingText.Should().Be("Z");
        devices.Remaps.Entries.Should().BeEmpty();
    }

    [Fact]
    public void A_controller_is_remapped_by_pressing_its_button_and_per_game_in_the_player()
    {
        var devices = new SampleInputDevicesService();
        var vm = new InputSettingsViewModel(devices, new InMemorySettingsStore());
        vm.SetGame("sha1:bb", "Coral Coast", GameVault.Core.Systems.GameSystem.MasterSystem);
        var pad = vm.Ports[0].Sources.Single(s => !s.IsKeyboard);

        pad.RemapCommand.Execute(null);
        vm.Remap.SystemIndex.Should().Be((int)InputSystem.Sega, "the running game's system comes first");
        vm.Remap.HasGame.Should().BeTrue();
        vm.Remap.ThisGameOnly = true;
        vm.Remap.Rows.Single(r => r.Id == "B").Label.Should().Be("Button 1");

        vm.Remap.Rows.Single(r => r.Id == "B").ChangeCommand.Execute(null);
        devices.CaptureMode.Should().BeTrue();
        devices.SimulateButton("pad:2", "North"); // another pad: ignored
        vm.Remap.IsListening.Should().BeTrue();
        devices.SimulateButton("pad:1", "North");

        devices.CaptureMode.Should().BeFalse();
        devices.Remaps.Effective("xbox", 0, InputSystem.Sega, "sha1:bb")["B"].Should().Be(InputCodes.Pad("North"));
        devices.Remaps.Effective("xbox", 0, InputSystem.Sega, null)["B"].Should().Be(InputCodes.Pad("South"), "only for this game");
    }

    [Fact]
    public void Pads_can_go_to_players_3_and_4_and_any_two_players_can_swap()
    {
        var devices = new SampleInputDevicesService();
        devices.SetPlayerCount(4); // a Four Score game
        var vm = new InputSettingsViewModel(devices, new InMemorySettingsStore());

        vm.Ports.Should().HaveCount(4);
        vm.Ports[0].SwapChoices.Select(c => c.Label).Should().Equal("Swap with Player 2", "Swap with Player 3", "Swap with Player 4");
        vm.Ports[2].Sources.Should().ContainSingle(s => s.Name == "DualSense Wireless Controller", "waiting pads fill players 3 and 4");

        vm.Ports[0].SwapChoices[2].ChooseCommand.Execute(null); // Player 1 ↔ Player 4
        vm.Ports[3].Sources.Select(s => s.Name).Should().Contain("Xbox Wireless Controller");

        devices.SetPlayerCount(2);
        vm.Unassigned.Should().HaveCount(2, "the pads on players 3 and 4 wait again");
        vm.Unassigned[0].Players.Select(p => p.Label).Should().Equal("Use for Player 1", "Use for Player 2");
    }
}
