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

    /// <summary>Returns up to <paramref name="batchSize"/> pending messages ready for dispatch.</summary>
    ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct);

    /// <summary>Marks a message as successfully dispatched.</summary>
    ValueTask MarkSucceededAsync(Guid id, CancellationToken ct);

    /// <summary>Records a failed dispatch attempt and schedules the next retry.</summary>
    ValueTask MarkFailedAsync(Guid id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct);

    /// <summary>Moves a message to dead-letter after exhausting all retry attempts.</summary>
    ValueTask DeadLetterAsync(Guid id, string error, CancellationToken ct);
}
