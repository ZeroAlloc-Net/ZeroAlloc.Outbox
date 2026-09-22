using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IPendingOutboxQuery"/> using <c>LIMIT</c> — SQLite and PostgreSQL.
/// </summary>
internal sealed partial class LimitPendingOutboxQuery(IAsyncDbConnection connection) : IPendingOutboxQuery
{
    [Query("""
        SELECT Id, TypeName, Payload, RetryCount, CreatedAt
        FROM OutboxMessages
        WHERE Status = @status AND NextRetryAt <= @now
        ORDER BY CreatedAt
        LIMIT @batchSize
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> FetchPendingAsync(
        int status, DateTimeOffset now, int batchSize, CancellationToken ct);
}
