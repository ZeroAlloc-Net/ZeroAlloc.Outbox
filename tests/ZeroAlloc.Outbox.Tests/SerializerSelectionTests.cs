using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

public class SerializerSelectionTests
{
    [Fact]
    public void AddOutbox_WithSerializerDispatcher_UsesDispatchingSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

#pragma warning disable IL2026, IL3050
        services
            .AddOutbox()
            .AddOutboxInMemory();
#pragma warning restore IL2026, IL3050

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IOutboxSerializer>();

        serializer.Should().BeOfType<DispatchingOutboxSerializer>();
    }

    [Fact]
    public void AddOutbox_WithoutSerializerDispatcher_UsesSystemTextJson()
    {
        var services = new ServiceCollection();
        services.AddLogging();

#pragma warning disable IL2026, IL3050
        services
            .AddOutbox()
            .AddOutboxInMemory();
#pragma warning restore IL2026, IL3050

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IOutboxSerializer>();

        serializer.Should().BeOfType<SystemTextJsonOutboxSerializer>();
    }

    private sealed class StubSerializerDispatcher : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type);

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) =>
            System.Text.Json.JsonSerializer.Deserialize(data.Span, type);
    }
}
