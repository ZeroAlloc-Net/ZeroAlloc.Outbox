using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class EfCoreDashboardStoreTests : IAsyncLifetime
{
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
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        await _store.MarkFailedAsync(id, 2, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var snap = await dash.GetSnapshotAsync(10, CancellationToken.None);

        Assert.Empty(snap.Pending);
        snap.RetryQueue.Should().HaveCount(1);
        Assert.Equal(2, snap.RetryQueue[0].RetryCount);
    }

    [Fact]
    public async Task RequeueAsync_ResetsDeadLetteredMessageToPending()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var rawId = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        var id = rawId;
        await _store.DeadLetterAsync(id, "err", CancellationToken.None);

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
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        await _store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.CancelAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task CancelAsync_ThrowsForDeadLettered()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        await _store.DeadLetterAsync(id, "x", CancellationToken.None);

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
    public async Task GetSnapshotAsync_RespectsDispatchedLimit()
    {
        for (var i = 0; i < 5; i++)
            await _store.EnqueueAsync("T", new byte[] { (byte)i }, null, CancellationToken.None);

        var rawIds = await _db.OutboxMessages.Select(m => m.Id).ToArrayAsync();
        foreach (var rawId in rawIds)
            await _store.MarkSucceededAsync(rawId, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var snap = await dash.GetSnapshotAsync(dispatchedLimit: 2, CancellationToken.None);

        Assert.Equal(2, snap.Dispatched.Count);
    }

    [Fact]
    public async Task ForceDispatchAsync_ThrowsForDispatched()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        await _store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dash.ForceDispatchAsync(id, CancellationToken.None).ConfigureAwait(false));
    }

    [Fact]
    public async Task GetThroughputAsync_YieldsBucketsWithinWindow()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = await _db.OutboxMessages.Select(m => m.Id).FirstAsync();
        await _store.MarkSucceededAsync(id, CancellationToken.None);

        IOutboxDashboardStore dash = _store;
        var points = new List<ThroughputPoint>();
        await foreach (var p in dash.GetThroughputAsync(TimeSpan.FromHours(1), CancellationToken.None))
            points.Add(p);

        Assert.NotEmpty(points);
        Assert.Equal(1, points.Sum(p => p.Dispatched));
    }
}
