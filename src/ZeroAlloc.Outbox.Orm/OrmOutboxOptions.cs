namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Per-pipeline settings for the ORM outbox store.
/// </summary>
public sealed class OrmOutboxOptions
{
    internal const string DefaultTableName = "OutboxMessages";

    /// <summary>
    /// The outbox table this pipeline's store reads and writes. Defaults to <c>OutboxMessages</c>.
    /// </summary>
    /// <remarks>
    /// The name is used verbatim in the store's SQL, so quote it yourself when the database needs
    /// it, such as <c>"Outbox"</c> on PostgreSQL. Names are compared with ordinal string equality,
    /// so a quoted and an unquoted name, or two names that differ in case, count as different
    /// tables. Create the table with <see cref="OutboxOrmMigrations"/> or your own migration.
    /// </remarks>
    public string TableName { get; set; } = DefaultTableName;

    /// <summary>
    /// Quotes every column name in the store's SQL, in the dialect's style: <c>"Id"</c> on
    /// PostgreSQL and SQLite, <c>[Id]</c> on SQL Server. Defaults to <c>false</c>, which leaves
    /// the SQL as it was.
    /// </summary>
    /// <remarks>
    /// Set it for a table whose columns were created quoted and mixed case, such as the
    /// <c>"OutboxMessages"</c> table EF Core's PostgreSQL provider creates, where an unquoted
    /// <c>Id</c> folds to <c>id</c> and finds no column. A table made by
    /// <see cref="OutboxOrmMigrations"/> has unquoted columns and needs this off. It is part of a
    /// pipeline's registration identity: registering the same pipeline again with another value
    /// throws.
    /// </remarks>
    public bool QuoteColumns { get; set; }
}
