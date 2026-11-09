namespace ZeroAlloc.Outbox;

/// <summary>
/// Moves a dead-lettered outbox message back to pending, so the worker dispatches it again.
/// </summary>
/// <remarks>
/// The narrow operator action behind a "retry" button, for stores that cannot offer the whole
/// <see cref="IOutboxDashboardStore"/>. The ORM store and the EF Core store both implement it, so
/// an application that switches between them needs one call site. The EF Core store offers the
/// same operation through <see cref="IOutboxDashboardStore"/> too.
/// </remarks>
public interface IOutboxRequeue
{
    /// <summary>
    /// Moves a dead-lettered message back to pending, due now, with a fresh attempt counter and no
    /// lease.
    /// </summary>
    /// <param name="id">The identifier of the message to requeue.</param>
    /// <param name="ct">Token to cancel the asynchronous operation.</param>
    /// <exception cref="InvalidOperationException">
    /// The message does not exist, or is not dead-lettered. A message in any other state is left
    /// unchanged.
    /// </exception>
    ValueTask RequeueAsync(OutboxMessageId id, CancellationToken ct);
}
