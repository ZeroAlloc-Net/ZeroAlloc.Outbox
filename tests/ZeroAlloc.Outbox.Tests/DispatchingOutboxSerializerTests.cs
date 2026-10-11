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
    public void Deserialize_DispatcherReturnsWrongType_Throws()
    {
        var serializer = new DispatchingOutboxSerializer(new WrongTypeDispatcher());

        var act = () => serializer.Deserialize<SampleMessage>(ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SampleMessage*")
            .WithInnerException<InvalidCastException>();
    }

    [Fact]
    public void Deserialize_GenericOverloadReturnsNull_Throws()
    {
        var serializer = new DispatchingOutboxSerializer(new GenericOnlyDispatcher(value: null));

        var act = () => serializer.Deserialize<SampleMessage>(ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SampleMessage*");
    }

    [Fact]
    public void Deserialize_ReadsThroughTheGenericOverload()
    {
        var expected = new SampleMessage("generic", 7);
        var serializer = new DispatchingOutboxSerializer(new GenericOnlyDispatcher(expected));

        serializer.Deserialize<SampleMessage>(ReadOnlyMemory<byte>.Empty).Should().BeSameAs(expected);
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

    private sealed class WrongTypeDispatcher : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) => ReadOnlyMemory<byte>.Empty;
        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) => new OtherMessage("other");
    }

    /// <summary>Overrides the generic read; the outbox must not fall back to the boxing overload.</summary>
    private sealed class GenericOnlyDispatcher(object? value) : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) => ReadOnlyMemory<byte>.Empty;

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) =>
            throw new InvalidOperationException("The outbox must read through Deserialize<T>.");

        public T? Deserialize<T>(ReadOnlyMemory<byte> data) => (T?)value;
    }
}
