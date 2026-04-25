using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Resilience;

namespace ZeroAlloc.Outbox.Resilience.Tests;

/// <summary>
/// Validates that AddOutboxResilience wires a proxy as IOutboxDispatcher&lt;T&gt;.
/// Uses a hand-written proxy to represent what the Resilience generator would emit.
/// </summary>
public class OutboxResilienceExtensionsTests
{
    [Fact]
    public void AddOutboxResilience_RegistersProxyAsDispatcher()
    {
        var services = new ServiceCollection();
        services.AddTransient<OrderDispatcherImpl>();
        services.AddOutboxResilience<OrderCreated, IOrderDispatcher, OrderDispatcherProxy>();

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IOutboxDispatcher<OrderCreated>>();

        dispatcher.Should().BeOfType<OrderDispatcherProxy>();
    }

    [Fact]
    public async Task AddOutboxResilience_ProxyDelegatesToInnerDispatcher()
    {
        var services = new ServiceCollection();
        services.AddTransient<OrderDispatcherImpl>();
        services.AddOutboxResilience<OrderCreated, IOrderDispatcher, OrderDispatcherProxy>();

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IOutboxDispatcher<OrderCreated>>();
        var proxy = (OrderDispatcherProxy)dispatcher;

        await dispatcher.DispatchAsync(new OrderCreated("ord-1"), CancellationToken.None);

        proxy.Inner.Dispatched.Should().Equal("ord-1");
    }

    // ---- Supporting types ----

    private sealed record OrderCreated(string OrderId);

    private interface IOrderDispatcher : IOutboxDispatcher<OrderCreated> { }

    private sealed class OrderDispatcherImpl : IOrderDispatcher
    {
        public List<string> Dispatched { get; } = [];

        public ValueTask DispatchAsync(OrderCreated message, CancellationToken ct)
        {
            Dispatched.Add(message.OrderId);
            return ValueTask.CompletedTask;
        }
    }

    // Simulates what the Resilience source generator would emit for IOrderDispatcher.
    private sealed class OrderDispatcherProxy : IOrderDispatcher
    {
        public OrderDispatcherImpl Inner { get; }

        public OrderDispatcherProxy(OrderDispatcherImpl inner) => Inner = inner;

        public ValueTask DispatchAsync(OrderCreated message, CancellationToken ct)
            => Inner.DispatchAsync(message, ct);
    }
}
