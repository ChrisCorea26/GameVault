using GameVault.Core.State;

namespace GameVault.Core.Tests.State;

public class StateFormatTests
{
    [Fact]
    public void Primitives_round_trip_in_order()
    {
        var writer = new StateWriter();
        writer.WriteByte(0xAB);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteUInt16(0xBEEF);
        writer.WriteInt32(-123456);
        writer.WriteUInt32(0xDEADBEEF);
        writer.WriteInt64(long.MinValue + 7);
        writer.WriteUInt64(ulong.MaxValue - 3);
        writer.WriteBytes([1, 2, 3, 4, 5]);

        var reader = new StateReader(writer.ToArray());

        reader.ReadByte().Should().Be(0xAB);
        reader.ReadBool().Should().BeTrue();
        reader.ReadBool().Should().BeFalse();
        reader.ReadUInt16().Should().Be(0xBEEF);
        reader.ReadInt32().Should().Be(-123456);
        reader.ReadUInt32().Should().Be(0xDEADBEEF);
        reader.ReadInt64().Should().Be(long.MinValue + 7);
        reader.ReadUInt64().Should().Be(ulong.MaxValue - 3);
        var bytes = new byte[5];
        reader.ReadBytes(bytes);
        bytes.Should().Equal(1, 2, 3, 4, 5);
        reader.IsAtEnd.Should().BeTrue();
    }

    [Fact]
    public void Values_are_little_endian()
    {
        var writer = new StateWriter();
        writer.WriteUInt32(0x04030201);

        writer.WrittenSpan.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Writer_grows_beyond_initial_capacity()
    {
        var writer = new StateWriter(initialCapacity: 4);
        for (var i = 0; i < 10_000; i++)
        {
            writer.WriteInt32(i);
        }

        var reader = new StateReader(writer.ToArray());
        for (var i = 0; i < 10_000; i++)
        {
            reader.ReadInt32().Should().Be(i);
        }
    }

    [Fact]
    public void Reset_reuses_the_writer_buffer()
    {
        var writer = new StateWriter();
        writer.WriteInt64(42);
        writer.Reset();

        writer.Length.Should().Be(0);
        writer.WriteByte(7);
        writer.ToArray().Should().Equal(7);
    }

    [Fact]
    public void Chunk_round_trips_tag_and_version()
    {
        var writer = new StateWriter();
        writer.BeginChunk(StateTag.From("CPU "), version: 3);
        writer.WriteUInt16(0x1234);
        writer.EndChunk();

        var reader = new StateReader(writer.ToArray());
        var version = reader.BeginChunk(StateTag.From("CPU "));
        version.Should().Be(3);
        reader.ReadUInt16().Should().Be(0x1234);
        reader.EndChunk();
        reader.IsAtEnd.Should().BeTrue();
    }

    [Fact]
    public void Chunks_can_nest()
    {
        var writer = new StateWriter();
        writer.BeginChunk(StateTag.From("NES "), 1);
        writer.BeginChunk(StateTag.From("RAM "), 1);
        writer.WriteByte(9);
        writer.EndChunk();
        writer.WriteByte(10);
        writer.EndChunk();

        var reader = new StateReader(writer.ToArray());
        reader.BeginChunk(StateTag.From("NES "));
        reader.BeginChunk(StateTag.From("RAM "));
        reader.ReadByte().Should().Be(9);
        reader.EndChunk();
        reader.ReadByte().Should().Be(10);
        reader.EndChunk();
        reader.IsAtEnd.Should().BeTrue();
    }

    [Fact]
    public void Wrong_chunk_tag_throws()
    {
        var writer = new StateWriter();
        writer.BeginChunk(StateTag.From("PPU "), 1);
        writer.EndChunk();

        var reader = new StateReader(writer.ToArray());
        var act = () => reader.BeginChunk(StateTag.From("APU "));

        act.Should().Throw<StateFormatException>().WithMessage("*APU*PPU*");
    }

    [Fact]
    public void Ending_a_chunk_with_unread_data_throws()
    {
        var writer = new StateWriter();
        writer.BeginChunk(StateTag.From("APU "), 1);
        writer.WriteInt32(5);
        writer.EndChunk();

        var reader = new StateReader(writer.ToArray());
        reader.BeginChunk(StateTag.From("APU "));
        var act = () => reader.EndChunk();

        act.Should().Throw<StateFormatException>();
    }

    [Fact]
    public void Reading_past_the_end_throws()
    {
        var reader = new StateReader(new byte[] { 1, 2 });
        var act = () => reader.ReadUInt32();

        act.Should().Throw<StateFormatException>();
    }

    [Fact]
    public void Unbalanced_writer_chunks_throw()
    {
        var writer = new StateWriter();
        var act = () => writer.EndChunk();

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCDE")]
    public void Tags_must_be_four_ascii_characters(string tag)
    {
        var act = () => StateTag.From(tag);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StateAssert_detects_a_component_that_does_not_round_trip()
    {
        var act = () => StateAssert.RoundTrips(
            () => new ForgetfulCounter(),
            c => c.Value = 99);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void StateAssert_accepts_a_component_that_round_trips()
    {
        StateAssert.RoundTrips(
            () => new Counter(),
            c => c.Value = 99);
    }

    private sealed class Counter : IStateful
    {
        public int Value { get; set; }

        public void SaveState(StateWriter writer)
        {
            writer.BeginChunk(StateTag.From("CNT "), 1);
            writer.WriteInt32(Value);
            writer.EndChunk();
        }

        public void LoadState(StateReader reader)
        {
            reader.BeginChunk(StateTag.From("CNT "));
            Value = reader.ReadInt32();
            reader.EndChunk();
        }
    }

    private sealed class ForgetfulCounter : IStateful
    {
        public int Value { get; set; }

        public void SaveState(StateWriter writer) => writer.WriteInt32(Value);

        public void LoadState(StateReader reader) => reader.ReadInt32();
    }
}
