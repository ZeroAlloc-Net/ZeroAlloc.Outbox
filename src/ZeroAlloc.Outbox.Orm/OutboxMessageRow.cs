namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// A pending outbox row as materialised by <see cref="OutboxMessageRepository"/>.
/// </summary>
/// <remarks>
/// Only the columns the poller needs are selected. <c>Payload</c> is the BLOB
/// carrying the serialised message; <c>Status</c> is stored as its integer
/// value so the column is ordered and indexable on every provider.
/// </remarks>
internal sealed record OutboxMessageRow(
    Guid Id,
    string TypeName,
    byte[] Payload,
    int RetryCount,
    DateTimeOffset CreatedAt);
