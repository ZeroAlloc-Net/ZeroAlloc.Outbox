using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Fan-out publisher backed by one bounded <see cref="Channel{T}"/> per subscriber.
/// When a subscriber's channel is full, oldest events are dropped for that subscriber
/// only. Subscribers MUST dispose their subscription to avoid leaking dictionary entries.
/// </summary>
public sealed class ChannelOutboxDashboardEventPublisher : IOutboxDashboardEventPublisher
{
    private const int DefaultCapacity = 256;

    private readonly ConcurrentDictionary<Guid, Channel<OutboxDashboardEvent>> _subscribers = new();

    /// <inheritdoc />
    public OutboxDashboardSubscription Subscribe()
    {
        var key = Guid.NewGuid();
        var ch = Channel.CreateBounded<OutboxDashboardEvent>(new BoundedChannelOptions(DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[key] = ch;
        return new OutboxDashboardSubscription(ch.Reader, () =>
        {
            if (_subscribers.TryRemove(key, out var removed))
                removed.Writer.TryComplete();
        });
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var ch in _subscribers.Values)
        {
            // DropOldest makes TryWrite non-blocking and never returns false for bounded channels.
            _ = ch.Writer.TryWrite(evt);
        }
        return ValueTask.CompletedTask;
    }
}
