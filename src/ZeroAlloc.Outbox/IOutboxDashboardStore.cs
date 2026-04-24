namespace ZeroAlloc.Outbox;

/// <summary>
/// Read/write surface for the outbox dashboard. Implemented alongside <see cref="IOutboxStore"/>
/// by each store adapter (InMemory, EfCore).
/// </summary>
public interface IOutboxDashboardStore
{
    /// <summary>Returns a grouped snapshot of outbox messages for the dashboard.</summary>
    /// <param name="dispatchedLimit">Maximum number of recently-dispatched messages to include.</param>
    /// <param name="ct">Token to cancel the asynchronous snapshot read.</param>
    ValueTask<OutboxSnapshot> GetSnapshotAsync(int dispatchedLimit, CancellationToken ct);

    /// <summary>Streams throughput buckets (one per minute) within the given time window.</summary>
    /// <param name="window">How far back from now the stream should cover.</param>
    /// <param name="ct">Token to cancel the asynchronous enumeration.</param>
    IAsyncEnumerable<ThroughputPoint> GetThroughputAsync(TimeSpan window, CancellationToken ct);

    /// <summary>
    /// Moves a dead-lettered message back to pending. Throws
    /// <see cref="InvalidOperationException"/> if the message is not dead-lettered.
    /// </summary>
    /// <param name="id">The identifier of the message to requeue.</param>
    /// <param name="ct">Token to cancel the asynchronous operation.</param>
    ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct);

    /// <summary>
    /// Removes a pending or retry-queue message. Throws
    /// <see cref="InvalidOperationException"/> if the message is dispatched or dead-lettered.
    /// </summary>
    /// <param name="id">The identifier of the message to cancel.</param>
    /// <param name="ct">Token to cancel the asynchronous operation.</param>
    ValueTask CancelAsync(OutboxMessageId id, CancellationToken ct);

    /// <summary>
    /// Sets <c>NextRetryAt</c> to now so the next polling cycle picks up the message.
    /// Throws <see cref="InvalidOperationException"/> if the message is not pending.
    /// </summary>
    /// <param name="id">The identifier of the message to force-dispatch.</param>
    /// <param name="ct">Token to cancel the asynchronous operation.</param>
    ValueTask ForceDispatchAsync(OutboxMessageId id, CancellationToken ct);
}
