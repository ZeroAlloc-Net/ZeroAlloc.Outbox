namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The state columns needed to decide whether a transition is legal, without
/// materialising the payload.
/// </summary>
internal sealed record OutboxMessageStateRow(int Status, int RetryCount);
