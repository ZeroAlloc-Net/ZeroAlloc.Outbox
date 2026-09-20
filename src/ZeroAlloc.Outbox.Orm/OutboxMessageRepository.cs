using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The SQL the outbox store needs, as ZeroAlloc.ORM partial methods. The
/// generator emits parameter binding and materialisation at compile time, so
/// nothing here reflects at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The SQL is plain ANSI throughout, so the same repository serves SQLite and
/// PostgreSQL. Only the DDL differs, and that lives in
/// <see cref="OutboxOrmMigrations"/>.
/// </para>
/// <para>
/// Status is written as its integer value rather than a name. That keeps the
/// column orderable and indexable on every provider, and means adding a state
/// does not invalidate existing rows.
/// </para>
/// </remarks>
internal sealed partial class OutboxMessageRepository(IAsyncDbConnection connection)
{
    [Command("""
        INSERT INTO OutboxMessages
            (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt)
        VALUES
            (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
        """)]
    public partial Task<int> InsertAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        CancellationToken ct);

    /// <summary>
    /// Same insert, enlisted in a transaction the caller owns.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a nullable parameter: the ORM decides at
    /// compile time whether to emit the <c>cmd.Transaction</c> assignment, so
    /// the two shapes are genuinely different emitted commands, not one command
    /// with an optional argument.
    /// </remarks>
    [Command("""
        INSERT INTO OutboxMessages
            (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt)
        VALUES
            (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
        """)]
    public partial Task<int> InsertInTransactionAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        IAsyncDbTransaction tx,
        CancellationToken ct);

    [Query("""
        SELECT Id, TypeName, Payload, RetryCount, CreatedAt
        FROM OutboxMessages
        WHERE Status = @status AND NextRetryAt <= @now
        ORDER BY CreatedAt
        LIMIT @batchSize
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> FetchPendingAsync(
        int status, DateTimeOffset now, int batchSize, CancellationToken ct);

    [Query("""
        SELECT Status, RetryCount FROM OutboxMessages WHERE Id = @id
        """)]
    public partial Task<OutboxMessageStateRow?> GetStateAsync(Guid id, CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET Status = @status, ProcessedAt = @processedAt
        WHERE Id = @id
        """)]
    public partial Task<int> MarkProcessedAsync(
        Guid id, int status, DateTimeOffset processedAt, CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET Status = @status, RetryCount = @retryCount, NextRetryAt = @nextRetryAt
        WHERE Id = @id
        """)]
    public partial Task<int> MarkForRetryAsync(
        Guid id, int status, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET Status = @status, DeadLetterError = @error, ProcessedAt = @processedAt
        WHERE Id = @id
        """)]
    public partial Task<int> DeadLetterAsync(
        Guid id, int status, string error, DateTimeOffset processedAt, CancellationToken ct);
}
