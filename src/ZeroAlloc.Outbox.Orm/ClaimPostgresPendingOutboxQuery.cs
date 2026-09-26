using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IPendingOutboxQuery"/> using a <c>MATERIALIZED</c> CTE that locks
/// the batch with <c>FOR UPDATE SKIP LOCKED</c>, then <c>UPDATE … FROM</c> it
/// with <c>RETURNING</c> — PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>FOR UPDATE SKIP LOCKED</c> makes concurrent claimers pass over rows
/// another claimer has locked, rather than blocking on them and then claiming
/// the same rows once that transaction commits. After taking each row lock,
/// PostgreSQL re-checks the CTE's own <c>WHERE</c> against the latest row
/// version, so the unleased predicate is enforced under the lock.
/// </para>
/// <para>
/// <c>MATERIALIZED</c> makes the CTE run exactly once, as its own step. An
/// <c>IN (subquery)</c> or an inlined CTE leaves the planner free to fold the
/// locking subquery into the outer join, or to evaluate it more than once, and
/// either way the rows it locks need not be the rows the update touches. The
/// materialized batch is locked once, and exactly those rows are leased and
/// returned.
/// </para>
/// </remarks>
internal sealed partial class ClaimPostgresPendingOutboxQuery(IAsyncDbConnection connection) : IPendingOutboxQuery
{
    [Query("""
        WITH c AS MATERIALIZED (
            SELECT Id FROM OutboxMessages
            WHERE Status = @status AND NextRetryAt <= @now
              AND (LockedUntil IS NULL OR LockedUntil < @now)
            ORDER BY CreatedAt
            LIMIT @batchSize
            FOR UPDATE SKIP LOCKED)
        UPDATE OutboxMessages t
        SET LockedBy = @hostId, LockedUntil = @until
        FROM c
        WHERE t.Id = c.Id
        RETURNING t.Id, t.TypeName, t.Payload, t.RetryCount, t.CreatedAt
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
