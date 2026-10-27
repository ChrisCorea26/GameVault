using FluentAssertions;
using GameVault.Core.Input;
using GameVault.Core.PlayStation;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>The DualShock on SIO0 (outstanding-work plan §11), one byte at a time as the BIOS and libpad send them.</summary>
public sealed class PsDualShockTests
{
    /// <summary>A packet after the 01h select byte; checks that /ACK follows every byte but the last.</summary>
    private static byte[] Packet(PsDualShock pad, params byte[] command)
    {
        var reply = new byte[command.Length];
        for (var i = 0; i < command.Length; i++)
        {
            reply[i] = pad.Exchange(i, command[i], out var ack);
            ack.Should().Be(i < command.Length - 1, $"byte {i} of {command.Length}");
        }

        return reply;
    }

    private static PsDualShock Pad(ControllerState input = ControllerState.None)
    {
        var pad = new PsDualShock();
        pad.Reset();
        pad.Input = input;
        return pad;
    }

    /// <summary>Config mode, analog on and locked, config mode off: as games turn the sticks on.</summary>
    private static void SwitchToAnalog(PsDualShock pad)
    {
        Packet(pad, 0x43, 0x00, 0x01, 0x00);
        Packet(pad, 0x44, 0x00, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00);
        Packet(pad, 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
    }

    [Fact]
    public void A_digital_poll_is_the_buttons_and_the_stick_works_the_dpad()
    {
        var pad = Pad((ControllerState.B | ControllerState.Start).WithStick(127, 0));

        var reply = Packet(pad, 0x42, 0x00, 0x00, 0x00);

        reply[0].Should().Be(0x41);
        reply[1].Should().Be(0x5A);
        ((~reply[2]) & 0xFF).Should().Be((1 << 3) | (1 << 5), "Start, and Right from the stick");
        ((~reply[3]) & 0xFF).Should().Be(1 << 6, "Cross");
    }

    [Fact]
    public void Games_switch_it_to_analog_and_read_both_sticks()
    {
        var pad = Pad(ControllerState.L3.WithStick(127, 127).WithRightStick(-128, 0));
        SwitchToAnalog(pad);
        pad.Analog.Should().BeTrue();

        var reply = Packet(pad, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

        reply[0].Should().Be(0x73);
        ((~reply[2]) & 0xFF).Should().Be(1 << 1, "L3, and the stick no longer works the D-pad");
        reply[4..8].Should().Equal(new byte[] { 0x00, 0x80, 0xFF, 0x01 }, "right X, right Y, left X, left Y");

        var status = Packet(pad, 0x43, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00);
        status[0].Should().Be(0x73, "entering config mode answers like a poll");
        Packet(pad, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00).Should().Equal(0xF3, 0x5A, 0x01, 0x02, 0x01, 0x02, 0x01, 0x00);
        Packet(pad, 0x46, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00)[2..].Should().Equal(0x00, 0x00, 0x01, 0x01, 0x01, 0x14);
        Packet(pad, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)[5].Should().Be(0x04);
    }

    [Fact]
    public void The_motor_map_lets_the_poll_spin_the_motors()
    {
        var pad = Pad();
        SwitchToAnalog(pad);
        Packet(pad, 0x43, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00);
        Packet(pad, 0x4D, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF).Should().Equal(0xF3, 0x5A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Packet(pad, 0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);

        Packet(pad, 0x42, 0x00, 0x01, 0x80, 0x00, 0x00, 0x00, 0x00);

        pad.SmallMotor.Should().BeTrue();
        pad.LargeMotor.Should().Be(0x80);
    }

    [Fact]
    public void Analog_mode_from_settings_and_save_states()
    {
        var pad = new PsDualShock { StartAnalog = true };
        pad.Reset();
        Packet(pad, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)[0].Should().Be(0x73);

        var writer = new StateWriter();
        pad.SaveState(writer);
        var copy = new PsDualShock();
        copy.Reset();
        copy.LoadState(new StateReader(writer.ToArray()));
        copy.Analog.Should().BeTrue();
    }
}

/// <summary>24-bit display (FMVs) at full depth (outstanding-work plan §11).</summary>
public sealed class PsGpuTrueColorTests
{
    [Fact]
    public void A_24_bit_display_keeps_every_bit_and_a_15_bit_one_does_not()
    {
        var gpu = new PsGpu();
        gpu.WriteGp1(0x03u << 24);           // display on
        gpu.WriteGp1((0x08u << 24) | 0x11);  // 320 wide, 24-bit
        gpu.WriteGp0(0xA0000000);            // copy to VRAM at (0, 0), 2×1 halfwords
        gpu.WriteGp0(0x00000000);
        gpu.WriteGp0(0x00010002);
        gpu.WriteGp0(0x0056_3412);           // R, G | B, (next pixel's R)
        var frame = new ushort[gpu.DisplayWidth * gpu.DisplayHeight];
        var argb = new uint[frame.Length];

        gpu.CopyDisplay(frame, argb).Should().BeTrue();

        argb[0].Should().Be(0xFF123456);
        frame[0].Should().Be((ushort)((0x12 >> 3) | (0x34 >> 3) << 5 | (0x56 >> 3) << 10));

        gpu.WriteGp1((0x08u << 24) | 0x01);  // 15-bit
        gpu.CopyDisplay(frame, argb).Should().BeFalse();
    }
}
