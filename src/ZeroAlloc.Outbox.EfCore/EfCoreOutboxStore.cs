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

    public async ValueTask MarkSucceededAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        entity.Status = OutboxMessageStatus.Succeeded;
        entity.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask MarkFailedAsync(Guid id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        entity.Status = OutboxMessageStatus.Pending;
        entity.RetryCount = retryCount;
        entity.NextRetryAt = nextRetryAt;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DeadLetterAsync(Guid id, string error, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (entity is null) return;
        entity.Status = OutboxMessageStatus.DeadLetter;
        entity.DeadLetterError = error;
        entity.ProcessedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct)
    {
        var all = await _db.Set<OutboxMessageEntity>()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var pending = new List<OutboxMessageView>();
        var retry = new List<OutboxMessageView>();
        var dead = new List<OutboxMessageView>();
        var succeeded = new List<OutboxMessageEntity>();

        foreach (ref readonly var e in CollectionsMarshal.AsSpan(all))
        {
            switch (e.Status)
            {
                case OutboxMessageStatus.Pending when e.RetryCount == 0:
                    pending.Add(ToView(e));
                    break;
                case OutboxMessageStatus.Pending:
                    retry.Add(ToView(e));
                    break;
                case OutboxMessageStatus.DeadLetter:
                    dead.Add(ToView(e));
                    break;
                case OutboxMessageStatus.Succeeded:
                    succeeded.Add(e);
                    break;
                default:
                    break;
            }
        }

        succeeded.Sort(static (a, b) =>
            (b.ProcessedAt ?? b.CreatedAt).CompareTo(a.ProcessedAt ?? a.CreatedAt));

        var take = Math.Min(succeeded.Count, dispatchedLimit);
        var dispatched = new List<OutboxMessageView>(take);
        for (var i = 0; i < take; i++)
            dispatched.Add(ToView(succeeded[i]));

        return new OutboxSnapshot(pending, retry, dead, dispatched);
    }

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

    public async ValueTask RequeueAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        if (entity.Status != OutboxMessageStatus.DeadLetter)
            throw new InvalidOperationException($"Message {id} is not dead-lettered.");
        entity.Status = OutboxMessageStatus.Pending;
        entity.RetryCount = 0;
        entity.NextRetryAt = DateTimeOffset.UtcNow;
        entity.DeadLetterError = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CancelAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        if (entity.Status != OutboxMessageStatus.Pending)
            throw new InvalidOperationException($"Message {id} cannot be cancelled (status: {entity.Status}).");
        _db.Set<OutboxMessageEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask ForceDispatchAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.Set<OutboxMessageEntity>()
            .FindAsync(new object[] { id }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Message {id} not found.");
        if (entity.Status != OutboxMessageStatus.Pending)
            throw new InvalidOperationException($"Message {id} is not pending (status: {entity.Status}).");
        entity.NextRetryAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset dto) =>
        new(dto.Year, dto.Month, dto.Day, dto.Hour, dto.Minute, 0, dto.Offset);

    private static OutboxMessageView ToView(OutboxMessageEntity e) => new()
    {
        Id = e.Id,
        TypeName = e.TypeName,
        CreatedAt = e.CreatedAt,
        RetryCount = e.RetryCount,
        NextRetryAt = e.NextRetryAt,
        PayloadPreview = DecodePreview(e.Payload),
        DispatchedAt = e.Status == OutboxMessageStatus.Succeeded ? e.ProcessedAt : null,
        DeadLetterError = e.Status == OutboxMessageStatus.DeadLetter ? e.DeadLetterError : null,
    };

    private static string DecodePreview(byte[] payload) =>
        Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 200));
}
