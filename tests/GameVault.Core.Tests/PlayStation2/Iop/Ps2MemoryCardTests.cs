using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>The memory card's formatted image and its ECC.</summary>
public sealed class Ps2MemoryCardTests
{
    private static readonly Ps2MemoryCard Card = Ps2MemoryCard.CreateFormatted();

    private static ReadOnlySpan<byte> Page(int page) => Card.Data.AsSpan(page * Ps2MemoryCard.RawPageSize, Ps2MemoryCard.PageSize);

    private static ReadOnlySpan<byte> Cluster(int cluster, int offset) =>
        Page(cluster * 2 + offset / 512)[(offset % 512)..];

    [Fact]
    public void The_image_is_8_mb_of_528_byte_pages()
    {
        Ps2MemoryCard.Size.Should().Be(8_650_752);
        Card.Data.Should().HaveCount(16384 * 528);
        Card.Dirty.Should().BeFalse();
    }

    [Fact]
    public void The_ecc_table_matches_the_published_one()
    {
        // The first and last rows of the table PCSX2's FolderMemoryCard and mymc document.
        Ps2MemoryCard.EccTable.AsSpan(0, 16).ToArray().Should().Equal(
            0x00, 0x87, 0x96, 0x11, 0xA5, 0x22, 0x33, 0xB4, 0xB4, 0x33, 0x22, 0xA5, 0x11, 0x96, 0x87, 0x00);
        Ps2MemoryCard.EccTable.AsSpan(16, 16).ToArray().Should().Equal(
            0xC3, 0x44, 0x55, 0xD2, 0x66, 0xE1, 0xF0, 0x77, 0x77, 0xF0, 0xE1, 0x66, 0xD2, 0x55, 0x44, 0xC3);
        Ps2MemoryCard.EccTable.AsSpan(240, 16).ToArray().Should().Equal(
            0x00, 0x87, 0x96, 0x11, 0xA5, 0x22, 0x33, 0xB4, 0xB4, 0x33, 0x22, 0xA5, 0x11, 0x96, 0x87, 0x00);
    }

    [Fact]
    public void Every_page_carries_a_valid_ecc()
    {
        var expected = new byte[Ps2MemoryCard.RawPageSize];
        for (var page = 0; page < Ps2MemoryCard.PageCount; page += 37)
        {
            var raw = Card.Data.AsSpan(page * Ps2MemoryCard.RawPageSize, Ps2MemoryCard.RawPageSize);
            raw.CopyTo(expected);
            Ps2MemoryCard.WriteEcc(expected);
            raw.ToArray().Should().Equal(expected, $"page {page}");
        }

        // An erased (all ones) quarter has the ECC 77h 7Fh 7Fh.
        var ecc = new byte[3];
        Ps2MemoryCard.ComputeEcc(Enumerable.Repeat((byte)0xFF, 128).ToArray(), ecc);
        ecc.Should().Equal(0x77, 0x7F, 0x7F);
    }

    [Fact]
    public void The_superblock_describes_a_standard_8_mb_card()
    {
        var sb = Page(0);
        Encoding.ASCII.GetString(sb[..28]).Should().Be("Sony PS2 Memory Card Format ");
        Encoding.ASCII.GetString(sb.Slice(28, 7)).Should().Be("1.2.0.0");
        BinaryPrimitives.ReadUInt16LittleEndian(sb[0x2C..]).Should().Be(16);
        BinaryPrimitives.ReadUInt32LittleEndian(sb[0x3C..]).Should().Be(0u, "the root directory is the first allocatable cluster");
        BinaryPrimitives.ReadInt32LittleEndian(sb[0xD0..]).Should().Be(-1, "no bad blocks");
        BinaryPrimitives.ReadUInt32LittleEndian(sb[0x154..]).Should().Be(1024u);
    }

    [Fact]
    public void The_fat_chains_the_root_directory_and_frees_the_rest()
    {
        // The indirect FAT cluster (8) lists the FAT clusters 9…40.
        for (var i = 0; i < 32; i++)
        {
            BinaryPrimitives.ReadUInt32LittleEndian(Cluster(8, i * 4)).Should().Be((uint)(9 + i));
        }

        BinaryPrimitives.ReadUInt32LittleEndian(Cluster(9, 0)).Should().Be(0xFFFFFFFFu, "the root directory's one cluster");
        BinaryPrimitives.ReadUInt32LittleEndian(Cluster(9, 4)).Should().Be(0x7FFFFFFFu, "free");
        BinaryPrimitives.ReadUInt32LittleEndian(Cluster(9 + 8134 / 256, 8134 % 256 * 4)).Should().Be(0x7FFFFFFFu);
        BinaryPrimitives.ReadUInt32LittleEndian(Cluster(9 + 8135 / 256, 8135 % 256 * 4)).Should().Be(0xFFFFFFFFu, "past alloc_end");
    }

    [Fact]
    public void The_root_directory_holds_dot_and_dot_dot()
    {
        var dot = Cluster(41, 0);
        BinaryPrimitives.ReadUInt16LittleEndian(dot).Should().Be(0x8427);
        BinaryPrimitives.ReadUInt32LittleEndian(dot[4..]).Should().Be(2u);
        dot[64].Should().Be((byte)'.');
        dot[65].Should().Be(0);
        BinaryPrimitives.ReadUInt16LittleEndian(dot[14..]).Should().Be(2000, "the creation year");

        var dotDot = Cluster(41, 512);
        BinaryPrimitives.ReadUInt16LittleEndian(dotDot).Should().Be(0xA426);
        Encoding.ASCII.GetString(dotDot.Slice(64, 3)).Should().Be("..\0");
    }

    [Fact]
    public void A_wrong_sized_image_is_rejected()
    {
        var act = () => new Ps2MemoryCard(new byte[1024]);
        act.Should().Throw<ArgumentException>();
    }
}
