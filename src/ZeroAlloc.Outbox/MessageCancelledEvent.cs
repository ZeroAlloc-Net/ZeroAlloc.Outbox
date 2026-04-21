namespace ZeroAlloc.Outbox;

/// <summary>Raised when a pending/retry message is cancelled from the dashboard.</summary>
public sealed record MessageCancelledEvent(Guid Id) : OutboxDashboardEvent(Id);
