using GameVault.Core.Input;
using GameVault.Core.State;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Input;

public class ZapperAndFourScoreTests
{
    /// <summary>NROM program: sets the backdrop color to <paramref name="color"/> with rendering off, then loops.</summary>
    private static NesConsole BackdropConsole(byte color)
    {
        byte[] program =
        [
            0xA9, 0x3F, 0x8D, 0x06, 0x20, // LDA #$3F; STA $2006
            0xA9, 0x00, 0x8D, 0x06, 0x20, // LDA #$00; STA $2006
            0xA9, color, 0x8D, 0x07, 0x20, // LDA #color; STA $2007
            0xA9, 0x00, 0x8D, 0x06, 0x20, // LDA #0; STA $2006
            0x8D, 0x06, 0x20,             // STA $2006 (v away from the palette, so the backdrop shows)
            0x4C, 0x17, 0xC0,             // JMP * (loop)
        ];
        var console = NesConsole.FromBytes(TestRoms.Nrom(program));
        console.ConnectInput(InputSetup.Zapper);
        console.Power();
        console.RunFrame();
        console.RunFrame();
        return console;
    }

    private static void RunUntil(NesConsole console, int scanline, int dot)
    {
        while (!(console.Ppu.Scanline == scanline && console.Ppu.Dot >= dot))
        {
            console.StepInstruction();
        }
    }

    [Fact]
    public void Light_is_seen_only_for_a_bright_pixel_the_beam_drew_recently()
    {
        var console = BackdropConsole(0x30); // white
        RunUntil(console, 100, 60);

        console.Zapper.Aim = (10, 95);
        console.Zapper.SeesLight().Should().BeTrue("line 95 was drawn 5 lines ago");

        console.Zapper.Aim = (10, 60);
        console.Zapper.SeesLight().Should().BeFalse("the sensor has gone dark 40 lines later");

        console.Zapper.Aim = (10, 150);
        console.Zapper.SeesLight().Should().BeFalse("the beam hasn't reached line 150 yet this frame");

        console.Zapper.Aim = null;
        console.Zapper.SeesLight().Should().BeFalse("aiming away from the screen");
    }

    [Fact]
    public void Dark_pixels_never_register()
    {
        var console = BackdropConsole(0x0F); // black
        RunUntil(console, 100, 60);
        console.Zapper.Aim = (10, 95);

        console.Zapper.SeesLight().Should().BeFalse();
    }

    [Fact]
    public void Port_2_reports_light_on_d3_and_trigger_on_d4()
    {
        var console = BackdropConsole(0x30);
        RunUntil(console, 100, 60);
        console.Zapper.Aim = (10, 98);
        console.Zapper.Trigger = true;

        var value = console.Bus.Read(0x4017);

        (value & 0x08).Should().Be(0, "D3 is 0 when light is seen");
        (value & 0x10).Should().Be(0x10, "D4 is 1 while the trigger is pulled");

        console.Zapper.Aim = null;
        console.Zapper.Trigger = false;
        (console.Bus.Read(0x4017) & 0x18).Should().Be(0x08);
    }

    [Fact]
    public void Four_score_sends_two_controllers_then_the_signature_then_ones()
    {
        var first = new StandardController { State = ControllerState.A | ControllerState.Start };
        var third = new StandardController { State = ControllerState.Right };
        var port = new FourScorePort(first, third, 0);

        port.Write(1);
        port.Write(0);
        var bits = Enumerable.Range(0, 26).Select(_ => port.Read()).ToArray();

        bits[..8].Should().Equal(1, 0, 0, 1, 0, 0, 0, 0); // A, B, Select, Start, ...
        bits[8..16].Should().Equal(0, 0, 0, 0, 0, 0, 0, 1); // ... Right
        bits[16..24].Should().Equal(0, 0, 0, 1, 0, 0, 0, 0); // $4016 signature
        bits[24..].Should().Equal(1, 1);

        var second = new FourScorePort(new StandardController(), new StandardController(), 1);
        second.Write(1);
        second.Write(0);
        var secondBits = Enumerable.Range(0, 24).Select(_ => second.Read()).ToArray();
        secondBits[16..].Should().Equal(0, 0, 1, 0, 0, 0, 0, 0); // $4017 signature
    }

    [Fact]
    public void Four_score_players_3_and_4_are_reachable_through_the_console()
    {
        var console = NesConsole.FromBytes(TestRoms.Nrom([0x4C, 0x00, 0xC0]));
        console.ConnectInput(InputSetup.FourScore);
        console.Power();
        console.SetControllerState(2, ControllerState.B);

        console.Bus.Write(0x4016, 1);
        console.Bus.Write(0x4016, 0);
        var bits = Enumerable.Range(0, 16).Select(_ => console.Bus.Read(0x4016) & 1).ToArray();

        bits[9].Should().Be(1, "player 3's B is the 10th bit on $4016");
    }

    [Fact]
    public void State_round_trips_the_zapper_and_rejects_a_different_setup()
    {
        var console = BackdropConsole(0x30);
        console.Zapper.Aim = (12, 34);
        console.Zapper.Trigger = true;
        var snapshot = console.SaveSnapshot();

        console.Zapper.Aim = null;
        console.Zapper.Trigger = false;
        console.LoadSnapshot(snapshot);

        console.Zapper.Aim.Should().Be((12, 34));
        console.Zapper.Trigger.Should().BeTrue();

        console.ConnectInput(InputSetup.Controllers);
        var act = () => console.LoadSnapshot(snapshot);
        act.Should().Throw<StateFormatException>().WithMessage("*different controllers*");
    }

    [Fact]
    public void Header_overrides_change_the_board()
    {
        var rom = GameVault.Core.Cartridge.INesRom.Parse(TestRoms.Nrom([0x4C, 0x00, 0xC0])) with
        {
            Mirroring = GameVault.Core.Cartridge.Mirroring.Vertical,
            HasBattery = true,
        };

        var console = new NesConsole(rom);

        console.Mapper.Mirroring.Should().Be(GameVault.Core.Cartridge.Mirroring.Vertical);
        ((GameVault.Core.Cartridge.IBatteryBacked)console.Mapper).HasBattery.Should().BeTrue();
    }
}
