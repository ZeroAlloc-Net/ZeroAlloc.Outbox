namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The one statement whose SQL cannot be written portably: atomically claiming a
/// bounded batch of due, unleased messages.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a single <c>UPDATE</c> that leases the rows it takes and returns
/// them, and every provider spells that differently. SQLite uses
/// <c>UPDATE … RETURNING</c> over a <c>LIMIT</c> subquery. PostgreSQL locks the
/// batch in a <c>MATERIALIZED</c> CTE with <c>FOR UPDATE SKIP LOCKED</c>, so
/// concurrent claimers pass over each other's rows instead of queueing behind
/// them, and updates from it with <c>RETURNING</c>. SQL Server has neither <c>LIMIT</c> nor
/// <c>RETURNING</c>, and uses an updatable <c>TOP</c> CTE with
/// <c>READPAST</c>/<c>UPDLOCK</c> hints and an <c>OUTPUT</c> clause. The ORM
/// composes SQL at compile time from the <c>[Query]</c> attribute, so one method
/// cannot serve every spelling, and the claim is split into a small
/// implementation per dialect instead.
/// </para>
/// <para>
/// Only this statement differs. The other statements the store issues are plain
/// ANSI and live on the shared <see cref="OutboxMessageRepository"/>, so the
/// split costs one duplicated statement rather than a duplicated repository.
/// </para>
/// <para>
/// The order of the returned rows is unspecified on every dialect, since
/// neither <c>RETURNING</c> nor <c>OUTPUT</c> guarantees one. The store sorts
/// them by <c>CreatedAt</c> itself.
/// </para>
/// </remarks>
internal interface IPendingOutboxQuery
{
    /// <summary>
    /// Leases up to <paramref name="batchSize"/> messages that are due and
    /// unleased to <paramref name="hostId"/> until <paramref name="until"/>, and
    /// returns exactly those messages.
    /// </summary>
    Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
