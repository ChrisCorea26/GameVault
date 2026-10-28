using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Cpu.Sh2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Cpu;

/// <summary>The SH7604's cache (genesis-gaps.md §5): lines, write-through, the areas around it, LRU and CCR.</summary>
public sealed class Sh2CacheTests
{
    private const uint Cached = 0x0000_0000, Through = 0x2000_0000, PurgeArea = 0x4000_0000, AddressArray = 0x6000_0000, DataArray = 0xC000_0000;
    private const byte Enable = 0x01, NoInstructionFill = 0x02, NoDataFill = 0x04, TwoWay = 0x08, PurgeAll = 0x10;

    /// <summary>1 MB of big-endian memory that counts the bus reads it answers.</summary>
    private sealed class Memory : ISh2Bus
    {
        public byte[] Bytes { get; } = new byte[0x100000];

        public int Reads { get; set; }

        public byte Read8(uint address)
        {
            Reads++;
            return Bytes[address & 0xFFFFF];
        }

        public ushort Read16(uint address)
        {
            Reads++;
            return BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFFE)));
        }

        public uint Read32(uint address)
        {
            Reads++;
            return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFFC)));
        }

        public void Write8(uint address, byte value) => Bytes[address & 0xFFFFF] = value;

        public void Write16(uint address, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)(address & 0xFFFFE)), value);

        public void Write32(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFFC)), value);

        /// <summary>Another bus master (the other CPU, the 68000) changes memory behind the cache.</summary>
        public void Poke32(uint address, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)(address & 0xFFFFC)), value);
    }

    private static (Sh2Cpu Cpu, Memory Memory) Machine(byte ccr = Enable | PurgeAll)
    {
        var memory = new Memory();
        memory.Write32(0, 0x1000);
        memory.Write32(4, 0xF000);
        memory.Write16(0x1000, 0x0009); // NOP
        var cpu = new Sh2Cpu(memory);
        cpu.PowerOn();
        cpu.Write8(0xFFFFFE92, ccr); // CCR, as programs set it
        return (cpu, memory);
    }

    [Fact]
    public void The_cache_starts_off_and_ccr_reads_back_without_cp()
    {
        var memory = new Memory();
        var cpu = new Sh2Cpu(memory);
        cpu.PowerOn();
        cpu.Ccr.Should().Be(0);

        cpu.Write8(0xFFFFFE92, 0xC0 | PurgeAll | TwoWay | Enable);
        cpu.Read8(0xFFFFFE92).Should().Be(0xC0 | TwoWay | Enable, "CP always reads 0");
    }

    [Fact]
    public void A_miss_fills_the_line_and_later_reads_hit_without_the_bus()
    {
        var (cpu, memory) = Machine();
        memory.Poke32(0x8000, 0x11223344);
        memory.Poke32(0x800C, 0xAABBCCDD);
        memory.Reads = 0;

        cpu.Read32(Cached | 0x8000).Should().Be(0x11223344);
        var fill = memory.Reads;
        fill.Should().Be(4, "a 16-byte line is four long reads");

        cpu.Read32(Cached | 0x800C).Should().Be(0xAABBCCDD);
        cpu.Read16(Cached | 0x8002).Should().Be(0x3344);
        cpu.Read8(Cached | 0x800D).Should().Be(0xBB);
        memory.Reads.Should().Be(fill, "the rest of the line comes from the cache");
    }

    [Fact]
    public void Memory_changed_behind_the_cache_reads_stale_until_purged()
    {
        var (cpu, memory) = Machine();
        memory.Poke32(0x8000, 1);
        cpu.Read32(Cached | 0x8000).Should().Be(1);

        memory.Poke32(0x8000, 2); // the other CPU writes the same place

        cpu.Read32(Cached | 0x8000).Should().Be(1, "the line is stale, as on the hardware");
        cpu.Read32(Through | 0x8000).Should().Be(2, "the cache-through area always reads memory");

        cpu.Write32(PurgeArea | 0x8000, 0); // associative purge of that line
        cpu.Read32(Cached | 0x8000).Should().Be(2);

        memory.Poke32(0x8000, 3);
        cpu.Write8(0xFFFFFE92, Enable | PurgeAll); // CP: every line invalid
        cpu.Read32(Cached | 0x8000).Should().Be(3);
    }

    [Fact]
    public void Writes_go_through_to_memory_and_update_a_line_they_hit()
    {
        var (cpu, memory) = Machine();
        cpu.Read32(Cached | 0x8000); // the line is cached

        cpu.Write16(Cached | 0x8002, 0xBEEF);
        memory.Read32(0x8000).Should().Be(0x0000BEEF, "write-through");
        memory.Reads = 0;
        cpu.Read32(Cached | 0x8000).Should().Be(0x0000BEEF);
        memory.Reads.Should().Be(0, "the hit was updated in the cache");

        cpu.Write32(Cached | 0x9000, 0x12345678); // a write miss doesn't fill
        memory.Read32(0x9000).Should().Be(0x12345678);
        memory.Reads = 0;
        cpu.Read32(Cached | 0x9000).Should().Be(0x12345678);
        memory.Reads.Should().Be(4, "the line is only filled by the read");
    }

    [Fact]
    public void Od_stops_data_from_filling_lines_and_id_stops_instructions()
    {
        var (cpu, memory) = Machine(Enable | NoDataFill | PurgeAll);
        memory.Poke32(0x8000, 5);
        cpu.Read32(Cached | 0x8000).Should().Be(5);
        memory.Poke32(0x8000, 6);
        cpu.Read32(Cached | 0x8000).Should().Be(6, "with OD set a data miss reads through without filling");

        (cpu, memory) = Machine(Enable | NoInstructionFill | PurgeAll);
        cpu.Step(); // fetches the NOP at $1000 without filling
        memory.Reads = 0;
        cpu.Read16(Cached | 0x1000).Should().Be(0x0009);
        memory.Reads.Should().Be(4, "the instruction fetch left no line, so this data read fills one");
    }

    [Fact]
    public void Four_lines_share_a_set_and_the_least_recently_used_is_replaced()
    {
        var (cpu, memory) = Machine();

        // Addresses $400 apart fall in the same set (address bits 9–4).
        uint[] lines = [0x10000, 0x10400, 0x10800, 0x10C00, 0x11000];
        for (var i = 0; i < lines.Length; i++)
        {
            memory.Poke32(lines[i], (uint)i);
        }

        for (var i = 0; i < 4; i++)
        {
            cpu.Read32(Cached | lines[i]);
        }

        cpu.Read32(Cached | lines[0]); // line 0 is used again: line 1 is now the oldest
        cpu.Read32(Cached | lines[4]); // a fifth line replaces it
        memory.Reads = 0;

        cpu.Read32(Cached | lines[0]).Should().Be(0);
        cpu.Read32(Cached | lines[2]).Should().Be(2);
        cpu.Read32(Cached | lines[3]).Should().Be(3);
        cpu.Read32(Cached | lines[4]).Should().Be(4);
        memory.Reads.Should().Be(0, "those four are cached");
        cpu.Read32(Cached | lines[1]).Should().Be(1);
        memory.Reads.Should().Be(4, "line 1 was the one replaced");
    }

    [Fact]
    public void Two_way_mode_caches_in_ways_2_and_3_and_leaves_ways_0_and_1_as_ram()
    {
        var (cpu, memory) = Machine(Enable | TwoWay | PurgeAll);
        cpu.Write32(DataArray | 0x000, 0xCAFEF00D); // way 0's data: on-chip RAM now

        uint[] lines = [0x20000, 0x20400, 0x20800];
        foreach (var line in lines)
        {
            memory.Poke32(line, line);
            cpu.Read32(Cached | line);
        }

        cpu.Read32(DataArray | 0x000).Should().Be(0xCAFEF00D, "no line was filled into ways 0 or 1");
        memory.Reads = 0;
        cpu.Read32(Cached | lines[2]);
        cpu.Read32(Cached | lines[1]);
        memory.Reads.Should().Be(0, "the last two lines are in ways 2 and 3");
        cpu.Read32(Cached | lines[0]);
        memory.Reads.Should().Be(4, "the first was replaced: two ways hold two lines");
    }

    [Fact]
    public void The_address_array_shows_and_sets_a_ways_tag_valid_bit_and_the_sets_lru()
    {
        var (cpu, _) = Machine(Enable | PurgeAll); // W1–W0 = 0: way 0
        cpu.Read32(Cached | 0x34560);              // set $16; a purged set's first fill goes to way 3

        cpu.Write8(0xFFFFFE92, 0xC0 | Enable);     // show way 3
        var entry = cpu.Read32(AddressArray | 0x160);
        (entry & 0x1FFFFC00).Should().Be(0x34560u & 0x1FFFFC00, "the tag");
        (entry & 4).Should().Be(4, "valid");
        ((entry >> 4) & 0x3F).Should().Be(0x0B, "way 3 was the last used");

        cpu.Write32(AddressArray | 0x160, 0); // invalidate it by hand
        (cpu.Read32(AddressArray | 0x160) & 4).Should().Be(0);
    }

    [Fact]
    public void The_dma_controller_reaches_memory_around_the_cache()
    {
        var (cpu, memory) = Machine();
        memory.Poke32(0x8000, 0x1111);
        cpu.Read32(Cached | 0x8000); // cached

        // Channel 0: one long word from $9000 to $8000 (cached addresses), auto-request.
        memory.Poke32(0x9000, 0x2222);
        cpu.Write32(0xFFFFFF80, 0x9000);
        cpu.Write32(0xFFFFFF84, 0x8000);
        cpu.Write32(0xFFFFFF88, 1);
        cpu.Write32(0xFFFFFFB0, 1); // DMAOR: DME
        cpu.Write32(0xFFFFFF8C, 0x0800 | 0x0200 | 0x0001); // long words, auto-request, enabled
        for (var i = 0; i < 8; i++)
        {
            cpu.Step();
        }

        memory.Read32(0x8000).Should().Be(0x2222, "the DMA wrote memory");
        cpu.Read32(Cached | 0x8000).Should().Be(0x1111, "and left the cached line as it was");
    }

    [Fact]
    public void Save_states_keep_the_cache_and_ccr()
    {
        var (cpu, memory) = Machine();
        memory.Poke32(0x8000, 7);
        cpu.Read32(Cached | 0x8000);
        memory.Poke32(0x8000, 8);

        var writer = new StateWriter();
        cpu.SaveState(writer);
        var (copy, copyMemory) = Machine(0);
        copyMemory.Poke32(0x8000, 8);
        copy.LoadState(new StateReader(writer.ToArray()));

        copy.Ccr.Should().Be(Enable);
        copy.Read32(Cached | 0x8000).Should().Be(7, "the stale line came along");
    }
}
