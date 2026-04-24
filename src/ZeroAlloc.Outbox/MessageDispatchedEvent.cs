namespace ZeroAlloc.Outbox;

/// <summary>Raised when a message is dispatched successfully.</summary>
public sealed record MessageDispatchedEvent(OutboxMessageId Id, DateTimeOffset DispatchedAt, int AttemptCount) : OutboxDashboardEvent(Id);
