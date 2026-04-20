using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class EndToEndTests
{
    private sealed record OrderPlaced(int OrderId, decimal Amount);

    private sealed class OrderPlacedOutboxWriter(IOutboxStore store, IOutboxSerializer serializer)
        : IOutboxWriter<OrderPlaced>
    {
#pragma warning disable IL2026, IL3050
        public ValueTask WriteAsync(OrderPlaced message, DbTransaction? transaction = null, CancellationToken ct = default)
            => store.EnqueueAsync(
                "ZeroAlloc.Outbox.Tests.EndToEndTests+OrderPlaced",
                serializer.Serialize(message),
                transaction, ct);
#pragma warning restore IL2026, IL3050
    }

    private sealed class OrderPlacedOutboxTypeDispatcher(
        IOutboxSerializer serializer,
        IOutboxDispatcher<OrderPlaced> dispatcher) : IOutboxTypeDispatcher
    {
        public string TypeName => "ZeroAlloc.Outbox.Tests.EndToEndTests+OrderPlaced";

#pragma warning disable IL2026, IL3050
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var msg = serializer.Deserialize<OrderPlaced>(payload);
            return dispatcher.DispatchAsync(msg, ct);
        }
#pragma warning restore IL2026, IL3050
    }

    private sealed class TestOrderPlacedDispatcher(List<OrderPlaced> delivered, TaskCompletionSource tcs)
        : IOutboxDispatcher<OrderPlaced>
    {
        public ValueTask DispatchAsync(OrderPlaced message, CancellationToken ct)
        {
            delivered.Add(message);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WriteAndDispatch_RoundTrip_MessageDelivered()
    {
        var delivered = new List<OrderPlaced>();
        var tcs = new TaskCompletionSource();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(30);
                });
                services.AddTransient<IOutboxWriter<OrderPlaced>, OrderPlacedOutboxWriter>();
                services.AddTransient<IOutboxDispatcher<OrderPlaced>>(
                    _ => new TestOrderPlacedDispatcher(delivered, tcs));
                services.AddSingleton<IOutboxTypeDispatcher, OrderPlacedOutboxTypeDispatcher>();
            })
            .Build();

        await host.StartAsync();

        var writer = host.Services.GetRequiredService<IOutboxWriter<OrderPlaced>>();
        await writer.WriteAsync(new OrderPlaced(42, 99.99m));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        delivered.Should().ContainSingle();
        delivered[0].OrderId.Should().Be(42);
        delivered[0].Amount.Should().Be(99.99m);

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        store.AllEntries().Should().ContainSingle()
             .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
    }
}
