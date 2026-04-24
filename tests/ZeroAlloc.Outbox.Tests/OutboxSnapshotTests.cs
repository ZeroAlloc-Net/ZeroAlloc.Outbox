namespace ZeroAlloc.Outbox.Tests;

public class OutboxSnapshotTests
{
    [Fact]
    public void OutboxMessageView_WithIdenticalFields_AreEqual()
    {
        var created = DateTimeOffset.UtcNow;
        var id = OutboxMessageId.New();
        var a = new OutboxMessageView
        {
            Id = id,
            TypeName = "T",
            CreatedAt = created,
            RetryCount = 0,
            NextRetryAt = created,
            PayloadPreview = "{}",
        };
        var b = new OutboxMessageView
        {
            Id = id,
            TypeName = "T",
            CreatedAt = created,
            RetryCount = 0,
            NextRetryAt = created,
            PayloadPreview = "{}",
        };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void OutboxMessageView_WithDifferentFields_AreNotEqual()
    {
        var created = DateTimeOffset.UtcNow;
        var a = new OutboxMessageView
        {
            Id = OutboxMessageId.New(),
            TypeName = "T",
            CreatedAt = created,
            RetryCount = 0,
            NextRetryAt = created,
            PayloadPreview = "{}",
        };
        var b = new OutboxMessageView
        {
            Id = OutboxMessageId.New(),
            TypeName = "T",
            CreatedAt = created,
            RetryCount = 0,
            NextRetryAt = created,
            PayloadPreview = "{}",
        };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void OutboxMessageView_OptionalFields_DefaultToNull()
    {
        var view = new OutboxMessageView
        {
            Id = OutboxMessageId.New(),
            TypeName = "T",
            CreatedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            NextRetryAt = DateTimeOffset.UtcNow,
            PayloadPreview = "{}",
        };
        Assert.Null(view.DispatchedAt);
        Assert.Null(view.DeadLetterError);
    }

    [Fact]
    public void OutboxMessageView_WithExpression_ProducesModifiedCopy()
    {
        var original = new OutboxMessageView
        {
            Id = OutboxMessageId.New(),
            TypeName = "T",
            CreatedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            NextRetryAt = DateTimeOffset.UtcNow,
            PayloadPreview = "{}",
        };
        var modified = original with { RetryCount = 3 };
        Assert.Equal(0, original.RetryCount);
        Assert.Equal(3, modified.RetryCount);
        Assert.NotEqual(original, modified);
    }
}
