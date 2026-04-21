using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Fan-out publisher backed by one bounded <see cref="Channel{T}"/> per subscriber.
/// When a subscriber's channel is full, oldest events are dropped for that subscriber
/// only — other subscribers are unaffected.
/// </summary>
public sealed class ChannelOutboxDashboardEventPublisher : IOutboxDashboardEventPublisher
{
    private const int DefaultCapacity = 1024;

    private readonly ConcurrentDictionary<Guid, Channel<OutboxDashboardEvent>> _subscribers = new();

    /// <inheritdoc />
    public ChannelReader<OutboxDashboardEvent> Subscribe()
    {
        var ch = Channel.CreateBounded<OutboxDashboardEvent>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[Guid.NewGuid()] = ch;
        return ch.Reader;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
    {
        foreach (var ch in _subscribers.Values)
        {
            await ch.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }
}
