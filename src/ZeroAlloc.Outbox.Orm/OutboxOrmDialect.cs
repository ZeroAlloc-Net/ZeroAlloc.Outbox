namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Selects the paged-query spelling for the target database.
/// </summary>
/// <remarks>
/// Only the batch fetch differs between providers; everything else the store
/// issues is plain ANSI. SQLite and PostgreSQL both accept <c>LIMIT</c>, so they
/// share an implementation and remain the default.
/// </remarks>
public enum OutboxOrmDialect
{
    /// <summary>SQLite. Uses <c>LIMIT</c>.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL. Uses <c>LIMIT</c>, which it accepts alongside the standard spelling.</summary>
    Postgres = 1,

    /// <summary>Microsoft SQL Server. Uses <c>OFFSET … FETCH NEXT</c>.</summary>
    SqlServer = 2,
}
