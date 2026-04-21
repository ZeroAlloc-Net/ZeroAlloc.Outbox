namespace ZeroAlloc.Outbox;

/// <summary>Raised when a dead-lettered message is requeued from the dashboard.</summary>
public sealed record MessageRequeuedEvent(Guid Id, DateTimeOffset RequeuedAt) : OutboxDashboardEvent(Id);
