using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IOutboxStatements"/> for SQLite, as ZeroAlloc.ORM partial methods. The generator
/// emits parameter binding and materialisation at compile time, so nothing here reflects at runtime.
/// </summary>
/// <remarks>
/// <para>
/// <c>{table}</c> is the primary-constructor parameter <c>table</c>, spliced in verbatim, so quoting
/// is the caller's choice: <c>OutboxMessages</c> and <c>"Outbox Messages"</c> both work. It is
/// trusted configuration, never user input. <c>{col:X}</c> is the bare column name, or quoted in the
/// dialect's style when <c>quoteColumns</c> is true, for a table whose columns were created quoted
/// and mixed case. Parameter names are fixed.
/// </para>
/// <para>
/// The claim is <c>UPDATE … RETURNING</c> over a <c>LIMIT</c> subquery. SQLite has no
/// <c>FOR UPDATE SKIP LOCKED</c> and does not need it: it serializes writers, so the whole statement
/// runs atomically and two claimers cannot take the same row.
/// </para>
/// </remarks>
[Dialect(SqlDialect.Sqlite)]
internal sealed partial class SqliteOutboxRepository(IAsyncDbConnection connection, string table, bool quoteColumns)
    : IOutboxStatements
{
    /// <inheritdoc />
    [Command("""
        INSERT INTO {table}
            ({col:Id}, {col:TypeName}, {col:Payload}, {col:Status}, {col:RetryCount}, {col:NextRetryAt}, {col:CreatedAt})
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

    /// <inheritdoc />
    [Command("""
        INSERT INTO {table}
            ({col:Id}, {col:TypeName}, {col:Payload}, {col:Status}, {col:RetryCount}, {col:NextRetryAt}, {col:CreatedAt})
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

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:Status} = @status, {col:ProcessedAt} = @processedAt,
            {col:LockedBy} = NULL, {col:LockedUntil} = NULL
        WHERE {col:Id} = @id AND {col:Status} = @pending AND {col:LockedBy} = @hostId
        """)]
    public partial Task<int> MarkProcessedAsync(
        Guid id, int status, DateTimeOffset processedAt, int pending, string hostId, CancellationToken ct);

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:RetryCount} = @retryCount, {col:NextRetryAt} = @nextRetryAt,
            {col:LockedBy} = NULL, {col:LockedUntil} = NULL
        WHERE {col:Id} = @id AND {col:Status} = @pending AND {col:LockedBy} = @hostId
        """)]
    public partial Task<int> MarkForRetryAsync(
        Guid id, int retryCount, DateTimeOffset nextRetryAt, int pending, string hostId, CancellationToken ct);

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:Status} = @status, {col:DeadLetterError} = @error, {col:ProcessedAt} = @processedAt,
            {col:LockedBy} = NULL, {col:LockedUntil} = NULL
        WHERE {col:Id} = @id AND {col:Status} = @pending AND {col:LockedBy} = @hostId
        """)]
    public partial Task<int> DeadLetterAsync(
        Guid id, int status, string error, DateTimeOffset processedAt, int pending, string hostId,
        CancellationToken ct);

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:LockedBy} = NULL, {col:LockedUntil} = NULL
        WHERE {col:Id} = @id AND {col:LockedBy} = @hostId AND {col:Status} = @pending
        """)]
    public partial Task<int> ReleaseLeaseAsync(Guid id, string hostId, int pending, CancellationToken ct);

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:LockedUntil} = @until
        WHERE {col:Id} = @id AND {col:LockedBy} = @hostId AND {col:LockedUntil} >= @now AND {col:Status} = @status
        """)]
    public partial Task<int> RenewLeaseAsync(
        Guid id, string hostId, int status, DateTimeOffset now, DateTimeOffset until, CancellationToken ct);

    /// <inheritdoc />
    [Command("""
        UPDATE {table}
        SET {col:Status} = @status, {col:RetryCount} = 0, {col:NextRetryAt} = @now,
            {col:DeadLetterError} = NULL, {col:ProcessedAt} = NULL,
            {col:LockedBy} = NULL, {col:LockedUntil} = NULL
        WHERE {col:Id} = @id AND {col:Status} = @deadLetter
        """)]
    public partial Task<int> RequeueAsync(Guid id, int status, DateTimeOffset now, int deadLetter, CancellationToken ct);

    /// <inheritdoc />
    [Query("SELECT {col:Status}, {col:RetryCount} FROM {table} WHERE {col:Id} = @id")]
    public partial Task<OutboxRowState?> ReadStateAsync(Guid id, CancellationToken ct);

    /// <inheritdoc />
    [Query("""
        UPDATE {table}
        SET {col:LockedBy} = @hostId, {col:LockedUntil} = @until
        WHERE {col:Id} IN (
            SELECT {col:Id} FROM {table}
            WHERE {col:Status} = @status AND {col:NextRetryAt} <= @now
              AND ({col:LockedUntil} IS NULL OR {col:LockedUntil} < @now)
            ORDER BY {col:CreatedAt}
            LIMIT @batchSize)
          AND ({col:LockedUntil} IS NULL OR {col:LockedUntil} < @now)
        RETURNING {col:Id}, {col:TypeName}, {col:Payload}, {col:RetryCount}, {col:CreatedAt}
        """)]
    public partial Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
