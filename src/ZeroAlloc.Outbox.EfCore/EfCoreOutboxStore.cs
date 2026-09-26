using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ZeroAlloc.Outbox.EfCore;

/// <summary>EF Core implementation of <see cref="IOutboxStore"/>.</summary>
public sealed class EfCoreOutboxStore<TContext> : IOutboxStore, IOutboxDashboardStore
    where TContext : DbContext
{
    private readonly TContext _db;

    public EfCoreOutboxStore(TContext db) => _db = db;

    public async ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        if (transaction is not null)
            await _db.Database.UseTransactionAsync(transaction, ct).ConfigureAwait(false);

        try
        {
            _db.Set<OutboxMessageEntity>().Add(new OutboxMessageEntity
            {
                TypeName = typeName,
                Payload = payload.ToArray(),
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (transaction is not null)
                await _db.Database.UseTransactionAsync(null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tracks the outbox row in the scoped <see cref="DbContext"/>'s <c>ChangeTracker</c>
    /// WITHOUT calling <c>SaveChangesAsync</c>. The next sibling write against the same
    /// <see cref="DbContext"/> (e.g. another EF Core consumer's <c>SaveChangesAsync</c>)
    /// commits this row in the same implicit transaction — the basis of <c>Saga.Outbox</c>'s
    /// atomic-dispatch contract.
    /// </summary>
    public ValueTask EnqueueDeferredAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        _db.Set<OutboxMessageEntity>().Add(new OutboxMessageEntity
        {
            TypeName = typeName,
            Payload = payload.ToArray(),
        });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// How many times one claim selects candidates and tries to lease them. A retry happens only
    /// when a concurrent claimer leased some of this attempt's candidates first.
    /// </summary>
    private const int MaxClaimAttempts = 3;

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due, unleased messages, since EF Core has no
    /// <c>UPDATE … RETURNING</c>: select candidates, lease them, then read back what was leased.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each attempt reads the ids of the oldest due, unleased candidates, then leases them with
    /// one conditional <c>ExecuteUpdate</c> that repeats the unleased predicate. The database
    /// re-checks that predicate on each row under the row's update lock, so a row a concurrent
    /// claimer leased in between is skipped rather than taken over. That holds on PostgreSQL and
    /// SQL Server at READ COMMITTED, and on SQLite, which serializes writers.
    /// </para>
    /// <para>
    /// Concurrent claimers select the same oldest candidates, so all but one of them can lose
    /// every row. When the update leases fewer rows than were selected, the claim selects again
    /// for the rest of the batch. The rows it already leased carry an unexpired lease, so the
    /// unleased predicate leaves them out. It stops when the batch is full, when every candidate
    /// it selected was leased, meaning no more are due, or after
    /// <see cref="MaxClaimAttempts"/> attempts. Without the retry, a host that lost the race would
    /// come away empty and sit out a whole polling interval while work was waiting.
    /// </para>
    /// <para>
    /// Finally it reads back the candidates of every attempt that now carry this host's id and
    /// this call's exact expiry, which are the rows this call claimed and no others, and returns
    /// them as one batch.
    /// </para>
    /// <para>
    /// If the claim is interrupted after it has leased rows but before it has returned them, by
    /// cancellation or a database error, nobody would process those rows until the lease ran
    /// out. So it releases them first, best effort and under a short budget of its own, since
    /// <paramref name="ct"/> may be the one that was cancelled, and then rethrows.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? until = now + lease.Duration;
        var hostId = lease.HostId;

        var candidateIds = new List<OutboxMessageId>();
        List<OutboxMessageEntity> rows;
        try
        {
            await LeaseCandidatesAsync(batchSize, now, until, hostId, candidateIds, ct).ConfigureAwait(false);
            if (candidateIds.Count == 0)
                return Array.Empty<OutboxEntry>();

            rows = await _db.Set<OutboxMessageEntity>()
                .AsNoTracking()
                .Where(e => candidateIds.Contains(e.Id) && e.LockedBy == hostId && e.LockedUntil == until)
                .OrderBy(e => e.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (candidateIds.Count > 0)
        {
            await ReleaseInterruptedClaimAsync(candidateIds, hostId, until).ConfigureAwait(false);
            throw;
        }

        var result = new List<OutboxEntry>(rows.Count);
        foreach (ref readonly var row in CollectionsMarshal.AsSpan(rows))
        {
            result.Add(new OutboxEntry
            {
                Id = row.Id,
                TypeName = row.TypeName,
                RawPayload = row.Payload,
                RetryCount = row.RetryCount,
                CreatedAt = row.CreatedAt,
            });
        }
        return result;
    }

    /// <summary>
    /// Selects and leases candidates until <paramref name="batchSize"/> rows are leased, no more
    /// are due, or <see cref="MaxClaimAttempts"/> attempts are spent. Adds every candidate
    /// selected, leased by this call or not, to <paramref name="selected"/> as it goes, so an
    /// interrupted claim still knows which rows it may have leased.
    /// </summary>
    private async Task LeaseCandidatesAsync(
        int batchSize, DateTimeOffset now, DateTimeOffset? until, string hostId,
        List<OutboxMessageId> selected, CancellationToken ct)
    {
        var leased = 0;

        for (var attempt = 0; attempt < MaxClaimAttempts && leased < batchSize; attempt++)
        {
            var before = selected.Count;
            var changed = await LeaseAttemptAsync(batchSize - leased, now, until, hostId, selected, ct)
                .ConfigureAwait(false);
            leased += changed;

            // Every candidate leased, or none selected: the batch is full, or no more are due.
            if (changed == selected.Count - before)
                break;
        }
    }

    /// <summary>How long an interrupted claim may spend handing back the rows it leased.</summary>
    private static readonly TimeSpan s_releaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Best-effort release of the rows an interrupted claim leased: those among
    /// <paramref name="candidateIds"/> that carry this claim's host and exact expiry. A failure
    /// is logged through the context's logger factory rather than thrown, because the caller
    /// rethrows the error that interrupted the claim; the leases then simply expire.
    /// </summary>
    private async Task ReleaseInterruptedClaimAsync(
        List<OutboxMessageId> candidateIds, string hostId, DateTimeOffset? until)
    {
        using var timeout = new CancellationTokenSource(s_releaseTimeout);
        try
        {
            await _db.Set<OutboxMessageEntity>()
                .Where(e => candidateIds.Contains(e.Id) && e.LockedBy == hostId && e.LockedUntil == until
                    && e.Status == OutboxMessageStatus.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.LockedBy, (string?)null)
                          .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _db.GetService<ILoggerFactory>()
                .CreateLogger<EfCoreOutboxStore<TContext>>()
                .LogWarning(ex,
                    "Could not release {Count} outbox rows leased by an interrupted claim; they expire at {LockedUntil}.",
                    candidateIds.Count, until);
        }
    }

    /// <summary>
    /// One claim attempt: select up to <paramref name="take"/> of the oldest due, unleased ids,
    /// then lease them with a conditional update that repeats the unleased predicate. Returns how
    /// many of them this attempt leased.
    /// </summary>
    /// <remarks>
    /// The ids go into <paramref name="selected"/> before the update runs. An update can commit
    /// on the server and still throw on the client, and the caller can only release rows it knows
    /// about. Listing a row this claim did not lease is harmless: the release matches only rows
    /// carrying this claim's host and exact expiry.
    /// </remarks>
    private async Task<int> LeaseAttemptAsync(
        int take, DateTimeOffset now, DateTimeOffset? until, string hostId,
        List<OutboxMessageId> selected, CancellationToken ct)
    {
        var set = _db.Set<OutboxMessageEntity>();
        var candidateIds = await set
            .AsNoTracking()
            .Where(e => e.Status == OutboxMessageStatus.Pending && e.NextRetryAt <= now
                && (e.LockedUntil == null || e.LockedUntil < now))
            .OrderBy(e => e.CreatedAt)
            .Take(take)
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
            return 0;

        selected.AddRange(candidateIds);
        return await set
            .Where(e => candidateIds.Contains(e.Id) && e.Status == OutboxMessageStatus.Pending
                && (e.LockedUntil == null || e.LockedUntil < now))
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LockedBy, hostId).SetProperty(e => e.LockedUntil, until),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extends this host's unexpired lease on a pending message, in one conditional
    /// <c>ExecuteUpdate</c>.
    /// </summary>
    public async ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? until = now + lease.Duration;
        var hostId = lease.HostId;

        var changed = await _db.Set<OutboxMessageEntity>()
            .Where(e => e.Id == id && e.LockedBy == hostId && e.LockedUntil >= now
                && e.Status == OutboxMessageStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntil, until), ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    /// <summary>
    /// Gives up this host's leases on the given pending messages, in one conditional
    /// <c>ExecuteUpdate</c>.
    /// </summary>
    public async ValueTask<int> ReleaseLeasesAsync(
        IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfNoHost(lease);
        if (ids.Count == 0) return 0;

        var idList = ids.ToList();
        var hostId = lease.HostId;
        return await _db.Set<OutboxMessageEntity>()
            .Where(e => idList.Contains(e.Id) && e.LockedBy == hostId && e.Status == OutboxMessageStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.LockedBy, (string?)null)
                      .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
    }

    // Each mark is one conditional ExecuteUpdate guarded by the row still being pending, which
    // is exactly the set of OutboxMessageFsm states from which Dispatch, Fail and Exhaust are
    // legal, and by the row still being leased by the marking host. The database decides the
    // transition: only the current lease holder can move the row, so of two concurrent marks from
    // different hosts exactly one wins, and a terminal state is never overwritten. The lease
    // expiry is deliberately not checked, so a host whose lease expired without being taken over
    // can still record its outcome. The update also bypasses any instance the context tracks,
    // which may be stale because the claim writes through ExecuteUpdate too.

    public async ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        DateTimeOffset? processedAt = DateTimeOffset.UtcNow;
        var changed = await LeasedPendingRow(id, lease.HostId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, OutboxMessageStatus.Succeeded)
                      .SetProperty(e => e.ProcessedAt, processedAt)
                      .SetProperty(e => e.LockedBy, (string?)null)
                      .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public async ValueTask<bool> MarkFailedAsync(
        OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        // Stays Pending: retry versus first attempt is carried by RetryCount. The retry time is
        // stored in UTC like every other timestamp: a PostgreSQL timestamptz rejects a non-zero
        // offset, and due-time comparisons must never mix offsets.
        var retryAt = nextRetryAt.ToUniversalTime();
        var changed = await LeasedPendingRow(id, lease.HostId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.RetryCount, retryCount)
                      .SetProperty(e => e.NextRetryAt, retryAt)
                      .SetProperty(e => e.LockedBy, (string?)null)
                      .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public async ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        DateTimeOffset? processedAt = DateTimeOffset.UtcNow;
        var changed = await LeasedPendingRow(id, lease.HostId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Status, OutboxMessageStatus.DeadLetter)
                      .SetProperty(e => e.DeadLetterError, error)
                      .SetProperty(e => e.ProcessedAt, processedAt)
                      .SetProperty(e => e.LockedBy, (string?)null)
                      .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    private IQueryable<OutboxMessageEntity> LeasedPendingRow(OutboxMessageId id, string hostId) =>
        _db.Set<OutboxMessageEntity>()
            .Where(e => e.Id == id && e.Status == OutboxMessageStatus.Pending && e.LockedBy == hostId);

    /// <summary>
    /// Returns a snapshot partitioned into pending / retry / dead-letter / dispatched buckets.
    /// </summary>
    /// <remarks>
    /// Each bucket is a separate server-side query with a <c>WHERE</c> filter so we never
    /// materialise the full table. The dispatched bucket's ordering by <see cref="OutboxMessageEntity.ProcessedAt"/>
    /// is applied client-side because SQLite cannot translate <c>DateTimeOffset</c> comparisons
    /// in <c>ORDER BY</c>; providers that support it (SQL Server, PostgreSQL) would benefit from
    /// adding an <c>ORDER BY</c> + <c>TOP</c>/<c>LIMIT</c> server-side. Benchmark before using
    /// with large dispatched tables on SQLite.
    /// </remarks>
    public async ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
    {
        var set = _db.Set<OutboxMessageEntity>().AsNoTracking();

        var pending = await QueryViewsAsync(
            set.Where(m => m.Status == OutboxMessageStatus.Pending && m.RetryCount == 0), ct)
            .ConfigureAwait(false);

        var retry = await QueryViewsAsync(
            set.Where(m => m.Status == OutboxMessageStatus.Pending && m.RetryCount > 0), ct)
            .ConfigureAwait(false);

        var dead = await QueryViewsAsync(
            set.Where(m => m.Status == OutboxMessageStatus.DeadLetter), ct)
            .ConfigureAwait(false);

        // SQLite can't ORDER BY DateTimeOffset, so fetch all Succeeded then sort + take in-memory.
        var dispatchedRows = await set
            .Where(m => m.Status == OutboxMessageStatus.Succeeded)
            .Select(m => new RawProjection
            {
                Id = m.Id,
                TypeName = m.TypeName,
                CreatedAt = m.CreatedAt,
                RetryCount = m.RetryCount,
                NextRetryAt = m.NextRetryAt,
                Payload = m.Payload,
                Status = m.Status,
                ProcessedAt = m.ProcessedAt,
                DeadLetterError = m.DeadLetterError,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dispatched = TakeLatestDispatched(dispatchedRows, dispatchedLimit);
        return new OutboxSnapshot(pending, retry, dead, dispatched);
    }

    private static List<OutboxMessageView> TakeLatestDispatched(List<RawProjection> rows, int limit)
    {
        rows.Sort(static (a, b) =>
            (b.ProcessedAt ?? b.CreatedAt).CompareTo(a.ProcessedAt ?? a.CreatedAt));

        var take = Math.Min(rows.Count, limit);
        if (rows.Count > take)
            rows.RemoveRange(take, rows.Count - take);

        var dispatched = new List<OutboxMessageView>(take);
        foreach (ref readonly var r in CollectionsMarshal.AsSpan(rows))
            dispatched.Add(ToView(in r));
        return dispatched;
    }

    private static async Task<List<OutboxMessageView>> QueryViewsAsync(
        IQueryable<OutboxMessageEntity> query, CancellationToken ct)
    {
        var rows = await query.Select(m => new RawProjection
        {
            Id = m.Id,
            TypeName = m.TypeName,
            CreatedAt = m.CreatedAt,
            RetryCount = m.RetryCount,
            NextRetryAt = m.NextRetryAt,
            Payload = m.Payload,
            Status = m.Status,
            ProcessedAt = m.ProcessedAt,
            DeadLetterError = m.DeadLetterError,
        }).ToListAsync(ct).ConfigureAwait(false);

        var result = new List<OutboxMessageView>(rows.Count);
        foreach (ref readonly var r in CollectionsMarshal.AsSpan(rows))
            result.Add(ToView(r));
        return result;
    }

    /// <summary>
    /// Streams per-minute dispatched/failed throughput for messages whose
    /// <see cref="OutboxMessageEntity.ProcessedAt"/> falls inside <paramref name="window"/>.
    /// </summary>
    /// <remarks>
    /// The time-window cutoff is filtered client-side because SQLite cannot translate
    /// <c>DateTimeOffset</c> comparisons. Server-side providers (SQL Server, PostgreSQL)
    /// should re-introduce the cutoff into the <c>WHERE</c> clause and benchmark. At scale
    /// on SQLite, this method reads every row where <see cref="OutboxMessageEntity.ProcessedAt"/>
    /// is non-null; consider pruning Succeeded/DeadLetter rows periodically.
    /// </remarks>
    public async IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var rows = await _db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .Where(m => m.ProcessedAt != null)
            .Select(m => new { m.ProcessedAt, m.Status })
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var buckets = new Dictionary<DateTimeOffset, (int Dispatched, int Failed)>();
        foreach (var row in rows)
        {
            var processed = row.ProcessedAt!.Value;
            if (processed < cutoff) continue;
            var key = TruncateToMinute(processed);
            buckets.TryGetValue(key, out var counts);
            if (row.Status == OutboxMessageStatus.Succeeded)
                counts.Dispatched++;
            else if (row.Status == OutboxMessageStatus.DeadLetter)
                counts.Failed++;
            buckets[key] = counts;
        }

        var ordered = buckets.ToArray();
        Array.Sort(ordered, static (a, b) => a.Key.CompareTo(b.Key));

        foreach (var kv in ordered)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ThroughputPoint(kv.Key, kv.Value.Dispatched, kv.Value.Failed);
        }
    }

    public async ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct)
    {
        var entity = await FindCurrentAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        var fsm = new OutboxMessageFsm(ToState(entity.Status, entity.RetryCount));
        if (!fsm.TryFire(OutboxMessageTrigger.Requeue))
            throw new InvalidOperationException(
                $"Message {id} cannot be requeued in state {fsm.Current}.");
        entity.Status = OutboxMessageStatus.Pending;
        entity.RetryCount = 0;
        entity.NextRetryAt = DateTimeOffset.UtcNow;
        entity.DeadLetterError = null;
        entity.ProcessedAt = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CancelAsync(OutboxMessageId id, CancellationToken ct)
    {
        var entity = await FindCurrentAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        var fsm = new OutboxMessageFsm(ToState(entity.Status, entity.RetryCount));
        if (!fsm.TryFire(OutboxMessageTrigger.Cancel))
            throw new InvalidOperationException(
                $"Message {id} cannot be cancelled in state {fsm.Current}.");
        _db.Set<OutboxMessageEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask ForceDispatchAsync(OutboxMessageId id, CancellationToken ct)
    {
        var entity = await FindCurrentAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        if (entity.Status != OutboxMessageStatus.Pending)
            throw new InvalidOperationException($"Message {id} is not pending (status: {entity.Status}).");
        entity.NextRetryAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the message as the database holds it now, for the dashboard operations.
    /// </summary>
    /// <remarks>
    /// <c>FindAsync</c> returns an already-tracked instance without querying. The claim, the
    /// renewal and every mark write through <c>ExecuteUpdate</c>, which bypasses the change
    /// tracker, and other hosts change rows too, so a tracked instance can be stale. Deciding a
    /// dashboard transition on stale state would, for example, requeue a message that is no
    /// longer dead-lettered. So a tracked instance is reloaded first. An instance tracked as
    /// Added has never been saved, and is left as is.
    /// </remarks>
    private async ValueTask<OutboxMessageEntity?> FindCurrentAsync(OutboxMessageId id, CancellationToken ct)
    {
        var tracked = _db.Set<OutboxMessageEntity>().Local.FindEntry(id);
        if (tracked is null)
        {
            return await _db.Set<OutboxMessageEntity>()
                .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        }

        if (tracked.State != EntityState.Added)
        {
            await tracked.ReloadAsync(ct).ConfigureAwait(false);
            if (tracked.State == EntityState.Detached) return null;
        }

        return tracked.Entity;
    }

    // A default OutboxLease carries no host, and a null, empty or blank LockedBy would read as
    // unleased, and a blank one identifies no host.
    private static void ThrowIfNoHost(OutboxLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.HostId))
            throw new ArgumentException("The lease has no HostId.", nameof(lease));
    }

    private static OutboxMessageState ToState(OutboxMessageStatus status, int retryCount) => status switch
    {
        OutboxMessageStatus.Succeeded => OutboxMessageState.Dispatched,
        OutboxMessageStatus.DeadLetter => OutboxMessageState.DeadLetter,
        _ => retryCount == 0 ? OutboxMessageState.Pending : OutboxMessageState.Retry,
    };

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dto) =>
        new(dto.Year, dto.Month, dto.Day, dto.Hour, dto.Minute, 0, dto.Offset);

    private static OutboxMessageView ToView(in RawProjection r) => new()
    {
        Id = r.Id,
        TypeName = r.TypeName,
        CreatedAt = r.CreatedAt,
        RetryCount = r.RetryCount,
        NextRetryAt = r.NextRetryAt,
        PayloadPreview = DecodePreview(r.Payload),
        DispatchedAt = r.Status == OutboxMessageStatus.Succeeded ? r.ProcessedAt : null,
        DeadLetterError = r.Status == OutboxMessageStatus.DeadLetter ? r.DeadLetterError : null,
    };

    private const int PayloadPreviewBytes = 200;

    private static string DecodePreview(byte[] payload) =>
        Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, PayloadPreviewBytes));

    private sealed class RawProjection
    {
        public OutboxMessageId Id { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public int RetryCount { get; set; }
        public DateTimeOffset NextRetryAt { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public OutboxMessageStatus Status { get; set; }
        public DateTimeOffset? ProcessedAt { get; set; }
        public string? DeadLetterError { get; set; }
    }
}
