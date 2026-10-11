using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

internal sealed class OrderShippedHandler(ChannelWriter<string> received) : INotificationHandler<OrderShipped>
{
    public ValueTask Handle(OrderShipped notification, CancellationToken ct)
        => received.WriteAsync("shipped:" + notification.OrderId, ct);
}
