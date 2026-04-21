namespace ZeroAlloc.Outbox;

/// <summary>
/// Fan-out publisher for dashboard events. Each <see cref="Subscribe"/> call returns
/// a disposable subscription with its own reader; events are delivered to every
/// current subscriber.
/// </summary>
public interface IOutboxDashboardEventPublisher
{
    /// <summary>Publishes an event to all current subscribers.</summary>
    /// <param name="evt">The event to deliver.</param>
    /// <param name="ct">Token to cancel the publish.</param>
    ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct);

    /// <summary>
    /// Creates a new per-subscriber channel. Dispose the returned subscription to
    /// unregister and complete the channel — required to avoid leaking subscribers.
    /// </summary>
    OutboxDashboardSubscription Subscribe();
}
