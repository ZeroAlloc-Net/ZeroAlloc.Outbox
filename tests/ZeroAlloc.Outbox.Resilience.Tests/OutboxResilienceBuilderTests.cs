using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Resilience;

namespace ZeroAlloc.Outbox.Resilience.Tests;

public class OutboxResilienceBuilderTests
{
    [Fact]
    public void WithResilience_RegistersDispatcher()
    {
        var services = new ServiceCollection();
        services.AddTransient<OrderDispatcherImpl>();

#pragma warning disable IL2026, IL3050
        services.AddOutbox()
                .WithResilience<OrderCreated, IOrderDispatcher, OrderDispatcherProxy>();
#pragma warning restore IL2026, IL3050

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IOutboxDispatcher<OrderCreated>>();

        dispatcher.Should().BeOfType<OrderDispatcherProxy>();
    }

    [Fact]
    public void AddOutboxResilience_LegacyShim_StillRegisters()
    {
        var services = new ServiceCollection();
        services.AddTransient<OrderDispatcherImpl>();

#pragma warning disable CS0618 // exercise the legacy shim
        services.AddOutboxResilience<OrderCreated, IOrderDispatcher, OrderDispatcherProxy>();
#pragma warning restore CS0618

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<IOutboxDispatcher<OrderCreated>>();

        dispatcher.Should().BeOfType<OrderDispatcherProxy>();
    }

    // ---- Supporting types (mirror OutboxResilienceExtensionsTests) ----

    public sealed record OrderCreated(string OrderId);

    public interface IOrderDispatcher : IOutboxDispatcher<OrderCreated> { }

    public sealed class OrderDispatcherImpl : IOrderDispatcher
    {
        private readonly List<string> _dispatched = [];

        public IReadOnlyList<string> Dispatched => _dispatched;

        public ValueTask DispatchAsync(OrderCreated message, CancellationToken ct)
        {
            _dispatched.Add(message.OrderId);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class OrderDispatcherProxy : IOrderDispatcher
    {
        public OrderDispatcherImpl Inner { get; }

        public OrderDispatcherProxy(OrderDispatcherImpl inner) => Inner = inner;

        public ValueTask DispatchAsync(OrderCreated message, CancellationToken ct)
            => Inner.DispatchAsync(message, ct);
    }
}
