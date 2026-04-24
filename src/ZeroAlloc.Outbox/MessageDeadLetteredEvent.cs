namespace ZeroAlloc.Outbox;

/// <summary>Raised when a message is dead-lettered after exhausting all attempts.</summary>
public sealed record MessageDeadLetteredEvent(OutboxMessageId Id, string Error, int TotalAttempts) : OutboxDashboardEvent(Id);
