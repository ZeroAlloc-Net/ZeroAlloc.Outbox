using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

internal sealed class RecordingDispatcher(ChannelWriter<OrderPlaced> received) : IOutboxDispatcher<OrderPlaced>
{
    public ValueTask DispatchAsync(OrderPlaced message, CancellationToken ct)
        => received.WriteAsync(message, ct);
}
