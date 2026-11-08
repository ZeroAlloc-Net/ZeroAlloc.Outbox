namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Per-pipeline settings for the ORM outbox store.
/// </summary>
public sealed class OrmOutboxOptions
{
    /// <summary>
    /// The outbox table this pipeline's store reads and writes. Defaults to <c>OutboxMessages</c>.
    /// </summary>
    /// <remarks>
    /// The name is used verbatim in the store's SQL, so quote it yourself when the database needs
    /// it, such as <c>"Outbox"</c> on PostgreSQL. Names are compared with ordinal string equality,
    /// so a quoted and an unquoted name, or two names that differ in case, count as different
    /// tables. Create the table with <see cref="OutboxOrmMigrations"/> or your own migration.
    /// </remarks>
    public string TableName { get; set; } = "OutboxMessages";
}
