using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public class InMemoryDashboardStoreTests
{
    // Marks apply only to a message the marking host has leased, so each test claims first.
    private static readonly OutboxLease s_lease = new("test-host", TimeSpan.FromMinutes(1));

    private static async Task<OutboxMessageId> EnqueueAndClaimAsync(InMemoryOutboxStore store)
    {
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None).ConfigureAwait(false);
        return (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None).ConfigureAwait(false))[0].Id;
    }

    [Fact]
    public async Task GetSnapshotAsync_GroupsPendingMessages()
    {
        using var store = new InMemoryOutboxStore();
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
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkFailedAsync(id, retryCount: 2, nextRetryAt: DateTimeOffset.UtcNow.AddMinutes(5), s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);

        Assert.Empty(snap.Pending);
        snap.RetryQueue.Should().HaveCount(1);
        Assert.Equal(2, snap.RetryQueue[0].RetryCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_CapsDispatchedAtLimit()
    {
        using var store = new InMemoryOutboxStore();
        for (int i = 0; i < 5; i++)
        {
            await store.EnqueueAsync($"T{i}", new byte[] { (byte)i }, null, CancellationToken.None);
        }
        foreach (var e in await store.ClaimPendingAsync(10, s_lease, CancellationToken.None))
        {
            await store.MarkSucceededAsync(e.Id, s_lease, CancellationToken.None);
        }

        IOutboxDashboardStore dash = store;
        var snap = await dash.GetSnapshotAsync(dispatchedLimit: 3, CancellationToken.None);

        Assert.Equal(3, snap.Dispatched.Count);
    }

    [Fact]
    public async Task RequeueAsync_MovesDeadLetterBackToPending_ResetsRetryCount()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkFailedAsync(id, 3, DateTimeOffset.UtcNow, s_lease, CancellationToken.None);
        (await store.ClaimPendingAsync(1, s_lease, CancellationToken.None)).Should().ContainSingle();
        await store.DeadLetterAsync(id, "boom", s_lease, CancellationToken.None);

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
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.RequeueAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task ForceDispatchAsync_On_A_Leased_Message_Leaves_Its_Lease_In_Place()
    {
        // ForceDispatch makes a message due, it does not take it from its holder, which may be a
        // host that crashed mid-dispatch. Another host gets it only once that lease runs out,
        // which the lease-expiry tests cover; here the lease is long, so nothing depends on timing.
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var holder = new OutboxLease("holder", TimeSpan.FromMinutes(5));
        var id = (await store.ClaimPendingAsync(1, holder, CancellationToken.None))[0].Id;
        var lockedUntil = store.AllEntries().First(e => e.Id == id).LockedUntil;

        IOutboxDashboardStore dash = store;
        await dash.ForceDispatchAsync(id, CancellationToken.None);

        (await store.ClaimPendingAsync(1, new OutboxLease("other", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeEmpty("the holder's lease is live");
        var entry = store.AllEntries().First(e => e.Id == id);
        entry.LockedBy.Should().Be("holder");
        entry.LockedUntil.Should().Be(lockedUntil);
    }

    [Fact]
    public async Task CancelAsync_Of_An_In_Flight_Message_Makes_The_Holders_Mark_Return_False()
    {
        // Cancel does not stop a dispatch in flight. The holder finishes, and its mark finds the
        // message gone, which the worker counts as completed elsewhere.
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);

        IOutboxDashboardStore dash = store;
        await dash.CancelAsync(id, CancellationToken.None);

        (await store.MarkSucceededAsync(id, s_lease, CancellationToken.None)).Should().BeFalse();
        store.AllEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_RemovesPendingMessage()
    {
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        IOutboxDashboardStore dash = store;
        await dash.CancelAsync(id, CancellationToken.None);

        Assert.Empty(store.AllEntries());
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDispatched()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDeadLettered()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.DeadLetterAsync(id, "x", s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task ForceDispatchAsync_SetsNextRetryToNow()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow.AddHours(1), s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await dash.ForceDispatchAsync(id, CancellationToken.None);

        var entry = store.AllEntries().First(e => e.Id == id);
        Assert.True(entry.NextRetryAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ForceDispatchAsync_ThrowsForDispatched()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.ForceDispatchAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task GetThroughputAsync_YieldsBucketsWithinWindow()
    {
        using var store = new InMemoryOutboxStore();
        var id = await EnqueueAndClaimAsync(store);
        await store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = store;
        var points = new List<ThroughputPoint>();
        await foreach (var p in dash.GetThroughputAsync(TimeSpan.FromHours(1), CancellationToken.None))
            points.Add(p);

        Assert.NotEmpty(points);
        Assert.Equal(1, points.Sum(p => p.Dispatched));
    }
}
