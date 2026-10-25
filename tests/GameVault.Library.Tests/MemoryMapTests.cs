using GameVault.Library.Memory;

namespace GameVault.Library.Tests;

/// <summary>Memory maps (SPEC §7.9, Phase 9): values, encodings and conditions.</summary>
public sealed class MemoryMapTests
{
    private const string Example = """
        {
          "sha1": "0123456789abcdef0123456789abcdef01234567",
          "title": "Example Quest",
          "values": {
            "lives":  { "address": "0x0010", "offset": 1 },
            "score":  { "address": "$0020", "size": 2, "encoding": "bcd" },
            "health": { "address": "0x0030", "mask": "0xF0", "shift": 4, "max": 16 },
            "room":   { "address": "0x0040" },
            "mode":   { "address": "0x0050" }
          },
          "playing": [ { "value": "mode", "op": "==", "target": 1 } ],
          "lives": "lives",
          "map": { "room": "room", "columns": 8 },
          "overlay": [ { "value": "health", "label": "Health", "when": [ { "address": "0x0051", "value": "0x02" } ] } ]
        }
        """;

    private static Func<int, byte> Ram(params (int Address, byte Value)[] bytes)
    {
        var ram = new byte[0x800];
        foreach (var (address, value) in bytes)
        {
            ram[address] = value;
        }

        return address => ram[address & 0x7FF];
    }

    [Fact]
    public void Values_read_binary_bcd_masked_and_offset()
    {
        var map = MemoryMap.Parse(Example);
        var ram = Ram((0x10, 2), (0x20, 0x34), (0x21, 0x12), (0x30, 0xA7));

        map.Read("lives", ram).Should().Be(3, "RAM 2 + offset 1");
        map.Read("score", ram).Should().Be(1234, "BCD, low byte first");
        map.Read("health", ram).Should().Be(10, "high nibble of $A7");
        map.Values["health"].Max.Should().Be(16);
    }

    [Fact]
    public void Conditions_compare_named_values_or_raw_bytes()
    {
        var map = MemoryMap.Parse(Example);

        map.IsPlaying(Ram((0x50, 1))).Should().BeTrue();
        map.IsPlaying(Ram((0x50, 0))).Should().BeFalse();
        map.Overlay[0].When[0].Holds(map, Ram((0x51, 2))).Should().BeTrue("value stands in for target on raw conditions");

        foreach (var (op, expected) in new[] { ("<", false), ("<=", true), (">=", true), (">", false), ("!=", false) })
        {
            MemoryCondition.Named("lives", op, 3).Holds(map, Ram((0x10, 2))).Should().Be(expected, op);
        }

        MemoryCondition.Raw(0x10, ">", 1).Holds(map, Ram((0x10, 2))).Should().BeTrue();
        MemoryCondition.Named("lives", ">=", 3).Describe().Should().Be("lives ≥ 3");
        MemoryCondition.Raw(0x10, "==", 7).Describe().Should().Be("RAM $0010 = 7");
    }

    [Fact]
    public void Undefined_names_are_reported()
    {
        var bad = Example.Replace("\"lives\": \"lives\"", "\"lives\": \"hearts\"");
        var act = () => MemoryMap.Parse(bad);
        act.Should().Throw<FormatException>().WithMessage("*\"hearts\"*");
    }

    [Fact]
    public void Folders_of_maps_are_loaded_by_hash()
    {
        var folder = Directory.CreateTempSubdirectory("gamevault-memory-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "example.json"), Example);
            File.WriteAllText(Path.Combine(folder.FullName, "broken.json"), "{ \"values\": ");
            var maps = new MemoryMaps(folder.FullName);

            maps.For("0123456789ABCDEF0123456789ABCDEF01234567")!.Title.Should().Be("Example Quest");
            maps.Errors.Should().ContainSingle().Which.Should().StartWith("broken.json");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
