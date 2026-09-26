using System.Collections.Concurrent;
using AwesomeAssertions;
using Xunit;

namespace ZeroAlloc.Outbox.TestServers;

/// <summary>
/// The lease-based claim of an <see cref="IOutboxStore"/> against a real database server, with
/// each claimer on its own connection or <c>DbContext</c>, the way separate hosts would be. The
/// claim relies on the engine's row locking, so only a real server can prove it exclusive.
/// Each test runs on a database of its own.
/// </summary>
/// <remarks>
/// Every store writes the same <c>OutboxMessages</c> table with the same status values, so the
/// tests read rows back through <see cref="ReadRowAsync"/> and <see cref="CountWithStatusAsync"/>
/// in the persisted form: 0 is pending, 1 is dispatched.
/// </remarks>
public abstract class OutboxServerClaimTests : IAsyncLifetime
{
    /// <summary>The persisted status of a pending message, including one awaiting a retry.</summary>
    protected const int PendingStatus = 0;

    /// <summary>The persisted status of a dispatched message.</summary>
    protected const int SucceededStatus = 1;

    private static readonly byte[] s_payload = [1, 2, 3];

    /// <summary>Creates this test's database, with the outbox schema in it.</summary>
    public abstract Task InitializeAsync();

    /// <summary>Releases this test's connections.</summary>
    public abstract Task DisposeAsync();

    /// <summary>A store on a connection, or <c>DbContext</c>, of its own.</summary>
    protected abstract Task<IOutboxStore> StoreAsync();

    /// <summary>Reads a message's row as the database holds it now.</summary>
    protected abstract Task<StoredRow> ReadRowAsync(OutboxMessageId id);

    /// <summary>Counts the messages with the given persisted status.</summary>
    protected abstract Task<long> CountWithStatusAsync(int status);

    [Fact]
    public async Task Concurrent_Claimers_Claim_Every_Message_Exactly_Once()
    {
        const int messages = 500;
        const int claimers = 12;
        var writer = await StoreAsync();
        for (var i = 0; i < messages; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        // A claimer stops at its first empty batch. The bounded EF Core retry can still hand the
        // loser of a race nothing, so this only guarantees that the work does not all go to one
        // claimer; the EF Core retry itself is pinned down deterministically in
        // EfCoreServerClaimTests. Counting claims bounds a broken claim that hands out the same
        // rows again and again, so it fails on the duplicates rather than running into the timeout.
        var claims = new ConcurrentBag<(int Claimer, OutboxMessageId Id)>();
        var claimed = 0;
        var rejectedMarks = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[claimers];
        for (var c = 0; c < claimers; c++)
        {
            var claimer = c;
            var store = await StoreAsync();
            var lease = new OutboxLease($"h{claimer}", TimeSpan.FromMinutes(1));
            tasks[c] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                while (Volatile.Read(ref claimed) < messages)
                {
                    var batch = await store.ClaimPendingAsync(10, lease, timeout.Token).ConfigureAwait(false);
                    if (batch.Count == 0) return;

                    foreach (var entry in batch)
                    {
                        claims.Add((claimer, entry.Id));
                        Interlocked.Increment(ref claimed);
                        await Task.Delay(1, timeout.Token).ConfigureAwait(false);
                        if (!await store.MarkSucceededAsync(entry.Id, lease, timeout.Token).ConfigureAwait(false))
                            Interlocked.Increment(ref rejectedMarks);
                    }
                }
            });
        }

        start.SetResult();
        await Task.WhenAll(tasks);

        var ids = claims.Select(c => c.Id).ToList();
        ids.Should().OnlyHaveUniqueItems("no message may be claimed twice");
        ids.Should().HaveCount(messages, "every message is claimed");
        rejectedMarks.Should().Be(0, "each claimer holds the lease on what it claimed");
        var perClaimer = claims.GroupBy(c => c.Claimer).Select(g => g.Count()).ToList();
        perClaimer.Should().HaveCountGreaterThan(1, "the work must not all go to one claimer");
        (await CountWithStatusAsync(SucceededStatus)).Should().Be(messages);
    }

    [Fact]
    public async Task An_Expired_Lease_Is_Claimable_By_Another_Host()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, new OutboxLease("a", TimeSpan.FromSeconds(3)), CancellationToken.None))
            .Should().ContainSingle().Subject.Id;
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None)).Should().BeEmpty("a's lease is live");

        await Task.Delay(TimeSpan.FromSeconds(4));

        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(id);
        (await ReadRowAsync(id)).LockedBy.Should().Be("b");
    }

    [Fact]
    public async Task A_Lost_Lease_Cannot_Be_Renewed_Or_Marked_By_Its_Old_Host()
    {
        var a = await StoreAsync();
        var b = await StoreAsync();
        await a.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var leaseA = new OutboxLease("a", TimeSpan.FromSeconds(3));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await a.ClaimPendingAsync(10, leaseA, CancellationToken.None))[0].Id;

        await Task.Delay(TimeSpan.FromSeconds(4));
        (await b.ClaimPendingAsync(10, leaseB, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(id);

        (await a.RenewLeaseAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await b.RenewLeaseAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();
        var held = await ReadRowAsync(id);

        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.MarkSucceededAsync(id, leaseA, CancellationToken.None)).Should().BeFalse();
        (await a.DeadLetterAsync(id, "late", leaseA, CancellationToken.None)).Should().BeFalse();

        var afterLateMarks = await ReadRowAsync(id);
        afterLateMarks.Status.Should().Be(PendingStatus);
        afterLateMarks.RetryCount.Should().Be(0);
        afterLateMarks.LockedBy.Should().Be("b", "host b's lease must survive host a's late marks");
        afterLateMarks.LockedUntil.Should().NotBeNull().And.Be(held.LockedUntil);

        (await b.MarkSucceededAsync(id, leaseB, CancellationToken.None)).Should().BeTrue();
        (await a.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, CancellationToken.None)).Should().BeFalse();

        var final = await ReadRowAsync(id);
        final.Status.Should().Be(SucceededStatus, "Dispatched is terminal");
        final.RetryCount.Should().Be(0, "the late mark must not touch the row at all");
        final.LockedBy.Should().BeNull();
        final.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task A_Batch_Longer_Than_The_Lease_Is_Dispatched_Without_Duplicates()
    {
        // Each dispatch fits in the lease, but a batch of them does not. Renewing right before
        // each dispatch is what keeps a host from dispatching a message the other host took over.
        const int messages = 10;
        var writer = await StoreAsync();
        for (var i = 0; i < messages; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);

        var run = new BatchRenewalRun(messages);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var hosts = new Task[2];
        for (var h = 0; h < hosts.Length; h++)
        {
            var host = h;
            var store = await StoreAsync();
            hosts[h] = Task.Run(() => run.RunHostAsync(store, host, timeout.Token));
        }

        await Task.WhenAll(hosts);

        run.Marks.Should().HaveCount(messages, "every message is dispatched");
        run.Marks.Values.Should().OnlyContain(n => n == 1, "no message may be marked twice");
        run.Dispatches.Should().HaveCount(messages);
        run.Dispatches.Values.Should().OnlyContain(n => n == 1, "no message may be dispatched twice");
        run.RejectedMarks.Should().Be(0);
        run.LostRenewals.Should().BePositive("the batch outlived the lease, so later entries were skipped");
        run.MarkedByHost.Should().OnlyContain(n => n > 0, "the two hosts take over each other's expired entries");
        (await CountWithStatusAsync(SucceededStatus)).Should().Be(messages);
    }

    [Fact]
    public async Task A_Retry_Time_With_A_Non_Utc_Offset_Is_Stored_As_The_Same_Instant()
    {
        // The worker passes UTC, but MarkFailedAsync is public. A retry time in another offset
        // must mean the same instant: due when it is in the past, not due when it is in the
        // future, and accepted by a timestamptz column, which rejects a non-zero offset.
        var store = await StoreAsync();
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        await store.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var batch = await store.ClaimPendingAsync(10, lease, CancellationToken.None);
        batch.Should().HaveCount(2);
        var due = batch[0].Id;
        var notDue = batch[1].Id;

        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToOffset(TimeSpan.FromHours(14));
        var future = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(-12));
        (await store.MarkFailedAsync(due, 1, past, lease, CancellationToken.None)).Should().BeTrue();
        (await store.MarkFailedAsync(notDue, 1, future, lease, CancellationToken.None)).Should().BeTrue();

        (await store.ClaimPendingAsync(10, lease, CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(due);
    }

    /// <summary>The columns of a message row the lease tests look at.</summary>
    protected sealed record StoredRow(int Status, int RetryCount, string? LockedBy, DateTimeOffset? LockedUntil);

    /// <summary>
    /// Two hosts claiming batches of 5 under a 3-second lease, each dispatch taking 1 second, the
    /// way <c>OutboxWorkerService</c> handles a batch: renew, dispatch, mark.
    /// </summary>
    /// <remarks>
    /// A renewed lease outlives its dispatch by 2 seconds, so a slow database round trip cannot
    /// turn a renewed entry into a duplicate. The batch still outlives the lease: the claim leases
    /// all 5 entries until about 3 seconds in, and the fifth entry is not reached before about
    /// 4 seconds, a full second after its lease ran out, so its renewal is always lost.
    /// </remarks>
    private sealed class BatchRenewalRun(int messages)
    {
        private const int BatchSize = 5;
        private static readonly TimeSpan s_leaseDuration = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan s_dispatchTime = TimeSpan.FromSeconds(1);

        private int _marked;
        private int _lostRenewals;
        private int _rejectedMarks;

        public ConcurrentDictionary<OutboxMessageId, int> Dispatches { get; } = new();

        public ConcurrentDictionary<OutboxMessageId, int> Marks { get; } = new();

        public int[] MarkedByHost { get; } = new int[2];

        public int LostRenewals => Volatile.Read(ref _lostRenewals);

        public int RejectedMarks => Volatile.Read(ref _rejectedMarks);

        public async Task RunHostAsync(IOutboxStore store, int host, CancellationToken ct)
        {
            var lease = new OutboxLease($"h{host}", s_leaseDuration);
            while (Volatile.Read(ref _marked) < messages)
            {
                var batch = await store.ClaimPendingAsync(BatchSize, lease, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in batch)
                    await HandleAsync(store, host, lease, entry.Id, ct).ConfigureAwait(false);
            }
        }

        private async Task HandleAsync(
            IOutboxStore store, int host, OutboxLease lease, OutboxMessageId id, CancellationToken ct)
        {
            if (!await store.RenewLeaseAsync(id, lease, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _lostRenewals);
                return;
            }

            Dispatches.AddOrUpdate(id, 1, static (_, n) => n + 1);
            await Task.Delay(s_dispatchTime, ct).ConfigureAwait(false);

            if (!await store.MarkSucceededAsync(id, lease, ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _rejectedMarks);
                return;
            }

            Marks.AddOrUpdate(id, 1, static (_, n) => n + 1);
            Interlocked.Increment(ref MarkedByHost[host]);
            Interlocked.Increment(ref _marked);
        }
    }
}
