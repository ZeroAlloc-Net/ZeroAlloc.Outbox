using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Fan-out publisher for dashboard events. Each <see cref="Subscribe"/> call returns
/// a reader for a new bounded channel; events are delivered to every subscriber.
/// </summary>
public interface IOutboxDashboardEventPublisher
{
    /// <summary>Publishes an event to all current subscribers.</summary>
    /// <param name="evt">The event to deliver.</param>
    /// <param name="ct">Token to cancel the asynchronous publish.</param>
    ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct);

    /// <summary>Creates a new per-subscriber channel and returns its reader.</summary>
    ChannelReader<OutboxDashboardEvent> Subscribe();
}
