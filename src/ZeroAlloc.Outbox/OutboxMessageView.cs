namespace ZeroAlloc.Outbox;

/// <summary>Read-only projection of an outbox message for the dashboard.</summary>
public sealed record OutboxMessageView
{
    public required OutboxMessageId Id { get; init; }
    public required string TypeName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int RetryCount { get; init; }
    public required DateTimeOffset NextRetryAt { get; init; }
    public required string PayloadPreview { get; init; }
    public DateTimeOffset? DispatchedAt { get; init; }
    public string? DeadLetterError { get; init; }
}
