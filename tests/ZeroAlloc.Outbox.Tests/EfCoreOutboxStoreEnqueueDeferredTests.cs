using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class EfCoreOutboxStoreEnqueueDeferredTests : IAsyncLifetime
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
    public async Task EnqueueDeferredAsync_TracksEntity_DoesNotPersistUntilSaveChanges()
    {
        await _store.EnqueueDeferredAsync("Test.Cmd", new byte[] { 1, 2, 3 }, CancellationToken.None);

        // Tracked entity exists but no row yet (no SaveChanges called).
#pragma warning disable HLQ005 // Assert.Single is xUnit's API, not LINQ Single
        Assert.Single(_db.ChangeTracker.Entries<OutboxMessageEntity>());
        var pending = await _store.FetchPendingAsync(10, CancellationToken.None);
        Assert.Empty(pending);

        // Caller's SaveChangesAsync flushes — exactly the contract Saga.Outbox needs.
        await _db.SaveChangesAsync(CancellationToken.None);
        pending = await _store.FetchPendingAsync(10, CancellationToken.None);
        Assert.Single(pending);
#pragma warning restore HLQ005
        Assert.Equal("Test.Cmd", pending[0].TypeName);
    }

    [Fact]
    public async Task EnqueueDeferredAsync_RollsBackWithSiblingWrite_OnExplicitTransactionAbort()
    {
        // The atomic-dispatch contract: deferred enqueue + a sibling write that fails
        // inside an explicit transaction → both roll back.
        await using var tx = await _db.Database.BeginTransactionAsync(CancellationToken.None);
        await _store.EnqueueDeferredAsync("Test.Cmd", new byte[] { 1, 2, 3 }, CancellationToken.None);
        await _db.SaveChangesAsync(CancellationToken.None);  // tracked changes flushed inside tx
        // Don't commit — simulating an OCC-rollback at a sibling write site.
        await tx.RollbackAsync(CancellationToken.None);

        // The clear-changetracker is needed because RollbackAsync does NOT detach tracked entities;
        // FetchPendingAsync would still see the tracked-but-uncommitted entity via DbContext's
        // local cache. Detach so the next read goes to the database.
        _db.ChangeTracker.Clear();
        var pending = await _store.FetchPendingAsync(10, CancellationToken.None);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task EnqueueDeferredAsync_DefaultInterfaceMethod_FallsBackToEnqueueAsync_WhenNotOverridden()
    {
        // Verify the default-interface-method shape: a hypothetical store that doesn't override
        // EnqueueDeferredAsync should auto-commit via the default implementation. This proves
        // the API is uniform — backends can opt in to true-defer or accept the auto-commit fallback.
        IOutboxStore fallback = new EnqueueAsyncOnlyStub();
        var stub = (EnqueueAsyncOnlyStub)fallback;
        await fallback.EnqueueDeferredAsync("Test.Cmd", new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert.True(stub.EnqueueAsyncCalled);
    }

    private sealed class EnqueueAsyncOnlyStub : IOutboxStore
    {
        public bool EnqueueAsyncCalled { get; private set; }

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
        {
            EnqueueAsyncCalled = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(System.Array.Empty<OutboxEntry>());
        public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, System.DateTimeOffset nextRetryAt, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
