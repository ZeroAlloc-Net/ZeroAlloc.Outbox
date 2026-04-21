namespace ZeroAlloc.Outbox.Tests;

public class DashboardEventPublisherTests
{
    [Fact]
    public async Task Publish_DeliversEventToSubscriber()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        var evt = new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);

        var reader = pub.Subscribe();
        await pub.PublishAsync(evt, CancellationToken.None);

        var received = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(evt.Id, ((MessageDispatchedEvent)received).Id);
    }

    [Fact]
    public async Task MultipleSubscribers_ReceiveSameEvent()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        var r1 = pub.Subscribe();
        var r2 = pub.Subscribe();
        var evt = new MessageCancelledEvent(Guid.NewGuid());

        await pub.PublishAsync(evt, CancellationToken.None);

        Assert.Equal(evt.Id, ((MessageCancelledEvent)await r1.ReadAsync(CancellationToken.None)).Id);
        Assert.Equal(evt.Id, ((MessageCancelledEvent)await r2.ReadAsync(CancellationToken.None)).Id);
    }
}
