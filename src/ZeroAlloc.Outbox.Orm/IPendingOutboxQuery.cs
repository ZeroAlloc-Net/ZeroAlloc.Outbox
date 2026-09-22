namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The one query whose SQL cannot be written portably: fetching a bounded batch
/// of due messages.
/// </summary>
/// <remarks>
/// <para>
/// SQLite supports only <c>LIMIT</c>; SQL Server supports only
/// <c>OFFSET … FETCH NEXT</c>. PostgreSQL accepts both. The ORM composes SQL at
/// compile time from the <c>[Query]</c> attribute, so one method cannot serve
/// both spellings and the query is split into a small implementation per family
/// instead.
/// </para>
/// <para>
/// Only this query differs. The other six statements the store issues are plain
/// ANSI and live on the shared <see cref="OutboxMessageRepository"/>, so the
/// split costs one duplicated query rather than a duplicated repository.
/// </para>
/// </remarks>
internal interface IPendingOutboxQuery
{
    /// <summary>Reads up to <paramref name="batchSize"/> messages that are due.</summary>
    Task<IReadOnlyList<OutboxMessageRow>> FetchPendingAsync(
        int status, DateTimeOffset now, int batchSize, CancellationToken ct);
}
