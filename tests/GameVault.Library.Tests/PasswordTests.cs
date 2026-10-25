using GameVault.Library.Passwords;
using GameVault.Library.Tests.Fixtures;

namespace GameVault.Library.Tests;

public sealed class PasswordTests : IDisposable
{
    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private const string RamDefinition = """
        {
          // A fictional game: the password screen sets $10 to 7; 8 characters live at $0500.
          "sha1": "0123456789abcdef0123456789abcdef01234567",
          "detect": [ { "address": "0x0010", "value": "0x07" }, { "address": "$0011", "mask": "0x80", "value": 128 } ],
          "stableFrames": 10,
          "source": { "from": "ram", "address": "0x0500", "length": 8 },
          "alphabet": "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
          "groups": [4, 4]
        }
        """;

    [Fact]
    public void Ram_definitions_detect_the_screen_and_decode_with_a_string_alphabet()
    {
        var definition = PasswordDefinition.Parse(RamDefinition);
        var ram = new byte[0x800];
        Func<int, byte> read = a => ram[a & 0x7FF];

        definition.IsPasswordScreen(read).Should().BeFalse();
        ram[0x10] = 7;
        ram[0x11] = 0x81;
        definition.IsPasswordScreen(read).Should().BeTrue();
        definition.StableFrames.Should().Be(10);

        byte[] password = [6, 0, 12, 4, 26, 27, 40, 7]; // G A M E 0 1 ? H
        password.CopyTo(ram, 0x500);
        definition.Decode(read, _ => 0).Should().Be("GAME 01?H");
    }

    [Fact]
    public void Nametable_definitions_read_rows_of_tiles_through_a_value_map()
    {
        var definition = PasswordDefinition.Parse("""
            {
              "detect": [ { "address": 0, "value": 0 } ],
              "source": { "from": "nametable", "address": "0x2185", "length": 6, "columns": 3, "rowStride": 64 },
              "alphabet": { "0x41": "X", "0x42": "Y", "0x43": "Z" }
            }
            """);
        var nametable = new Dictionary<int, byte>
        {
            [0x2185] = 0x41, [0x2186] = 0x42, [0x2187] = 0x43,
            [0x21C5] = 0x43, [0x21C6] = 0x42, [0x21C7] = 0x41,
        };

        definition.Decode(_ => 0, a => nametable.GetValueOrDefault(a)).Should().Be("XYZZYX");
    }

    [Fact]
    public void Bundled_definitions_load_and_skip_broken_files()
    {
        var folder = Path.Combine(_fx.Root, "passwords");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "game.json"), RamDefinition);
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ nope");

        var definitions = new PasswordDefinitions(folder);

        definitions.Count.Should().Be(1);
        definitions.Errors.Should().ContainSingle();
        definitions.For("0123456789ABCDEF0123456789ABCDEF01234567").Should().NotBeNull();
        definitions.For(null).Should().BeNull();
    }

    [Fact]
    public void Store_keeps_passwords_newest_first_with_screenshots()
    {
        var store = new PasswordStore(Path.Combine(_fx.Root, "Passwords"));
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var first = store.Add("sha1:abc", " GAME 0001 ", [0x89], automatic: true, t0);
        store.Add("sha1:abc", "GAME 0002", [], automatic: false, t0.AddMinutes(5), note: "Before the castle");

        var list = store.List("sha1:abc");
        list.Select(p => p.Password).Should().Equal("GAME 0002", "GAME 0001");
        list[0].Note.Should().Be("Before the castle");
        File.Exists(store.ScreenshotPath("sha1:abc", first.Id)).Should().BeTrue();
        store.IsLatest("sha1:abc", "GAME 0002").Should().BeTrue();

        store.Delete("sha1:abc", first.Id);
        store.List("sha1:abc").Should().ContainSingle();
        File.Exists(store.ScreenshotPath("sha1:abc", first.Id)).Should().BeFalse();
    }
}
