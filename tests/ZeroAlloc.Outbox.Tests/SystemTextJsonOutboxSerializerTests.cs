namespace ZeroAlloc.Outbox.Tests;

public sealed class SystemTextJsonOutboxSerializerTests
{
    [Fact]
    public void RoundTrip_RecordType_PreservesAllProperties()
    {
        var serializer = new SystemTextJsonOutboxSerializer();
        var original = new SampleMessage(42, "hello");

        var bytes = serializer.Serialize(original);
        var restored = serializer.Deserialize<SampleMessage>(bytes);

        restored.Id.Should().Be(42);
        restored.Name.Should().Be("hello");
    }

    [Fact]
    public void Serialize_ReturnsNonEmptyBytes()
    {
        var serializer = new SystemTextJsonOutboxSerializer();
        var bytes = serializer.Serialize(new SampleMessage(1, "x"));
        bytes.Length.Should().BeGreaterThan(0);
    }

    private sealed record SampleMessage(int Id, string Name);
}
