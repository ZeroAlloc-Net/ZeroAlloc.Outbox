using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IPendingOutboxQuery"/> using an updatable <c>TOP</c> CTE with an
/// <c>OUTPUT</c> clause — SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server has neither <c>LIMIT</c> nor <c>RETURNING</c>, and <c>UPDATE TOP</c>
/// takes no <c>ORDER BY</c>. So the oldest due rows are picked in a CTE, which
/// may carry <c>TOP</c> with <c>ORDER BY</c>, and the <c>UPDATE</c> runs through
/// it, returning the claimed rows with <c>OUTPUT inserted.*</c> columns.
/// </para>
/// <para>
/// <c>UPDLOCK</c> holds the selected rows until the statement commits, and
/// <c>READPAST</c> lets a concurrent claimer skip rows already locked rather
/// than wait for them and then claim the same rows. <c>ROWLOCK</c> keeps the
/// locks at row granularity so <c>READPAST</c> has rows to skip.
/// </para>
/// </remarks>
internal sealed partial class ClaimSqlServerPendingOutboxQuery(IAsyncDbConnection connection) : IPendingOutboxQuery
{
    [Query("""
        WITH claim AS (
            SELECT TOP (@batchSize) Id, TypeName, Payload, RetryCount, CreatedAt, LockedBy, LockedUntil
            FROM OutboxMessages WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE Status = @status AND NextRetryAt <= @now
              AND (LockedUntil IS NULL OR LockedUntil < @now)
            ORDER BY CreatedAt)
        UPDATE claim SET LockedBy = @hostId, LockedUntil = @until
        OUTPUT inserted.Id, inserted.TypeName, inserted.Payload, inserted.RetryCount, inserted.CreatedAt
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
