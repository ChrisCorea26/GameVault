using FluentAssertions;
using GameVault.Platform.Input;

namespace GameVault.Platform.Tests;

public sealed class Sc3000KeyboardMappingTests
{
    [Fact]
    public void Pc_keys_land_where_the_sc3000_has_them()
    {
        Sc3000Keyboard.Position('1').Should().Be((0, 0));
        Sc3000Keyboard.Position('D').Should().Be((2, 2));
        Sc3000Keyboard.Position('0').Should().Be((2, 8));
        Sc3000Keyboard.Position(VirtualKeys.Enter).Should().Be((5, 6));
        Sc3000Keyboard.Position(VirtualKeys.LeftShift).Should().Be((6, 11));
        Sc3000Keyboard.Position(0x7B).Should().BeNull("F12 isn't on the SC-3000");
    }

    [Fact]
    public void The_matrix_holds_every_key_held()
    {
        var rows = Sc3000Keyboard.Matrix(['A', 'S', VirtualKeys.Up]);

        rows[0].Should().Be(1 << 2);
        rows[1].Should().Be(1 << 2);
        rows[6].Should().Be(1 << 6);
    }
}
