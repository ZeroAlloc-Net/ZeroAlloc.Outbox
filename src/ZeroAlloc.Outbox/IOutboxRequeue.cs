namespace ZeroAlloc.Outbox;

/// <summary>
/// Moves a dead-lettered outbox message back to pending, so the worker dispatches it again.
/// </summary>
/// <remarks>
/// The narrow operator action behind a "retry" button, for stores that cannot offer the whole
/// <see cref="IOutboxDashboardStore"/>. A store implementing both exposes the same behaviour
/// through either.
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
