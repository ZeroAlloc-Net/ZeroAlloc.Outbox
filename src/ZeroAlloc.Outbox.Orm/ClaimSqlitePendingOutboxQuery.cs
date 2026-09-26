using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IPendingOutboxQuery"/> using <c>UPDATE … RETURNING</c> over a
/// <c>LIMIT</c> subquery — SQLite.
/// </summary>
/// <remarks>
/// SQLite has no <c>FOR UPDATE SKIP LOCKED</c> and does not need it: it
/// serializes writers, so the whole statement runs atomically and two claimers
/// cannot take the same row. PostgreSQL uses
/// <see cref="ClaimPostgresPendingOutboxQuery"/>, which adds the row locking.
/// </remarks>
internal sealed partial class ClaimSqlitePendingOutboxQuery(IAsyncDbConnection connection) : IPendingOutboxQuery
{
    [Query("""
        UPDATE OutboxMessages
        SET LockedBy = @hostId, LockedUntil = @until
        WHERE Id IN (
            SELECT Id FROM OutboxMessages
            WHERE Status = @status AND NextRetryAt <= @now
              AND (LockedUntil IS NULL OR LockedUntil < @now)
            ORDER BY CreatedAt
            LIMIT @batchSize)
          AND (LockedUntil IS NULL OR LockedUntil < @now)
        RETURNING Id, TypeName, Payload, RetryCount, CreatedAt
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
