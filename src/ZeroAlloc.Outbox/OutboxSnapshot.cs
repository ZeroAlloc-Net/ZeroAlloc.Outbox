namespace ZeroAlloc.Outbox;

/// <summary>Dashboard snapshot of the outbox grouped by message state.</summary>
public sealed record OutboxSnapshot(
    IReadOnlyList<OutboxMessageView> Pending,
    IReadOnlyList<OutboxMessageView> RetryQueue,
    IReadOnlyList<OutboxMessageView> DeadLettered,
    IReadOnlyList<OutboxMessageView> Dispatched);
