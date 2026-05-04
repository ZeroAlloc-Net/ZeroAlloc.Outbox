using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;

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

    public async ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Set<OutboxMessageEntity>()
            .Where(e => e.Status == OutboxMessageStatus.Pending && e.NextRetryAt <= now)
            .OrderBy(e => e.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

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

    public async ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        var fsm = new OutboxMessageFsm(ToState(entity.Status, entity.RetryCount));
        if (!fsm.TryFire(OutboxMessageTrigger.Dispatch))
            throw new InvalidOperationException(
                $"Cannot mark message {id} as succeeded in state {fsm.Current}.");
        entity.Status = OutboxMessageStatus.Succeeded;
        entity.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        var fsm = new OutboxMessageFsm(ToState(entity.Status, entity.RetryCount));
        if (!fsm.TryFire(OutboxMessageTrigger.Fail))
            throw new InvalidOperationException(
                $"Cannot mark message {id} as failed in state {fsm.Current}.");
        entity.Status = OutboxMessageStatus.Pending;
        entity.RetryCount = retryCount;
        entity.NextRetryAt = nextRetryAt;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        var fsm = new OutboxMessageFsm(ToState(entity.Status, entity.RetryCount));
        if (!fsm.TryFire(OutboxMessageTrigger.Exhaust))
            throw new InvalidOperationException(
                $"Cannot dead-letter message {id} in state {fsm.Current}.");
        entity.Status = OutboxMessageStatus.DeadLetter;
        entity.DeadLetterError = error;
        entity.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

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
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
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
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
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
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        if (entity.Status != OutboxMessageStatus.Pending)
            throw new InvalidOperationException($"Message {id} is not pending (status: {entity.Status}).");
        entity.NextRetryAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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
