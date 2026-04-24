using System.Runtime.CompilerServices;

namespace ZeroAlloc.Outbox.Tests;

public class OutboxDashboardStoreInterfaceTests
{
    [Fact]
    public async Task Interface_CanBeImplementedByConsumer()
    {
        IOutboxDashboardStore store = new StubDashboardStore();
        var snapshot = await store.GetSnapshotAsync(10, default);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Pending);
        Assert.Empty(snapshot.RetryQueue);
        Assert.Empty(snapshot.DeadLettered);
        Assert.Empty(snapshot.Dispatched);

        await store.RequeueAsync(OutboxMessageId.New(), default);
        await store.CancelAsync(OutboxMessageId.New(), default);
        await store.ForceDispatchAsync(OutboxMessageId.New(), default);

        var points = new List<ThroughputPoint>();
        await foreach (var point in store.GetThroughputAsync(TimeSpan.FromMinutes(5), default))
        {
            points.Add(point);
        }
        Assert.Empty(points);
    }

    private sealed class StubDashboardStore : IOutboxDashboardStore
    {
        public ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
            => ValueTask.FromResult(new OutboxSnapshot(
                Array.Empty<OutboxMessageView>(),
                Array.Empty<OutboxMessageView>(),
                Array.Empty<OutboxMessageView>(),
                Array.Empty<OutboxMessageView>()));

        public async IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask CancelAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask ForceDispatchAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
