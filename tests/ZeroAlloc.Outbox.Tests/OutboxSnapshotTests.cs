using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Tests;

public class OutboxSnapshotTests
{
    [Fact]
    public void OutboxMessageView_InitializesWithRequiredProperties()
    {
        var view = new OutboxMessageView
        {
            Id = Guid.NewGuid(),
            TypeName = "OrderPlaced",
            CreatedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            NextRetryAt = DateTimeOffset.UtcNow,
            PayloadPreview = "{}",
        };
        Assert.Equal("OrderPlaced", view.TypeName);
    }

    [Fact]
    public void OutboxSnapshot_GroupsMessagesByState()
    {
        var snapshot = new OutboxSnapshot(
            Pending: Array.Empty<OutboxMessageView>(),
            RetryQueue: Array.Empty<OutboxMessageView>(),
            DeadLettered: Array.Empty<OutboxMessageView>(),
            Dispatched: Array.Empty<OutboxMessageView>());
        Assert.Empty(snapshot.Pending);
    }
}
