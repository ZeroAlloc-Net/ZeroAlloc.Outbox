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
    /// <summary>Schema for SQLite.</summary>
    public static IMigrationSource Sqlite { get; } = new Source("""
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
        """);

    /// <summary>Schema for PostgreSQL.</summary>
    public static IMigrationSource Postgres { get; } = new Source("""
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
        """);

    /// <summary>Schema for Microsoft SQL Server.</summary>
    /// <remarks>
    /// Id is UNIQUEIDENTIFIER rather than a BLOB so it indexes as a value and
    /// reads back as a Guid without a conversion. The table is keyed on it, and
    /// at 16 bytes it is comfortably inside the clustered index key limit, so
    /// unlike the saga table this one needs no NONCLUSTERED qualifier.
    /// </remarks>
    public static IMigrationSource SqlServer { get; } = new Source("""
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
        """);

    private sealed class Source(string sql) : IMigrationSource
    {
        private readonly IReadOnlyList<Migration> _migrations =
            [new Migration(1, "create_outbox_messages", sql)];

        public IReadOnlyList<Migration> GetMigrations() => _migrations;
    }
}
