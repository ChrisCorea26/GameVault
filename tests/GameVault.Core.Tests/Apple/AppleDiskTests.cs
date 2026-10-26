using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Apple;

namespace GameVault.Core.Tests.Apple;

/// <summary>Apple II disk images (SPEC §7.11, Phase 12): nibblizing and reading back.</summary>
public sealed class AppleDiskTests
{
    private static byte[] RandomDisk(int seed)
    {
        var image = new byte[AppleDisk.DiskBytes];
        new Random(seed).NextBytes(image);
        return image;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sector_images_nibblize_and_read_back(bool proDos)
    {
        var image = RandomDisk(1);
        var disk = AppleDisk.FromSectors(image, proDos);

        for (var track = 0; track < AppleDisk.Tracks; track++)
        {
            var sectors = AppleDisk.ReadSectors(disk.TrackAt(track * 4)!, proDos);
            sectors.Should().Equal(image.AsSpan(track * AppleDisk.TrackBytes, AppleDisk.TrackBytes).ToArray(), $"track {track}");
        }
    }

    [Fact]
    public void Tracks_hold_valid_disk_bytes_with_address_and_data_marks()
    {
        var disk = AppleDisk.FromSectors(RandomDisk(2), proDosOrder: false);
        var nibbles = disk.TrackAt(0)!.ToNibbles();

        nibbles.Should().OnlyContain(b => b >= 0x96, "every disk byte has its high bit set and no more than one pair of zero bits");
        Count(nibbles, 0xD5, 0xAA, 0x96).Should().Be(16);
        Count(nibbles, 0xD5, 0xAA, 0xAD).Should().Be(16);
        disk.TrackAt(1).Should().BeSameAs(disk.TrackAt(0), "a quarter-track off still reads the track");
        disk.TrackAt(2).Should().BeNull("half-way between tracks there's nothing");
    }

    [Fact]
    public void Woz2_images_read_their_track_map_and_bits()
    {
        var bits = new byte[512];
        bits[0] = 0xD5;
        var woz = new List<byte>();
        woz.AddRange("WOZ2"u8.ToArray());
        woz.AddRange([0xFF, 0x0A, 0x0D, 0x0A, 0, 0, 0, 0]);
        Chunk(woz, "INFO", info =>
        {
            info[0] = 2;
            info[1] = 1; // 5.25"
            info[2] = 1; // write protected
        }, 60);
        Chunk(woz, "TMAP", map =>
        {
            map.Fill(0xFF);
            map[0] = map[1] = 0;
        }, 160);
        var tracksChunkStart = woz.Count + 8;
        var trackBlock = (tracksChunkStart + 1280 + 511) / 512;
        Chunk(woz, "TRKS", trks =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(trks, (ushort)trackBlock);
            BinaryPrimitives.WriteUInt16LittleEndian(trks[2..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(trks[4..], 4096);
        }, 1280 + (trackBlock * 512 - tracksChunkStart - 1280) + 512);
        var image = woz.ToArray();
        bits.CopyTo(image, trackBlock * 512);

        var disk = AppleDisk.Open(image, ".woz");

        disk.WriteProtected.Should().BeTrue();
        disk.TrackAt(0)!.BitCount.Should().Be(4096);
        disk.TrackAt(0)!.Bits[0].Should().Be(0xD5);
        disk.TrackAt(4).Should().BeNull();
    }

    private static void Chunk(List<byte> woz, string id, SpanAction fill, int size)
    {
        var data = new byte[size];
        fill(data);
        woz.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)size);
        woz.AddRange(length);
        woz.AddRange(data);
    }

    private delegate void SpanAction(Span<byte> data);

    private static int Count(byte[] nibbles, byte a, byte b, byte c)
    {
        var count = 0;
        for (var i = 0; i + 2 < nibbles.Length; i++)
        {
            if (nibbles[i] == a && nibbles[i + 1] == b && nibbles[i + 2] == c)
            {
                count++;
            }
        }

        return count;
    }
}
