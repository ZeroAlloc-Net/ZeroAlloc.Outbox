namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The SQL the outbox store needs, built once from the table name.
/// </summary>
/// <remarks>
/// <para>
/// The table name is spliced in verbatim, so quoting is the caller's choice: <c>OutboxMessages</c>,
/// <c>"OutboxMessages"</c> and <c>public."Outbox Messages"</c> all work. It is trusted
/// configuration, never user input. The statements are plain ANSI except the batch claim, which
/// each dialect spells its own way. Column and parameter names are fixed.
/// </para>
/// <para>
/// Every mark is guarded by <c>Status = @pending AND LockedBy = @hostId</c> and the store reads the
/// number of rows it changed, so the caller learns whether it won the transition. The status
/// condition is the outbox state machine for these triggers: Dispatch, Fail and Exhaust are legal
/// exactly from the pending states. The lease condition means only the current lease holder can move
/// a row. <c>LockedUntil</c> is deliberately not checked, so a host whose lease expired without being
/// taken over can still record its outcome.
/// </para>
/// <para>
/// Status is written as its integer value rather than a name. That keeps the column orderable and
/// indexable on every provider, and means adding a state does not invalidate existing rows.
/// </para>
/// <para>
/// The claim is a single <c>UPDATE</c> that leases the rows it takes and returns them, and every
/// provider spells that differently. SQLite uses <c>UPDATE … RETURNING</c> over a <c>LIMIT</c>
/// subquery; it serializes writers, so it needs no row locking. PostgreSQL locks the batch in a
/// <c>MATERIALIZED</c> CTE with <c>FOR UPDATE SKIP LOCKED</c>, so concurrent claimers pass over each
/// other's rows instead of queueing behind them, and updates from it with <c>RETURNING</c>; the
/// CTE runs exactly once, so the rows it locks are the rows the update touches. SQL Server has
/// neither <c>LIMIT</c> nor <c>RETURNING</c>, so it uses an updatable <c>TOP</c> CTE with
/// <c>ROWLOCK, READPAST, UPDLOCK</c> hints and an <c>OUTPUT</c> clause. The order of the returned
/// rows is unspecified on every dialect, so the store sorts them by <c>CreatedAt</c> itself.
/// </para>
/// </remarks>
internal sealed class OutboxSql
{
    public OutboxSql(OutboxOrmDialect dialect, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        Insert = $"""
            INSERT INTO {tableName}
                (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt)
            VALUES
                (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
            """;

        MarkProcessed = $"""
            UPDATE {tableName}
            SET Status = @status, ProcessedAt = @processedAt,
                LockedBy = NULL, LockedUntil = NULL
            WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
            """;

        MarkForRetry = $"""
            UPDATE {tableName}
            SET RetryCount = @retryCount, NextRetryAt = @nextRetryAt,
                LockedBy = NULL, LockedUntil = NULL
            WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
            """;

        DeadLetter = $"""
            UPDATE {tableName}
            SET Status = @status, DeadLetterError = @error, ProcessedAt = @processedAt,
                LockedBy = NULL, LockedUntil = NULL
            WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
            """;

        ReleaseLease = $"""
            UPDATE {tableName}
            SET LockedBy = NULL, LockedUntil = NULL
            WHERE Id = @id AND LockedBy = @hostId AND Status = @pending
            """;

        RenewLease = $"""
            UPDATE {tableName}
            SET LockedUntil = @until
            WHERE Id = @id AND LockedBy = @hostId AND LockedUntil >= @now AND Status = @status
            """;

        Requeue = $"""
            UPDATE {tableName}
            SET Status = @status, RetryCount = 0, NextRetryAt = @now,
                DeadLetterError = NULL, ProcessedAt = NULL,
                LockedBy = NULL, LockedUntil = NULL
            WHERE Id = @id AND Status = @deadLetter
            """;

        ReadState = $"SELECT Status, RetryCount FROM {tableName} WHERE Id = @id";

        Claim = BuildClaim(dialect, tableName);
    }

    public string Insert { get; }

    public string MarkProcessed { get; }

    public string MarkForRetry { get; }

    public string DeadLetter { get; }

    public string ReleaseLease { get; }

    public string RenewLease { get; }

    public string Claim { get; }

    public string Requeue { get; }

    public string ReadState { get; }

    private static string BuildClaim(OutboxOrmDialect dialect, string tableName)
        => dialect switch
        {
            OutboxOrmDialect.SqlServer => $"""
                WITH claim AS (
                    SELECT TOP (@batchSize) Id, TypeName, Payload, RetryCount, CreatedAt, LockedBy, LockedUntil
                    FROM {tableName} WITH (ROWLOCK, READPAST, UPDLOCK)
                    WHERE Status = @status AND NextRetryAt <= @now
                      AND (LockedUntil IS NULL OR LockedUntil < @now)
                    ORDER BY CreatedAt)
                UPDATE claim SET LockedBy = @hostId, LockedUntil = @until
                OUTPUT inserted.Id, inserted.TypeName, inserted.Payload, inserted.RetryCount, inserted.CreatedAt
                """,
            OutboxOrmDialect.Postgres => $"""
                WITH c AS MATERIALIZED (
                    SELECT Id FROM {tableName}
                    WHERE Status = @status AND NextRetryAt <= @now
                      AND (LockedUntil IS NULL OR LockedUntil < @now)
                    ORDER BY CreatedAt
                    LIMIT @batchSize
                    FOR UPDATE SKIP LOCKED)
                UPDATE {tableName} t
                SET LockedBy = @hostId, LockedUntil = @until
                FROM c
                WHERE t.Id = c.Id
                RETURNING t.Id, t.TypeName, t.Payload, t.RetryCount, t.CreatedAt
                """,
            _ => $"""
                UPDATE {tableName}
                SET LockedBy = @hostId, LockedUntil = @until
                WHERE Id IN (
                    SELECT Id FROM {tableName}
                    WHERE Status = @status AND NextRetryAt <= @now
                      AND (LockedUntil IS NULL OR LockedUntil < @now)
                    ORDER BY CreatedAt
                    LIMIT @batchSize)
                  AND (LockedUntil IS NULL OR LockedUntil < @now)
                RETURNING Id, TypeName, Payload, RetryCount, CreatedAt
                """,
        };
}
