using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Mediator;

namespace ZeroAlloc.Outbox.Mediator.Tests;

public class MediatorOutboxDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_CallsAllHandlers()
    {
        var log = new List<string>();
        var handlers = new INotificationHandler<OrderCreated>[]
        {
            new RecordingHandler(log, "A"),
            new RecordingHandler(log, "B"),
        };
        var dispatcher = new MediatorOutboxDispatcher<OrderCreated>(handlers);

        await dispatcher.DispatchAsync(new OrderCreated("ord-1"), CancellationToken.None);

        log.Should().Equal("A:ord-1", "B:ord-1");
    }

    [Fact]
    public async Task DispatchAsync_NoHandlers_Completes()
    {
        var dispatcher = new MediatorOutboxDispatcher<OrderCreated>([]);

        var act = () => dispatcher.DispatchAsync(new OrderCreated("ord-2"), CancellationToken.None).AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrows_Propagates()
    {
        var handlers = new INotificationHandler<OrderCreated>[]
        {
            new ThrowingHandler(),
        };
        var dispatcher = new MediatorOutboxDispatcher<OrderCreated>(handlers);

        var act = () => dispatcher.DispatchAsync(new OrderCreated("ord-3"), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
    }

    private sealed record OrderCreated(string OrderId) : INotification;

    private sealed class RecordingHandler(List<string> log, string tag) : INotificationHandler<OrderCreated>
    {
        public ValueTask Handle(OrderCreated notification, CancellationToken ct)
        {
            log.Add($"{tag}:{notification.OrderId}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : INotificationHandler<OrderCreated>
    {
        public ValueTask Handle(OrderCreated notification, CancellationToken ct)
            => throw new InvalidOperationException("handler failed");
    }
}
