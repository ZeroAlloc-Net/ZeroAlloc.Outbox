using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IPendingOutboxQuery"/> using <c>OFFSET … FETCH NEXT</c> — SQL
/// Server, and any other provider on the standard-SQL spelling.
/// </summary>
/// <remarks>
/// <c>OFFSET</c> is mandatory before <c>FETCH NEXT</c> in standard SQL, hence the
/// literal <c>OFFSET 0 ROWS</c>. The <c>ORDER BY</c> is required too: without it
/// the clause is not merely non-deterministic, it is a syntax error.
/// </remarks>
internal sealed partial class FetchFirstPendingOutboxQuery(IAsyncDbConnection connection) : IPendingOutboxQuery
{
    [Query("""
        SELECT Id, TypeName, Payload, RetryCount, CreatedAt
        FROM OutboxMessages
        WHERE Status = @status AND NextRetryAt <= @now
        ORDER BY CreatedAt
        OFFSET 0 ROWS FETCH NEXT @batchSize ROWS ONLY
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> FetchPendingAsync(
        int status, DateTimeOffset now, int batchSize, CancellationToken ct);
}
