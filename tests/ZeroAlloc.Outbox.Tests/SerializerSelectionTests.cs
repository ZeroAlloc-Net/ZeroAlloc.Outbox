using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Which <see cref="IOutboxSerializer"/> <c>AddOutbox()</c> resolves. Since 4.0 there is no implicit
/// reflection fallback: the serializer is the dispatcher-backed one, the explicit
/// <c>WithSystemTextJsonSerializer()</c> one, or one the application registered itself.
/// </summary>
public class SerializerSelectionTests
{
    [Fact]
    public void AddOutbox_WithSerializerDispatcher_UsesDispatchingSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

        services
            .AddOutbox()
            .WithInMemoryStore();

        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IOutboxSerializer>();

        serializer.Should().BeOfType<DispatchingOutboxSerializer>();
    }

    [Fact]
    public void AddOutbox_SerializerDispatcherRegisteredAfterAddOutbox_UsesDispatchingSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithInMemoryStore();
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutboxSerializer>().Should().BeOfType<DispatchingOutboxSerializer>();
    }

    [Fact]
    public void AddOutbox_WithoutAnySerializer_ThrowsNamingBothOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services
            .AddOutbox()
            .WithInMemoryStore();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOutboxSerializer>();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("services.AddSerializerDispatcher()")
            .And.Contain(".WithSystemTextJsonSerializer()");
    }

    [Fact]
    public void WithSystemTextJsonSerializer_UsesSystemTextJsonSerializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services
            .AddOutbox()
            .WithInMemoryStore()
            .WithSystemTextJsonSerializer();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutboxSerializer>().Should().BeOfType<SystemTextJsonOutboxSerializer>();
    }

    [Fact]
    public void WithSystemTextJsonSerializer_WinsOverSerializerDispatcher_RegisteredBefore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

        services.AddOutbox().WithSystemTextJsonSerializer();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutboxSerializer>().Should().BeOfType<SystemTextJsonOutboxSerializer>();
    }

    [Fact]
    public void WithSystemTextJsonSerializer_WinsOverSerializerDispatcher_RegisteredAfter()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithSystemTextJsonSerializer();
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutboxSerializer>().Should().BeOfType<SystemTextJsonOutboxSerializer>();
    }

    [Fact]
    public void WithSystemTextJsonSerializer_LeavesASingleSerializerRegistration()
    {
        var services = new ServiceCollection();

        services.AddOutbox().WithSystemTextJsonSerializer();

        services.Count(d => d.ServiceType == typeof(IOutboxSerializer)).Should().Be(1);
    }

    [Fact]
    public void AddOutbox_KeepsASerializerTheApplicationRegisteredFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var custom = new CustomSerializer();
        services.AddSingleton<IOutboxSerializer>(custom);
        services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());

        services.AddOutbox();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutboxSerializer>().Should().BeSameAs(custom);
    }

    [Fact]
    public async Task HostStart_Fails_WhenADispatcherNeedsASerializerAndNoneIsConfigured()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithInMemoryStore();
                services.AddTransient<IOutboxTypeDispatcher, SerializerDependentDispatcher>();
            })
            .Build();

        var act = () => host.StartAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("services.AddSerializerDispatcher()")
            .And.Contain(".WithSystemTextJsonSerializer()");
    }

    [Fact]
    public async Task HostStart_Succeeds_WithoutSerializer_WhenNoDispatcherNeedsOne()
    {
        // ZeroAlloc.Saga dispatches its commands through the outbox worker without ever using
        // IOutboxSerializer, so AddOutbox() alone must not demand a serializer.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithInMemoryStore();
                services.AddSingleton<IOutboxTypeDispatcher>(new SerializerFreeDispatcher());
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task HostStart_Succeeds_WhenADispatcherNeedsASerializerAndOneIsConfigured()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<ISerializerDispatcher>(new StubSerializerDispatcher());
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithInMemoryStore();
                services.AddTransient<IOutboxTypeDispatcher, SerializerDependentDispatcher>();
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    private sealed class SerializerDependentDispatcher(IOutboxSerializer serializer) : IOutboxTypeDispatcher
    {
        public string TypeName => "Tests.NeedsSerializer";

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            _ = serializer.Deserialize<int>(payload);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SerializerFreeDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => "Tests.NoSerializer";

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class CustomSerializer : IOutboxSerializer
    {
        public ReadOnlyMemory<byte> Serialize<T>(T value) => ReadOnlyMemory<byte>.Empty;

        public T Deserialize<T>(ReadOnlyMemory<byte> data) => default!;
    }

    private sealed class StubSerializerDispatcher : ISerializerDispatcher
    {
        public ReadOnlyMemory<byte> Serialize(object value, Type type) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type);

        public object? Deserialize(ReadOnlyMemory<byte> data, Type type) =>
            System.Text.Json.JsonSerializer.Deserialize(data.Span, type);
    }
}
