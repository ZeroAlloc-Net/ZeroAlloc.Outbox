using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

public class DispatchingOutboxSerializerTests
{
    [Fact]
    public void RoundTrip_SerializesAndDeserializes()
    {
        var dispatcher = new StubSerializerDispatcher();
        var serializer = new DispatchingOutboxSerializer(dispatcher);

        var original = new SampleMessage("hello", 42);
        var bytes = serializer.Serialize(original);
        var result = serializer.Deserialize<SampleMessage>(bytes);

        result.Should().Be(original);
    }

    [Fact]
    public void Deserialize_DispatcherReturnsNull_Throws()
    {
        var serializer = new DispatchingOutboxSerializer(new NullReturningDispatcher());

        var act = () => serializer.Deserialize<SampleMessage>(ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SampleMessage*");
    }

    [Fact]
    public void Constructor_NullDispatcher_Throws()
    {
        var act = () => new DispatchingOutboxSerializer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- Supporting types ----

    private sealed record SampleMessage(string Name, int Value);
    private sealed record OtherMessage(string Key);

    /// <summary>Simple stub that round-trips via System.Text.Json for test purposes.</summary>
    private sealed class StubSerializerDispatcher : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type);

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) =>
            System.Text.Json.JsonSerializer.Deserialize(data.Span, type);
    }

    private sealed class NullReturningDispatcher : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) => ReadOnlyMemory<byte>.Empty;
        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) => null;
    }
}
