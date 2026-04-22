namespace ZeroAlloc.Outbox;

/// <summary>Raised when a new message is queued to the outbox.</summary>
public sealed record MessageQueuedEvent(Guid Id, string TypeName, DateTimeOffset QueuedAt) : OutboxDashboardEvent(Id);
