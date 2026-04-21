using System.Threading.Channels;

namespace ZeroAlloc.Outbox.Tests;

public class DashboardEventPublisherTests
{
    [Fact]
    public async Task Publish_DeliversEventToSubscriber()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        using var sub = pub.Subscribe();
        var evt = new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);

        await pub.PublishAsync(evt, CancellationToken.None);

        var received = await sub.Reader.ReadAsync(CancellationToken.None);
        Assert.Equal(evt.Id, received.Id);
    }

    [Fact]
    public async Task Publish_DeliversToAllSubscribers()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        using var s1 = pub.Subscribe();
        using var s2 = pub.Subscribe();
        var evt = new MessageCancelledEvent(Guid.NewGuid());

        await pub.PublishAsync(evt, CancellationToken.None);

        Assert.Equal(evt.Id, (await s1.Reader.ReadAsync(CancellationToken.None)).Id);
        Assert.Equal(evt.Id, (await s2.Reader.ReadAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Dispose_RemovesSubscriberAndCompletesReader()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        var sub = pub.Subscribe();

        sub.Dispose();

        // After dispose, reading should complete (channel completed)
        await Assert.ThrowsAsync<ChannelClosedException>(async () =>
        {
            await sub.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        });
    }

    [Fact]
    public async Task SlowSubscriber_DoesNotBlockFastSubscriber()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        using var slow = pub.Subscribe();   // never reads
        using var fast = pub.Subscribe();

        var evt1 = new MessageCancelledEvent(Guid.NewGuid());
        var evt2 = new MessageCancelledEvent(Guid.NewGuid());

        await pub.PublishAsync(evt1, CancellationToken.None);
        await pub.PublishAsync(evt2, CancellationToken.None);

        // fast subscriber reads both without timing out
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var r1 = await fast.Reader.ReadAsync(cts.Token);
        var r2 = await fast.Reader.ReadAsync(cts.Token);
        Assert.Equal(evt1.Id, r1.Id);
        Assert.Equal(evt2.Id, r2.Id);
    }

    [Fact]
    public async Task DropOldest_WhenBufferFull_DropsOldestEventsForSlowSubscriber()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        using var sub = pub.Subscribe();

        // Default capacity is 256. Push 300 to force 44 drops.
        for (int i = 0; i < 300; i++)
            await pub.PublishAsync(new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, i), CancellationToken.None);

        // Drain what remains; should be exactly 256 (capacity), with the oldest 44 dropped.
        var drained = new List<OutboxDashboardEvent>();
        while (sub.Reader.TryRead(out var e))
            drained.Add(e);

        Assert.Equal(256, drained.Count);
        // Oldest event retained should have AttemptCount == 44 (indices 44..299 survive)
        var first = (MessageDispatchedEvent)drained[0];
        Assert.Equal(44, first.AttemptCount);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task PublishAsync_ThrowsOnCancelledToken()
    {
        var pub = new ChannelOutboxDashboardEventPublisher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var evt = new MessageCancelledEvent(Guid.NewGuid());
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await pub.PublishAsync(evt, cts.Token).ConfigureAwait(false));
    }
}
