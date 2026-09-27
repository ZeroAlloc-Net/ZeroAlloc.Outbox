using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

internal sealed class RecordingDispatcher(TaskCompletionSource<OrderPlaced> received) : IOutboxDispatcher<OrderPlaced>
{
    public ValueTask DispatchAsync(OrderPlaced message, CancellationToken ct)
    {
        received.TrySetResult(message);
        return ValueTask.CompletedTask;
    }
}
