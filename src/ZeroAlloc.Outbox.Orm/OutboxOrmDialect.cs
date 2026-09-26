namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Selects the batch-claim spelling for the target database.
/// </summary>
/// <remarks>
/// Only the batch claim differs between providers; everything else the store
/// issues is plain ANSI. SQLite is the default.
/// </remarks>
public enum OutboxOrmDialect
{
    /// <summary>SQLite. Uses <c>UPDATE … RETURNING</c> over a <c>LIMIT</c> subquery.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL. Uses <c>UPDATE … FROM … RETURNING</c> over a <c>MATERIALIZED</c> CTE locked with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
    Postgres = 1,

    /// <summary>Microsoft SQL Server. Uses an updatable <c>TOP</c> CTE with <c>READPAST</c> and <c>OUTPUT</c>.</summary>
    SqlServer = 2,
}
