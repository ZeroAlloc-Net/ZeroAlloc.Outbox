using System.Data.Async;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Builds the generated repository for a dialect. Each repository's SQL is built once per table and
/// <c>quoteColumns</c> value and cached, so creating one allocates only the repository itself.
/// </summary>
internal static class OutboxStatements
{
    /// <summary>
    /// The statements for <paramref name="dialect"/> over <paramref name="connection"/> and
    /// <paramref name="table"/>.
    /// </summary>
    /// <param name="dialect">The database the statements are spelled for.</param>
    /// <param name="connection">The connection every statement runs on.</param>
    /// <param name="table">The outbox table, spliced into the SQL verbatim.</param>
    /// <param name="quoteColumns">Quotes every column name in the dialect's style.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dialect"/> is not a defined value.</exception>
    internal static IOutboxStatements Create(
        OutboxOrmDialect dialect, IAsyncDbConnection connection, string table, bool quoteColumns)
        => dialect switch
        {
            OutboxOrmDialect.Sqlite => new SqliteOutboxRepository(connection, table, quoteColumns),
            OutboxOrmDialect.Postgres => new PostgresOutboxRepository(connection, table, quoteColumns),
            OutboxOrmDialect.SqlServer => new SqlServerOutboxRepository(connection, table, quoteColumns),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unknown outbox dialect."),
        };
}
