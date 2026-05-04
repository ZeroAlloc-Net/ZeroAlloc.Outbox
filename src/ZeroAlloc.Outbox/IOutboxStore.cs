using System.Data.Common;

namespace ZeroAlloc.Outbox;

/// <summary>Durable backing store for outbox messages.</summary>
public interface IOutboxStore
{
    /// <summary>Persists a serialized message payload.</summary>
    ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct);

    /// <summary>
    /// Tracks the outbox message in the underlying store WITHOUT committing it. Pending
    /// changes are flushed by the next sibling write that calls the store's containing
    /// unit of work (e.g. for <c>Outbox.EfCore</c>: the next <c>DbContext.SaveChangesAsync</c>
    /// from another consumer of the same scoped <c>DbContext</c>).
    /// </summary>
    /// <remarks>
    /// Required by callers that need atomic commit of an outbox row alongside other domain
    /// writes — most notably <c>ZeroAlloc.Saga.Outbox</c>, where the outbox row must commit
    /// in the same transaction as the saga state save so an OCC retry rolls both back together.
    /// <para>
    /// Backends that don't support deferred persistence MAY implement this as a regular
    /// <see cref="EnqueueAsync"/> via the default body below — the resulting auto-commit
    /// is suboptimal for atomicity but keeps the API uniform. The atomicity contract holds
    /// only when paired with a backend that participates in an outer transaction (currently
    /// only <c>ZeroAlloc.Outbox.EfCore</c> overrides this with a true defer).
    /// </para>
    /// </remarks>
    ValueTask EnqueueDeferredAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
        => EnqueueAsync(typeName, payload, transaction: null, ct);

    /// <summary>Returns up to <paramref name="batchSize"/> pending messages ready for dispatch.</summary>
    ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct);

    /// <summary>Marks a message as successfully dispatched.</summary>
    ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct);

    /// <summary>Records a failed dispatch attempt and schedules the next retry.</summary>
    ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct);

    /// <summary>Moves a message to dead-letter after exhausting all retry attempts.</summary>
    ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct);
}
