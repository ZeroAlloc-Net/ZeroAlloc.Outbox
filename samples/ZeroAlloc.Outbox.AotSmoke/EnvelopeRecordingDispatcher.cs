using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

internal sealed class EnvelopeRecordingDispatcher(ChannelWriter<Envelope<OrderPlaced>> received)
    : IOutboxDispatcher<Envelope<OrderPlaced>>
{
    public ValueTask DispatchAsync(Envelope<OrderPlaced> message, CancellationToken ct)
        => received.WriteAsync(message, ct);
}
