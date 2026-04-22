using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public class InMemoryDashboardStoreTests
{
    [Fact]
    public async Task GetSnapshotAsync_GroupsPendingMessages()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T1", new byte[] { 1 }, null, CancellationToken.None);
        await store.EnqueueAsync("T2", new byte[] { 2 }, null, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        var snap = await dash.GetSnapshotAsync(dispatchedLimit: 10, CancellationToken.None);

        Assert.Equal(2, snap.Pending.Count);
        Assert.Empty(snap.RetryQueue);
        Assert.Empty(snap.DeadLettered);
        Assert.Empty(snap.Dispatched);
    }

    [Fact]
    public async Task GetSnapshotAsync_MovesRetryCountGreaterThanZeroToRetryQueue()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, retryCount: 2, nextRetryAt: DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        IOutboxDashboardStore dash = store;
        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);

        Assert.Empty(snap.Pending);
        snap.RetryQueue.Should().HaveCount(1);
        Assert.Equal(2, snap.RetryQueue[0].RetryCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CapsDispatchedAtLimit()
    {
        var store = new InMemoryOutboxStore();
        for (int i = 0; i < 5; i++)
        {
            await store.EnqueueAsync($"T{i}", new byte[] { (byte)i }, null, CancellationToken.None);
        }
        foreach (var e in store.AllEntries())
        {
            await store.MarkSucceededAsync(e.Id, CancellationToken.None);
        }

        IOutboxDashboardStore dash = store;
        var snap = await dash.GetSnapshotAsync(dispatchedLimit: 3, CancellationToken.None);

        Assert.Equal(3, snap.Dispatched.Count);
    }

    [Fact]
    public async Task RequeueAsync_MovesDeadLetterBackToPending_ResetsRetryCount()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 3, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.DeadLetterAsync(id, "boom", CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await dash.RequeueAsync(id, CancellationToken.None);

        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);
        Assert.Empty(snap.DeadLettered);
        snap.Pending.Should().HaveCount(1);
        Assert.Equal(0, snap.Pending[0].RetryCount);
    }

    [Fact]
    public async Task RequeueAsync_ThrowsWhenNotDeadLettered()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.RequeueAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_RemovesPendingMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        IOutboxDashboardStore dash = store;
        await dash.CancelAsync(id, CancellationToken.None);

        Assert.Empty(store.AllEntries());
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDispatched()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDeadLettered()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.DeadLetterAsync(id, "x", CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task ForceDispatchAsync_SetsNextRetryToNow()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await dash.ForceDispatchAsync(id, CancellationToken.None);

        var entry = store.AllEntries().First(e => e.Id == id);
        Assert.True(entry.NextRetryAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ForceDispatchAsync_ThrowsForDispatched()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.ForceDispatchAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task GetThroughputAsync_YieldsBucketsWithinWindow()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        var points = new List<ThroughputPoint>();
        await foreach (var p in dash.GetThroughputAsync(TimeSpan.FromHours(1), CancellationToken.None))
            points.Add(p);

        Assert.NotEmpty(points);
        Assert.Equal(1, points.Sum(p => p.Dispatched));
    }
}
