namespace ZeroAlloc.Outbox;

/// <summary>Raised when a dispatch attempt fails but more attempts remain.</summary>
public sealed record MessageFailedEvent(OutboxMessageId Id, string Error, int AttemptCount, DateTimeOffset NextRetryAt) : OutboxDashboardEvent(Id);
