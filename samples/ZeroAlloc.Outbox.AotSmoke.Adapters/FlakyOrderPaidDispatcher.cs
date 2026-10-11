using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

/// <summary>Fails the first attempt, so the message reaches the channel only through a retry.</summary>
internal sealed class FlakyOrderPaidDispatcher(ChannelWriter<string> received) : IResilientOrderPaidDispatcher
{
    private int _attempts;

    public ValueTask DispatchAsync(OrderPaid message, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _attempts) == 1)
            throw new InvalidOperationException("simulated transient failure");
        return received.WriteAsync("paid:" + message.OrderId, ct);
    }
}
