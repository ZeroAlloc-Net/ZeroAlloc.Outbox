namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// A row's persisted status and attempt counter, as read back by <see cref="IOutboxStatements.ReadStateAsync"/>.
/// </summary>
/// <remarks>
/// Named apart from the public <c>ZeroAlloc.Outbox.OutboxMessageState</c> enum, which this is not.
/// </remarks>
/// <param name="Status">The persisted <see cref="OrmOutboxMessageStatus"/> value.</param>
/// <param name="RetryCount">The number of failed attempts so far.</param>
internal sealed record OutboxRowState(int Status, int RetryCount);
