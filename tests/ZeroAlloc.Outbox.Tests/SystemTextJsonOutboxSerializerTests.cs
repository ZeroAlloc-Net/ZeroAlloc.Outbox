using System.Diagnostics.CodeAnalysis;

namespace ZeroAlloc.Outbox.Tests;

[RequiresUnreferencedCode("Tests reflection-based serializer")]
[RequiresDynamicCode("Tests reflection-based serializer")]
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

    [Fact]
    public void Deserialize_NullJson_ThrowsInvalidOperationException()
    {
        var serializer = new SystemTextJsonOutboxSerializer();
        byte[] nullBytes = "null"u8.ToArray();
        var act = () => serializer.Deserialize<SampleMessage?>(nullBytes);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Deserialized null*");
    }

    private sealed record SampleMessage(int Id, string Name);
}
