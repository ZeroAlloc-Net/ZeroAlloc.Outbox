using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The outbox schema, as an <see cref="IMigrationSource"/> you hand to
/// ZeroAlloc.ORM's <c>MigrationRunner</c> along with the dialect for your
/// database.
/// </summary>
/// <remarks>
/// <para>
/// Supplied as code rather than embedded <c>.sql</c> resources because the DDL
/// is the one genuinely provider-specific part — <c>BLOB</c> versus
/// <c>BYTEA</c>, <c>TEXT</c> versus <c>VARCHAR</c>. The resource-naming
/// convention gives a single migration set per assembly, which cannot express
/// that; selecting the statement by dialect can.
/// </para>
/// <para>
/// Every dialect's source for the default <c>OutboxMessages</c> table has the same fixed
/// <see cref="IMigrationSource.Name"/>, <c>ZeroAlloc.Outbox.Orm</c>, so the outbox numbers its
/// migrations on its own in ZeroAlloc.ORM's history table, next to your application's
/// migrations and any other library's, each starting at version 1. Run each source through its own
/// <c>MigrationRunner</c> on the same connection. A pipeline with its own table gets its
/// schema from <see cref="For"/>, which names the source after the table.
/// </para>
/// <para>
/// The index on <c>(Status, NextRetryAt)</c> matches the poller's query exactly.
/// Without it every poll scans the whole table, which grows without bound in a
/// system that keeps dispatched rows.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var runner = new MigrationRunner(connection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect());
/// await runner.RunAsync(ct);
/// </code>
/// </example>
public static class OutboxOrmMigrations
{
    // The name the history table records the outbox's migrations under. A fixed string rather
    // than the source type's name, ZeroAlloc.ORM's default, so renaming or moving the type can
    // never make the runner apply the outbox migrations again. It must never change: a changed
    // name reads as a new source, and databases already migrated would apply everything again.
    private const int MaxTableNameLength = 40;

    private const string SourceName = "ZeroAlloc.Outbox.Orm";

    /// <summary>Schema for SQLite.</summary>
    public static IMigrationSource Sqlite { get; } = new Source(
        """
        CREATE TABLE IF NOT EXISTS OutboxMessages (
            Id              BLOB NOT NULL PRIMARY KEY,
            TypeName        TEXT NOT NULL,
            Payload         BLOB NOT NULL,
            Status          INTEGER NOT NULL,
            RetryCount      INTEGER NOT NULL,
            NextRetryAt     TEXT NOT NULL,
            CreatedAt       TEXT NOT NULL,
            ProcessedAt     TEXT NULL,
            DeadLetterError TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_OutboxMessages_Status_NextRetryAt
            ON OutboxMessages (Status, NextRetryAt);
        """,
        """
        ALTER TABLE OutboxMessages ADD COLUMN LockedBy TEXT NULL;
        ALTER TABLE OutboxMessages ADD COLUMN LockedUntil TEXT NULL;
        """);

    /// <summary>Schema for PostgreSQL.</summary>
    public static IMigrationSource Postgres { get; } = new Source(
        """
        CREATE TABLE IF NOT EXISTS OutboxMessages (
            Id              UUID NOT NULL PRIMARY KEY,
            TypeName        VARCHAR(256) NOT NULL,
            Payload         BYTEA NOT NULL,
            Status          INTEGER NOT NULL,
            RetryCount      INTEGER NOT NULL,
            NextRetryAt     TIMESTAMPTZ NOT NULL,
            CreatedAt       TIMESTAMPTZ NOT NULL,
            ProcessedAt     TIMESTAMPTZ NULL,
            DeadLetterError TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_OutboxMessages_Status_NextRetryAt
            ON OutboxMessages (Status, NextRetryAt);
        """,
        """
        ALTER TABLE OutboxMessages ADD COLUMN IF NOT EXISTS LockedBy VARCHAR(128) NULL;
        ALTER TABLE OutboxMessages ADD COLUMN IF NOT EXISTS LockedUntil TIMESTAMPTZ NULL;
        """);

    /// <summary>Schema for Microsoft SQL Server.</summary>
    /// <remarks>
    /// Id is UNIQUEIDENTIFIER rather than a BLOB so it indexes as a value and
    /// reads back as a Guid without a conversion. The table is keyed on it, and
    /// at 16 bytes it is comfortably inside the clustered index key limit, so
    /// unlike the saga table this one needs no NONCLUSTERED qualifier.
    /// </remarks>
    public static IMigrationSource SqlServer { get; } = new Source(
        """
        IF OBJECT_ID(N'OutboxMessages', N'U') IS NULL
        CREATE TABLE OutboxMessages (
            Id              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            TypeName        NVARCHAR(256) NOT NULL,
            Payload         VARBINARY(MAX) NOT NULL,
            Status          INT NOT NULL,
            RetryCount      INT NOT NULL,
            NextRetryAt     DATETIMEOFFSET NOT NULL,
            CreatedAt       DATETIMEOFFSET NOT NULL,
            ProcessedAt     DATETIMEOFFSET NULL,
            DeadLetterError NVARCHAR(MAX) NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OutboxMessages_Status_NextRetryAt')
        CREATE INDEX IX_OutboxMessages_Status_NextRetryAt
            ON OutboxMessages (Status, NextRetryAt);
        """,
        """
        IF COL_LENGTH(N'OutboxMessages', N'LockedBy') IS NULL
            ALTER TABLE OutboxMessages ADD LockedBy NVARCHAR(128) NULL;
        IF COL_LENGTH(N'OutboxMessages', N'LockedUntil') IS NULL
            ALTER TABLE OutboxMessages ADD LockedUntil DATETIMEOFFSET NULL;
        """);

    /// <summary>
    /// The schema for <paramref name="dialect"/> on a table other than the default
    /// <c>OutboxMessages</c>, for a pipeline whose store was given its own table name.
    /// </summary>
    /// <param name="dialect">The database the schema is for.</param>
    /// <param name="tableName">
    /// The table to create, a plain identifier: letters, digits and underscores, not starting
    /// with a digit, at most 40 characters (the index name adds 22, and PostgreSQL truncates
    /// identifiers at 63 bytes). It is used unquoted in the DDL, and the index is named
    /// <c>IX_{tableName}_Status_NextRetryAt</c>.
    /// </param>
    /// <returns>
    /// A source named <c>ZeroAlloc.Outbox.Orm:{tableName}</c> with the name in lower case, so
    /// each table has its own history next to the others, and names differing only in case share
    /// one, as the databases treat them as one table. For <c>OutboxMessages</c>, in any case, it is
    /// the same source as <see cref="Sqlite"/>, <see cref="Postgres"/> or
    /// <see cref="SqlServer"/>, under the unchanged name <c>ZeroAlloc.Outbox.Orm</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="tableName"/> is not a plain identifier or is longer than 40 characters,
    /// or <paramref name="dialect"/> is unknown.
    /// </exception>
    public static IMigrationSource For(OutboxOrmDialect dialect, string tableName)
    {
        if (!IsPlainIdentifier(tableName))
        {
            throw new ArgumentException(
                "ZeroAlloc.Outbox: the table name must be a plain identifier: letters, digits and "
                + "underscores, not starting with a digit, at most 40 characters.",
                nameof(tableName));
        }

        if (!Enum.IsDefined(dialect))
            throw new ArgumentException("ZeroAlloc.Outbox: unknown dialect.", nameof(dialect));

        if (string.Equals(tableName, OrmOutboxOptions.DefaultTableName, StringComparison.OrdinalIgnoreCase))
        {
            return dialect switch
            {
                OutboxOrmDialect.Sqlite => Sqlite,
                OutboxOrmDialect.Postgres => Postgres,
                _ => SqlServer,
            };
        }

        var name = SourceName + ":" + tableName.ToLowerInvariant();
        var index = "IX_" + tableName + "_Status_NextRetryAt";
        return dialect switch
        {
            OutboxOrmDialect.Sqlite => SqliteSource(name, tableName, index),
            OutboxOrmDialect.Postgres => PostgresSource(name, tableName, index),
            _ => SqlServerSource(name, tableName, index),
        };
    }

    private static Source SqliteSource(string name, string tableName, string index) => new(
            name,
            $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                Id              BLOB NOT NULL PRIMARY KEY,
                TypeName        TEXT NOT NULL,
                Payload         BLOB NOT NULL,
                Status          INTEGER NOT NULL,
                RetryCount      INTEGER NOT NULL,
                NextRetryAt     TEXT NOT NULL,
                CreatedAt       TEXT NOT NULL,
                ProcessedAt     TEXT NULL,
                DeadLetterError TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS {index}
                ON {tableName} (Status, NextRetryAt);
            """,
            $"""
            ALTER TABLE {tableName} ADD COLUMN LockedBy TEXT NULL;
            ALTER TABLE {tableName} ADD COLUMN LockedUntil TEXT NULL;
            """);

    private static Source PostgresSource(string name, string tableName, string index) => new(
            name,
            $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                Id              UUID NOT NULL PRIMARY KEY,
                TypeName        VARCHAR(256) NOT NULL,
                Payload         BYTEA NOT NULL,
                Status          INTEGER NOT NULL,
                RetryCount      INTEGER NOT NULL,
                NextRetryAt     TIMESTAMPTZ NOT NULL,
                CreatedAt       TIMESTAMPTZ NOT NULL,
                ProcessedAt     TIMESTAMPTZ NULL,
                DeadLetterError TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS {index}
                ON {tableName} (Status, NextRetryAt);
            """,
            $"""
            ALTER TABLE {tableName} ADD COLUMN IF NOT EXISTS LockedBy VARCHAR(128) NULL;
            ALTER TABLE {tableName} ADD COLUMN IF NOT EXISTS LockedUntil TIMESTAMPTZ NULL;
            """);

    private static Source SqlServerSource(string name, string tableName, string index) => new(
            name,
            $"""
            IF OBJECT_ID(N'{tableName}', N'U') IS NULL
            CREATE TABLE {tableName} (
                Id              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                TypeName        NVARCHAR(256) NOT NULL,
                Payload         VARBINARY(MAX) NOT NULL,
                Status          INT NOT NULL,
                RetryCount      INT NOT NULL,
                NextRetryAt     DATETIMEOFFSET NOT NULL,
                CreatedAt       DATETIMEOFFSET NOT NULL,
                ProcessedAt     DATETIMEOFFSET NULL,
                DeadLetterError NVARCHAR(MAX) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'{tableName}'))
            CREATE INDEX {index}
                ON {tableName} (Status, NextRetryAt);
            """,
            $"""
            IF COL_LENGTH(N'{tableName}', N'LockedBy') IS NULL
                ALTER TABLE {tableName} ADD LockedBy NVARCHAR(128) NULL;
            IF COL_LENGTH(N'{tableName}', N'LockedUntil') IS NULL
                ALTER TABLE {tableName} ADD LockedUntil DATETIMEOFFSET NULL;
            """);

    private static bool IsPlainIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxTableNameLength || char.IsAsciiDigit(name[0]))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    private sealed class Source(string name, string createSql, string leaseSql) : IMigrationSource
    {
        public Source(string createSql, string leaseSql)
            : this(SourceName, createSql, leaseSql)
        {
        }

        private readonly IReadOnlyList<Migration> _migrations =
        [
            new Migration(1, "create_outbox_messages", createSql),
            new Migration(2, "add_outbox_lease", leaseSql),
        ];

        public string Name => name;

        public IReadOnlyList<Migration> GetMigrations() => _migrations;
    }
}
