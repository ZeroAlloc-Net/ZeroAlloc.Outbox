namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The SQL the outbox store needs, built once from the table name.
/// </summary>
/// <remarks>
/// <para>
/// The table name is spliced in verbatim, so quoting is the caller's choice: <c>OutboxMessages</c>,
/// <c>"OutboxMessages"</c> and <c>public."Outbox Messages"</c> all work. It is trusted
/// configuration, never user input. The statements are plain ANSI except the batch claim, which
/// each dialect spells its own way. Column and parameter names are fixed. With <c>quoteColumns</c> every column is quoted in the
/// dialect's style, <c>"Id"</c> or <c>[Id]</c>, for a table whose columns were created quoted and
/// mixed case, as EF Core does on PostgreSQL; otherwise the text is unchanged.
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
    private readonly string _open;
    private readonly string _close;

    public OutboxSql(OutboxOrmDialect dialect, string tableName, bool quoteColumns = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        (_open, _close) = !quoteColumns ? (string.Empty, string.Empty)
            : dialect == OutboxOrmDialect.SqlServer ? ("[", "]") : ("\"", "\"");

        Insert = $"""
            INSERT INTO {tableName}
                ({Q("Id")}, {Q("TypeName")}, {Q("Payload")}, {Q("Status")}, {Q("RetryCount")}, {Q("NextRetryAt")}, {Q("CreatedAt")})
            VALUES
                (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
            """;

        MarkProcessed = $"""
            UPDATE {tableName}
            SET {Q("Status")} = @status, {Q("ProcessedAt")} = @processedAt,
                {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL
            WHERE {Q("Id")} = @id AND {Q("Status")} = @pending AND {Q("LockedBy")} = @hostId
            """;

        MarkForRetry = $"""
            UPDATE {tableName}
            SET {Q("RetryCount")} = @retryCount, {Q("NextRetryAt")} = @nextRetryAt,
                {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL
            WHERE {Q("Id")} = @id AND {Q("Status")} = @pending AND {Q("LockedBy")} = @hostId
            """;

        DeadLetter = $"""
            UPDATE {tableName}
            SET {Q("Status")} = @status, {Q("DeadLetterError")} = @error, {Q("ProcessedAt")} = @processedAt,
                {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL
            WHERE {Q("Id")} = @id AND {Q("Status")} = @pending AND {Q("LockedBy")} = @hostId
            """;

        ReleaseLease = $"""
            UPDATE {tableName}
            SET {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL
            WHERE {Q("Id")} = @id AND {Q("LockedBy")} = @hostId AND {Q("Status")} = @pending
            """;

        RenewLease = $"""
            UPDATE {tableName}
            SET {Q("LockedUntil")} = @until
            WHERE {Q("Id")} = @id AND {Q("LockedBy")} = @hostId AND {Q("LockedUntil")} >= @now AND {Q("Status")} = @status
            """;

        Requeue = $"""
            UPDATE {tableName}
            SET {Q("Status")} = @status, {Q("RetryCount")} = 0, {Q("NextRetryAt")} = @now,
                {Q("DeadLetterError")} = NULL, {Q("ProcessedAt")} = NULL,
                {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL
            WHERE {Q("Id")} = @id AND {Q("Status")} = @deadLetter
            """;

        ReadState = $"SELECT {Q("Status")}, {Q("RetryCount")} FROM {tableName} WHERE {Q("Id")} = @id";

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

    /// <summary>A column name, quoted in the dialect's style when the pipeline asked for it.</summary>
    private string Q(string column) => _open + column + _close;

    private string BuildClaim(OutboxOrmDialect dialect, string tableName)
        => dialect switch
        {
            OutboxOrmDialect.SqlServer => $"""
                WITH claim AS (
                    SELECT TOP (@batchSize) {Q("Id")}, {Q("TypeName")}, {Q("Payload")}, {Q("RetryCount")}, {Q("CreatedAt")}, {Q("LockedBy")}, {Q("LockedUntil")}
                    FROM {tableName} WITH (ROWLOCK, READPAST, UPDLOCK)
                    WHERE {Q("Status")} = @status AND {Q("NextRetryAt")} <= @now
                      AND ({Q("LockedUntil")} IS NULL OR {Q("LockedUntil")} < @now)
                    ORDER BY {Q("CreatedAt")})
                UPDATE claim SET {Q("LockedBy")} = @hostId, {Q("LockedUntil")} = @until
                OUTPUT inserted.{Q("Id")}, inserted.{Q("TypeName")}, inserted.{Q("Payload")}, inserted.{Q("RetryCount")}, inserted.{Q("CreatedAt")}
                """,
            OutboxOrmDialect.Postgres => $"""
                WITH c AS MATERIALIZED (
                    SELECT {Q("Id")} FROM {tableName}
                    WHERE {Q("Status")} = @status AND {Q("NextRetryAt")} <= @now
                      AND ({Q("LockedUntil")} IS NULL OR {Q("LockedUntil")} < @now)
                    ORDER BY {Q("CreatedAt")}
                    LIMIT @batchSize
                    FOR UPDATE SKIP LOCKED)
                UPDATE {tableName} t
                SET {Q("LockedBy")} = @hostId, {Q("LockedUntil")} = @until
                FROM c
                WHERE t.{Q("Id")} = c.{Q("Id")}
                RETURNING t.{Q("Id")}, t.{Q("TypeName")}, t.{Q("Payload")}, t.{Q("RetryCount")}, t.{Q("CreatedAt")}
                """,
            _ => $"""
                UPDATE {tableName}
                SET {Q("LockedBy")} = @hostId, {Q("LockedUntil")} = @until
                WHERE {Q("Id")} IN (
                    SELECT {Q("Id")} FROM {tableName}
                    WHERE {Q("Status")} = @status AND {Q("NextRetryAt")} <= @now
                      AND ({Q("LockedUntil")} IS NULL OR {Q("LockedUntil")} < @now)
                    ORDER BY {Q("CreatedAt")}
                    LIMIT @batchSize)
                  AND ({Q("LockedUntil")} IS NULL OR {Q("LockedUntil")} < @now)
                RETURNING {Q("Id")}, {Q("TypeName")}, {Q("Payload")}, {Q("RetryCount")}, {Q("CreatedAt")}
                """,
        };
}
