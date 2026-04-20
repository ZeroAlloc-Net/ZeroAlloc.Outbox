using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Outbox.EfCore;

/// <summary>EF Core implementation of <see cref="IOutboxStore"/>.</summary>
public sealed class EfCoreOutboxStore<TContext> : IOutboxStore
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
}
