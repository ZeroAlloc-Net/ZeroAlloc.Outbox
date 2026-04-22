using System.Threading.Channels;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Per-subscriber reader handle. Dispose to unregister from the publisher and
/// complete the underlying channel.
/// </summary>
public sealed class OutboxDashboardSubscription : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    internal OutboxDashboardSubscription(ChannelReader<OutboxDashboardEvent> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<OutboxDashboardEvent> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _dispose();
    }
}
