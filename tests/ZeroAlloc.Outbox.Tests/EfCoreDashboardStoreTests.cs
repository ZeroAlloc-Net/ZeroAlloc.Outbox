using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class EfCoreDashboardStoreTests : IAsyncLifetime
{
    // Marks apply only to a message the marking host has leased, so each test claims first.
    private static readonly OutboxLease s_lease = new("test-host", TimeSpan.FromMinutes(1));

    private SqliteConnection _conn = default!;
    private DashboardTestDbContext _db = default!;
    private EfCoreOutboxStore<DashboardTestDbContext> _store = default!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        var opts = new DbContextOptionsBuilder<DashboardTestDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new DashboardTestDbContext(opts);
        await _db.Database.EnsureCreatedAsync(CancellationToken.None).ConfigureAwait(false);
        _store = new EfCoreOutboxStore<DashboardTestDbContext>(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync().ConfigureAwait(false);
        await _conn.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<OutboxMessageId> EnqueueAndClaimAsync()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None).ConfigureAwait(false);
        return (await _store.ClaimPendingAsync(1, s_lease, CancellationToken.None).ConfigureAwait(false))[0].Id;
    }

    [Fact]
    public async Task GetSnapshotAsync_GroupsPendingMessages()
    {
        await _store.EnqueueAsync("T1", new byte[] { 1 }, null, CancellationToken.None);
        await _store.EnqueueAsync("T2", new byte[] { 2 }, null, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);

        Assert.Equal(2, snap.Pending.Count);
        Assert.Empty(snap.RetryQueue);
        Assert.Empty(snap.DeadLettered);
        Assert.Empty(snap.Dispatched);
    }

    [Fact]
    public async Task GetSnapshotAsync_RetryCountGreaterThanZero_GoesToRetryQueue()
    {
        var id = await EnqueueAndClaimAsync();
        await _store.MarkFailedAsync(id, 2, DateTimeOffset.UtcNow.AddMinutes(5), s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);

        Assert.Empty(snap.Pending);
        snap.RetryQueue.Should().HaveCount(1);
        Assert.Equal(2, snap.RetryQueue[0].RetryCount);
    }

    [Fact]
    public async Task RequeueAsync_ResetsDeadLetteredMessageToPending()
    {
        var rawId = await EnqueueAndClaimAsync();
        var id = rawId;
        await _store.DeadLetterAsync(id, "err", s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await dash.RequeueAsync(id, CancellationToken.None);

        var row = await _db.OutboxMessages.FindAsync([rawId], CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(OutboxMessageStatus.Pending, row!.Status);
        Assert.Equal(0, row.RetryCount);
        Assert.Null(row.DeadLetterError);
    }

    [Fact]
    public async Task RequeueAsync_ThrowsWhenNotDeadLettered()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.RequeueAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_DeletesPendingRow()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var rawId = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        var id = rawId;

        IOutboxDashboardStore dash = _store;
        await dash.CancelAsync(id, CancellationToken.None);

        Assert.Null(await _db.OutboxMessages.FindAsync([rawId], CancellationToken.None));
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDispatched()
    {
        var id = await EnqueueAndClaimAsync();
        await _store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDeadLettered()
    {
        var id = await EnqueueAndClaimAsync();
        await _store.DeadLetterAsync(id, "x", s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task ForceDispatchAsync_UpdatesNextRetryToNow()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var row = await _db.OutboxMessages.FirstAsync();
        row.NextRetryAt = DateTimeOffset.UtcNow.AddHours(1);
        await _db.SaveChangesAsync(CancellationToken.None);

        var before = DateTimeOffset.UtcNow;
        IOutboxDashboardStore dash = _store;
        await dash.ForceDispatchAsync(row.Id, CancellationToken.None);

        var updated = await _db.OutboxMessages.FindAsync([row.Id], CancellationToken.None);
        Assert.NotNull(updated);
        Assert.True(updated!.NextRetryAt >= before);
    }

    [Fact]
    public async Task ForceDispatchAsync_On_A_Leased_Message_Leaves_Its_Lease_In_Place()
    {
        // ForceDispatch makes a message due, it does not take it from its holder, which may be a
        // host that crashed mid-dispatch. Another host gets it only once that lease runs out,
        // which the lease-expiry tests cover; here the lease is long, so nothing depends on timing.
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var holder = new OutboxLease("holder", TimeSpan.FromMinutes(5));
        var id = (await _store.ClaimPendingAsync(1, holder, CancellationToken.None))[0].Id;
        var lockedUntil = (await _db.OutboxMessages.AsNoTracking().FirstAsync(e => e.Id == id)).LockedUntil;

        IOutboxDashboardStore dash = _store;
        await dash.ForceDispatchAsync(id, CancellationToken.None);

        (await _store.ClaimPendingAsync(1, new OutboxLease("other", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().BeEmpty("the holder's lease is live");
        var row = await _db.OutboxMessages.AsNoTracking().FirstAsync(e => e.Id == id);
        row.LockedBy.Should().Be("holder");
        row.LockedUntil.Should().Be(lockedUntil);
    }

    [Fact]
    public async Task CancelAsync_Of_An_In_Flight_Message_Makes_The_Holders_Mark_Return_False()
    {
        // Cancel does not stop a dispatch in flight. The holder finishes, and its mark finds the
        // row gone, which the worker counts as completed elsewhere.
        var id = await EnqueueAndClaimAsync();

        IOutboxDashboardStore dash = _store;
        await dash.CancelAsync(id, CancellationToken.None);

        (await _store.MarkSucceededAsync(id, s_lease, CancellationToken.None)).Should().BeFalse();
        (await _db.OutboxMessages.AsNoTracking().AnyAsync(e => e.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task GetSnapshotAsync_RespectsDispatchedLimit()
    {
        for (var i = 0; i < 5; i++)
            await _store.EnqueueAsync("T", new byte[] { (byte)i }, null, CancellationToken.None);

        foreach (var entry in await _store.ClaimPendingAsync(10, s_lease, CancellationToken.None))
            await _store.MarkSucceededAsync(entry.Id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var snap = await dash.GetSnapshotAsync(dispatchedLimit: 2, CancellationToken.None);

        Assert.Equal(2, snap.Dispatched.Count);
    }

    [Fact]
    public async Task ForceDispatchAsync_ThrowsForDispatched()
    {
        var id = await EnqueueAndClaimAsync();
        await _store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.ForceDispatchAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task GetThroughputAsync_YieldsBucketsWithinWindow()
    {
        var id = await EnqueueAndClaimAsync();
        await _store.MarkSucceededAsync(id, s_lease, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var points = new List<ThroughputPoint>();
        await foreach (var p in dash.GetThroughputAsync(TimeSpan.FromHours(1), CancellationToken.None))
            points.Add(p);

        Assert.NotEmpty(points);
        Assert.Equal(1, points.Sum(p => p.Dispatched));
    }
}
