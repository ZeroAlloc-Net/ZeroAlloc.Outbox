using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The lease-based claim on <see cref="EfCoreOutboxStore{TContext}"/> against a SQLite file
/// database, with each claimer on its own <see cref="DbContext"/>, the way separate hosts would be.
/// </summary>
public sealed class EfCoreOutboxClaimTests : IAsyncLifetime
{
    private static readonly byte[] s_payload = [1, 2, 3];

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"outbox-efclaim-{Guid.NewGuid():N}.db");
    private readonly List<DashboardTestDbContext> _contexts = [];

    public async Task InitializeAsync()
    {
        var db = await ContextAsync().ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        for (var i = 0; i < _contexts.Count; i++)
        {
            var db = _contexts[i];
            var connection = db.Database.GetDbConnection();
            await db.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public async Task Concurrent_Claimers_Claim_Every_Message_Exactly_Once()
    {
        const int messages = 200;
        const int claimers = 8;
        var writer = await StoreAsync();
        for (var i = 0; i < messages; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        var claimed = new ConcurrentBag<OutboxMessageId>();
        var tasks = new Task[claimers];
        for (var c = 0; c < claimers; c++)
        {
            var store = await StoreAsync();
            var lease = new OutboxLease($"h{c}", TimeSpan.FromMinutes(1));
            tasks[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (batch.Count == 0) return;
                    foreach (var entry in batch)
                    {
                        claimed.Add(entry.Id);
                        await store.MarkSucceededAsync(entry.Id, lease, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        claimed.Should().HaveCount(messages);
        claimed.Distinct().Should().HaveCount(messages, "no message may be claimed twice");
    }

    [Fact]
    public async Task A_Lost_Lease_Is_Reclaimed_And_Cannot_Be_Renewed_By_Its_Old_Host()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(id);

        (await a.RenewLeaseAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await b.RenewLeaseAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Release_Semantics_Follow_The_Outcome()
    {
        var store = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await store.EnqueueAsync($"T{i}", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None);

        await store.MarkFailedAsync(batch[0].Id, 1, DateTimeOffset.UtcNow, lease, CancellationToken.None);
        await store.MarkSucceededAsync(batch[1].Id, lease, CancellationToken.None);
        await store.DeadLetterAsync(batch[2].Id, "boom", lease, CancellationToken.None);

        var again = await store.ClaimPendingAsync(
            10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None);
        again.Should().ContainSingle().Which.Id.Should().Be(batch[0].Id);

        var rows = await (await ContextAsync()).OutboxMessages.AsNoTracking().ToListAsync();
        rows.Where(r => r.Status != OutboxMessageStatus.Pending)
            .Should().OnlyContain(r => r.LockedBy == null && r.LockedUntil == null);
    }

    [Fact]
    public async Task Marking_Clears_A_Lease_Taken_After_The_Entity_Was_Tracked()
    {
        // The claim writes the lease with ExecuteUpdate, which bypasses the change tracker.
        // A Mark on an entity the context already tracks must still clear the lease in the
        // database, not skip the columns because the stale tracked values look unchanged.
        var store = await StoreAsync();
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var id = (await store.ClaimPendingAsync(10, lease, CancellationToken.None))[0].Id;
        await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, CancellationToken.None);

        (await store.ClaimPendingAsync(10, lease, CancellationToken.None)).Should().ContainSingle();
        await store.MarkSucceededAsync(id, lease, CancellationToken.None);

        var row = await (await ContextAsync()).OutboxMessages.AsNoTracking().SingleAsync();
        row.Status.Should().Be(OutboxMessageStatus.Succeeded);
        row.LockedBy.Should().BeNull();
        row.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task A_Late_Mark_After_A_Lost_Lease_Does_Not_Overwrite_The_New_Holders_Outcome()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None)).Should().ContainSingle();
        (await b.MarkSucceededAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();

        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();

        var row = await (await ContextAsync()).OutboxMessages.AsNoTracking().SingleAsync();
        row.Status.Should().Be(OutboxMessageStatus.Succeeded);
        row.RetryCount.Should().Be(0, "the late mark must not touch the row at all");
    }

    [Fact]
    public async Task Released_Leases_Are_Claimable_At_Once_But_Only_This_Hosts_Pending_Ones()
    {
        var a = await StoreAsync();
        var c = await StoreAsync();
        var b = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await a.EnqueueAsync($"T{i}", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMinutes(10));
        var byA = await a.ClaimPendingAsync(2, leaseA, CancellationToken.None);
        var byC = await c.ClaimPendingAsync(1, new OutboxLease("c", TimeSpan.FromMinutes(10)), CancellationToken.None);
        (await a.MarkSucceededAsync(byA[0].Id, leaseA, CancellationToken.None)).Should().BeTrue();

        (await a.ReleaseLeasesAsync([byA[0].Id, byA[1].Id, byC[0].Id], leaseA, CancellationToken.None))
            .Should().Be(1, "only this host's pending rows are released");

        var byB = await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None);
        byB.Should().ContainSingle().Which.Id.Should().Be(byA[1].Id);
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Was_Taken_Over_Cannot_Mark_And_Leaves_The_New_Lease_Alone()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(100);
        (await b.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().ContainSingle();
        var before = await (await ContextAsync()).OutboxMessages.AsNoTracking().SingleAsync();

        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.MarkSucceededAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.DeadLetterAsync(id, "late", leaseA, CancellationToken.None)).Should().BeFalse();

        var after = await (await ContextAsync()).OutboxMessages.AsNoTracking().SingleAsync();
        after.Status.Should().Be(OutboxMessageStatus.Pending);
        after.RetryCount.Should().Be(0);
        after.LockedBy.Should().Be("b", "host b's lease must survive host a's late marks");
        after.LockedUntil.Should().Be(before.LockedUntil);
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Expired_Unclaimed_Can_Still_Mark()
    {
        var store = await StoreAsync();
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await store.ClaimPendingAsync(10, lease, CancellationToken.None))[0].Id;

        await Task.Delay(100);

        (await store.MarkSucceededAsync(id, lease, CancellationToken.None)).Should().BeTrue();
        var row = await (await ContextAsync()).OutboxMessages.AsNoTracking().SingleAsync();
        row.Status.Should().Be(OutboxMessageStatus.Succeeded);
        row.LockedBy.Should().BeNull();
        row.LockedUntil.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Lease_Without_A_Host_Is_Rejected(string? hostId)
    {
        var store = await StoreAsync();
        var lease = new OutboxLease(hostId!, TimeSpan.FromMinutes(1));
        var id = OutboxMessageId.New();

        var calls = new Func<Task>[]
        {
            async () => await store.ClaimPendingAsync(10, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.RenewLeaseAsync(id, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.ReleaseLeasesAsync([id], lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.MarkSucceededAsync(id, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, CancellationToken.None).ConfigureAwait(false),
            async () => await store.DeadLetterAsync(id, "boom", lease, CancellationToken.None).ConfigureAwait(false),
        };

        foreach (var call in calls)
            await call.Should().ThrowAsync<ArgumentException>().WithParameterName("lease");
    }

    [Fact]
    public async Task A_Claim_Interrupted_After_Leasing_Releases_What_It_Leased()
    {
        // The host stops after the claim leased its rows but before it read them back. Nobody
        // would process those rows until the lease ran out, so the claim hands them back first,
        // under a token of its own since the claim's token is the one that was cancelled.
        var writer = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        using var stopping = new CancellationTokenSource();
        var interrupt = new StopBeforeReadBackInterceptor(stopping);
        var store = new EfCoreOutboxStore<DashboardTestDbContext>(await ContextAsync(interrupt));

        var claim = async () => await store.ClaimPendingAsync(
            10, new OutboxLease("a", TimeSpan.FromMinutes(10)), stopping.Token).ConfigureAwait(false);

        await claim.Should().ThrowAsync<OperationCanceledException>();
        interrupt.Fired.Should().BeTrue("the claim was interrupted between leasing and reading back");
        var rows = await (await ContextAsync()).OutboxMessages.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3).And.OnlyContain(r => r.LockedBy == null && r.LockedUntil == null);
        (await (await StoreAsync()).ClaimPendingAsync(
            10, new OutboxLease("b", TimeSpan.FromMinutes(1)), CancellationToken.None))
            .Should().HaveCount(3, "another host can claim the released rows at once");
    }

    [Fact]
    public async Task A_Leasing_Update_That_Commits_But_Throws_Is_Released()
    {
        // The leasing update commits on the database, then the client sees an error, such as a
        // dropped connection. The claim never learned how many rows it leased, and this is its
        // first attempt, but it must still release them rather than strand them for the lease.
        var writer = await StoreAsync();
        for (var i = 0; i < 3; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        var fail = new FailAfterLeasingInterceptor();
        var store = new EfCoreOutboxStore<DashboardTestDbContext>(await ContextAsync(fail));

        var claim = async () => await store.ClaimPendingAsync(
            10, new OutboxLease("a", TimeSpan.FromMinutes(10)), CancellationToken.None).ConfigureAwait(false);

        await claim.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated lost reply");
        var rows = await (await ContextAsync()).OutboxMessages.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3).And.OnlyContain(r => r.LockedBy == null && r.LockedUntil == null);
    }

    private async Task<EfCoreOutboxStore<DashboardTestDbContext>> StoreAsync()
        => new(await ContextAsync().ConfigureAwait(false));

    /// <summary>
    /// Throws after the first non-query, the claim's leasing update, has already executed, the way
    /// a reply lost after the commit would. Later commands, including the release, run normally.
    /// </summary>
    private sealed class FailAfterLeasingInterceptor : DbCommandInterceptor
    {
        private int _fired;

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                throw new InvalidOperationException("simulated lost reply");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Once the claim's leasing update has run, cancels <c>stopping</c> and fails the next query,
    /// which is the claim's read-back, the way a host stopping at that moment would.
    /// </summary>
    private sealed class StopBeforeReadBackInterceptor(CancellationTokenSource stopping) : DbCommandInterceptor
    {
        private int _leased;

        public bool Fired { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _leased, 1);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _leased) == 1 && !Fired)
            {
                Fired = true;
                await stopping.CancelAsync().ConfigureAwait(false);
                throw new OperationCanceledException(stopping.Token);
            }

            return result;
        }
    }

    /// <summary>
    /// A context on its own connection, which waits for a busy writer instead of failing,
    /// since SQLite serializes writers and eight claimers will contend.
    /// </summary>
    private async Task<DashboardTestDbContext> ContextAsync(params IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync().ConfigureAwait(false);
        var pragma = connection.CreateCommand();
        await using (pragma.ConfigureAwait(false))
        {
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var builder = new DbContextOptionsBuilder<DashboardTestDbContext>().UseSqlite(connection);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        var db = new DashboardTestDbContext(builder.Options);
        _contexts.Add(db);
        return db;
    }
}
