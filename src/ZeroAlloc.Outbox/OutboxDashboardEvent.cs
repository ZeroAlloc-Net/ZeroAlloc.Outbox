namespace ZeroAlloc.Outbox;

/// <summary>Base type for events pushed over the dashboard SSE stream.</summary>
public abstract record OutboxDashboardEvent(Guid Id);
