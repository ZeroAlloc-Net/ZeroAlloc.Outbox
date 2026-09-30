using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The outbox schema as one named source among others in ZeroAlloc.ORM's scoped history table,
/// on SQLite. PostgreSQL and SQL Server run the two-source case in
/// <see cref="OrmServerClaimTests"/>.
/// </summary>
/// <remarks>
/// SQLite's migration 2 is a plain <c>ALTER TABLE … ADD COLUMN</c> with no guard, so a run
/// that applied it a second time would fail with a duplicate column. Every test here that
/// runs the outbox source over an already migrated database therefore also proves that
/// nothing was applied again.
/// </remarks>
public sealed class OutboxMigrationSourceTests
{
    private const string OutboxSourceName = "ZeroAlloc.Outbox.Orm";

    // The name ZeroAlloc.ORM 2.2 records when a source does not override it, which is what
    // Outbox 4.1 and earlier recorded when run against that ORM.
    private const string DefaultSourceName = "ZeroAlloc.Outbox.Orm.OutboxOrmMigrations+Source";

    [Fact]
    public void Every_Dialect_Records_Its_Migrations_Under_One_Fixed_Name()
    {
        // Fixed, not the type's name, so renaming or moving the source class can never make
        // the runner apply the outbox migrations again.
        OutboxOrmMigrations.Sqlite.Name.Should().Be(OutboxSourceName);
        OutboxOrmMigrations.Postgres.Name.Should().Be(OutboxSourceName);
        OutboxOrmMigrations.SqlServer.Name.Should().Be(OutboxSourceName);
    }

    [Fact]
    public async Task The_Outbox_And_An_Application_Source_Both_Numbered_From_1_Share_One_Database()
    {
        await using var fx = new SqliteFixture();

        await RunAsync(fx, new AppMigrationSource());
        await RunAsync(fx, OutboxOrmMigrations.Sqlite);

        // A second run of each is a no-op: each finds its own versions applied.
        await RunAsync(fx, new AppMigrationSource());
        await RunAsync(fx, OutboxOrmMigrations.Sqlite);

        (await HistoryAsync(fx)).Should().BeEquivalentTo(
        [
            (AppMigrationSource.SourceName, 1L, "create_orders"),
            (AppMigrationSource.SourceName, 2L, "create_customers"),
            (OutboxSourceName, 1L, "create_outbox_messages"),
            (OutboxSourceName, 2L, "add_outbox_lease"),
        ]);
        (await fx.CountAsync()).Should().Be(0, "the outbox table exists");
    }

    [Fact]
    public async Task A_History_Table_From_Before_Source_Scoping_Is_Taken_Over_Without_Reapplying()
    {
        // A database the outbox alone migrated with ZeroAlloc.ORM 2.1: the tables, and a
        // history table keyed by version with no source column.
        await using var fx = new SqliteFixture();
        await ExecuteAsync(
            fx,
            OutboxSchemaSql(OutboxOrmMigrations.Sqlite)
            + """
              CREATE TABLE __zaorm_migrations (
                  version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
              INSERT INTO __zaorm_migrations (version, name, applied_at) VALUES
                  (1, 'create_outbox_messages', '2026-01-01 00:00:00'),
                  (2, 'add_outbox_lease', '2026-01-01 00:00:00');
              """);

        await RunAsync(fx, OutboxOrmMigrations.Sqlite);

        (await HistoryAsync(fx)).Should().BeEquivalentTo(
        [
            (OutboxSourceName, 1L, "create_outbox_messages"),
            (OutboxSourceName, 2L, "add_outbox_lease"),
        ]);
    }

    [Fact]
    public async Task Rows_Recorded_Under_The_Default_Name_Carry_Over_With_The_Documented_Update()
    {
        // Outbox 4.1 and earlier run against ZeroAlloc.ORM 2.2 record the source under the
        // type's name. docs/store-adapters.md gives the one statement that moves those rows to
        // the fixed name; this runs it exactly as written there.
        await using var fx = new SqliteFixture();
        await RunAsync(fx, new RenamedSource(OutboxOrmMigrations.Sqlite, DefaultSourceName));

        await ExecuteAsync(
            fx,
            """
            UPDATE __zaorm_migrations
            SET source = 'ZeroAlloc.Outbox.Orm'
            WHERE source = 'ZeroAlloc.Outbox.Orm.OutboxOrmMigrations+Source';
            """);
        await RunAsync(fx, OutboxOrmMigrations.Sqlite);

        (await HistoryAsync(fx)).Should().BeEquivalentTo(
        [
            (OutboxSourceName, 1L, "create_outbox_messages"),
            (OutboxSourceName, 2L, "add_outbox_lease"),
        ]);
    }

    [Fact]
    public async Task Without_The_Update_The_Default_Name_Makes_The_Runner_Apply_The_Outbox_Again()
    {
        // Why the docs ask for the update: the fixed name finds none of its versions applied.
        await using var fx = new SqliteFixture();
        await RunAsync(fx, new RenamedSource(OutboxOrmMigrations.Sqlite, DefaultSourceName));

        var act = () => RunAsync(fx, OutboxOrmMigrations.Sqlite);

        (await act.Should().ThrowAsync<SqliteException>())
            .Which.Message.Should().Contain("duplicate column name");
    }

    [Fact]
    public async Task A_Database_Switched_From_The_EfCore_Store_Takes_The_Documented_History_Row()
    {
        // The EF Core model already created the lease columns, so the outbox's migration 2
        // must be recorded as applied under the outbox's name before the first run. The SQL
        // is the one in docs/store-adapters.md.
        await using var fx = new SqliteFixture();
        await ExecuteAsync(fx, OutboxSchemaSql(OutboxOrmMigrations.Sqlite));

        await ExecuteAsync(
            fx,
            """
            CREATE TABLE IF NOT EXISTS __zaorm_migrations (
                source TEXT NOT NULL, version INTEGER NOT NULL, name TEXT NOT NULL, applied_at TEXT NOT NULL,
                PRIMARY KEY (source, version));
            INSERT INTO __zaorm_migrations (source, version, name, applied_at)
            VALUES ('ZeroAlloc.Outbox.Orm', 2, 'add_outbox_lease', datetime('now'));
            """);
        await RunAsync(fx, OutboxOrmMigrations.Sqlite);

        (await HistoryAsync(fx)).Should().BeEquivalentTo(
        [
            (OutboxSourceName, 1L, "create_outbox_messages"),
            (OutboxSourceName, 2L, "add_outbox_lease"),
        ]);
    }

    private static async Task RunAsync(SqliteFixture fx, IMigrationSource source)
    {
        var connection = await fx.ConnectAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await new MigrationRunner(connection, source, new SqliteMigrationDialect())
                .RunAsync().ConfigureAwait(false);
        }
    }

    private static string OutboxSchemaSql(IMigrationSource source)
    {
        var migrations = source.GetMigrations();
        return string.Concat(migrations.Select(m => m.Sql + "\n"));
    }

    private static async Task ExecuteAsync(SqliteFixture fx, string sql)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<List<(string Source, long Version, string Name)>> HistoryAsync(SqliteFixture fx)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT source, version, name FROM __zaorm_migrations";
                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    var rows = new List<(string, long, string)>();
                    while (await reader.ReadAsync().ConfigureAwait(false))
                        rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
                    return rows;
                }
            }
        }
    }
}
