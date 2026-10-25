using GameVault.Core.Cheats;
using GameVault.Core.Tests.Cartridge;

namespace GameVault.Core.Tests.Cheats;

/// <summary>Game Genie and RAM codes (SPEC §7.8, Phase 8).</summary>
public class CheatTests
{
    [Fact]
    public void A_well_known_six_letter_code_decodes_to_its_documented_patch()
    {
        // Super Mario Bros.' "infinite lives" turns DEC $075A at $91D9 into LDA (opcode $AD).
        var code = CheatCode.Parse("SXIOPO");

        code.Kind.Should().Be(CheatKind.Rom);
        code.Address.Should().Be(0x91D9);
        code.Value.Should().Be(0xAD);
        code.Compare.Should().BeNull();
        code.Describe().Should().Be("$91D9 = $AD");
    }

    [Fact]
    public void Eight_letter_codes_have_a_compare_value()
    {
        var code = new CheatCode(CheatKind.Rom, 0xC3A2, 0x05, 0x1F);
        var letters = code.ToString();

        letters.Should().HaveLength(8);
        CheatCode.Parse(letters).Should().Be(code);
        code.Describe().Should().Be("$C3A2 = $05 if it was $1F");
    }

    [Fact]
    public void Encoding_and_decoding_round_trip_for_every_address_value_and_compare()
    {
        var random = new Random(3);
        for (var i = 0; i < 20000; i++)
        {
            var address = (ushort)(0x8000 | random.Next(0x8000));
            var value = (byte)random.Next(256);
            byte? compare = random.Next(2) == 0 ? null : (byte)random.Next(256);
            var code = new CheatCode(CheatKind.Rom, address, value, compare);

            CheatCode.Parse(code.ToString()).Should().Be(code);
        }
    }

    [Theory]
    [InlineData("sxio-po", true)]
    [InlineData(" SXIO PO ", true)]
    [InlineData("SXIOP", false)]
    [InlineData("SXIOPB", false)]
    [InlineData("", false)]
    [InlineData("075A:09", true)]
    [InlineData("$075A:$09", true)]
    [InlineData("6123?03:7F", true)]
    [InlineData("2000:01", false)]
    [InlineData("075A:100", false)]
    [InlineData("hello", false)]
    public void Codes_are_checked_with_a_reason(string text, bool valid)
    {
        CheatCode.TryParse(text, out _, out var error).Should().Be(valid);
        if (!valid)
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Bad_letters_are_named()
    {
        CheatCode.TryParse("SXIOPB", out _, out var error);
        error.Should().Contain("\"B\"");
    }

    [Fact]
    public void Ram_codes_format_and_describe_themselves()
    {
        var code = CheatCode.Parse("075a:09");
        code.Should().Be(new CheatCode(CheatKind.Ram, 0x075A, 0x09));
        code.ToString().Should().Be("075A:09");
        code.Describe().Should().Be("RAM $075A = $09");
        CheatCode.Parse("6123?03:7F").ToString().Should().Be("6123?03:7F");
    }

    [Fact]
    public void Rom_codes_replace_reads_and_respect_the_compare_byte()
    {
        // BankedRom: every 8 KB bank is filled with its number, so $8000 reads 0 until MMC1 switches it.
        var console = NesConsole.FromBytes(BankedRom.Build(0, 4, 8));
        console.Power();
        console.SetCheats(
        [
            new CheatCode(CheatKind.Rom, 0x8010, 0x42),
            new CheatCode(CheatKind.Rom, 0x8020, 0x55, Compare: 0x00),
            new CheatCode(CheatKind.Rom, 0x8030, 0x66, Compare: 0x07),
        ]);

        console.Bus.Read(0x8010).Should().Be(0x42);
        console.Bus.Read(0x8020).Should().Be(0x55, "the real byte is 0, which matches");
        console.Bus.Read(0x8030).Should().Be(0x00, "the real byte isn't 7, so the code doesn't apply");
        console.Bus.Read(0x8011).Should().Be(0x00);

        console.SetCheats([]);
        console.Bus.Read(0x8010).Should().Be(0x00, "codes can be switched off");
    }

    [Fact]
    public void Ram_codes_are_written_before_every_frame()
    {
        var console = NesConsole.FromBytes(BankedRom.Build(0, 4, 8));
        console.Power();
        console.SetCheats([CheatCode.Parse("0300:80"), CheatCode.Parse("0301?11:22")]);

        for (var frame = 0; frame < 3; frame++)
        {
            console.Bus.PokeRam(0x0300, 0x05); // as if the game changed it
            console.Bus.PokeRam(0x0301, frame == 1 ? (byte)0x11 : (byte)0x33);
            console.RunFrame();

            console.Bus.Peek(0x0300).Should().Be(0x80);
            console.Bus.Peek(0x0301).Should().Be(frame == 1 ? (byte)0x22 : (byte)0x33, "the compare value must match");
        }
    }

    [Fact]
    public void Save_states_record_which_codes_were_on()
    {
        var image = BankedRom.Build(0, 4, 8);
        var console = NesConsole.FromBytes(image);
        console.Power();
        var codes = new[] { CheatCode.Parse("SXIOPO"), CheatCode.Parse("0010:05") };
        console.SetCheats(codes);
        console.RunFrame();
        var withCodes = console.SaveSnapshot();

        console.SetCheats([]);
        console.RunFrame();
        console.LoadSnapshot(withCodes);
        console.Cheats.Should().Equal(codes, "loading the state turns its codes back on");

        var fresh = NesConsole.FromBytes(image);
        fresh.LoadSnapshot(withCodes);
        fresh.Cheats.Should().Equal(codes);
        fresh.SaveSnapshot().Should().Equal(withCodes);
    }
}
