using System.Data.Common;
using System.Data.Async.Adapters;
using AwesomeAssertions;
using Npgsql;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The ORM claim on PostgreSQL: a <c>MATERIALIZED</c> CTE locked with
/// <c>FOR UPDATE SKIP LOCKED</c>, then <c>UPDATE … FROM … RETURNING</c>.
/// </summary>
public sealed class PostgresOutboxClaimTests(PostgresServerFixture server)
    : OrmServerClaimTests, IClassFixture<PostgresServerFixture>
{
    protected override OutboxOrmDialect Dialect => OutboxOrmDialect.Postgres;

    protected override IMigrationSource Migrations => OutboxOrmMigrations.Postgres;

    protected override IMigrationDialect MigrationDialect { get; } = new PostgresMigrationDialect();

    protected override Task<string> CreateDatabaseAsync() => server.CreateDatabaseAsync();

    protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    protected override void ClearPools() => server.ClearPools();

    [Fact]
    public async Task An_Offset_Combined_History_Reassigned_As_Documented_Runs_As_Two_Sources()
    {
        // Before ZeroAlloc.ORM 2.2 the saga docs combined two schemas into one source, the
        // outbox's versions offset by 1000. docs/store-adapters.md gives the SQL that assigns
        // those rows to the two sources; this runs it, with the stand-in application source in
        // place of the saga's, against the exact pre-2.2 history layout.
        var connectionString = await server.CreateDatabaseAsync();
        var dialect = new PostgresMigrationDialect();
        var outbox = OutboxOrmMigrations.Postgres.GetMigrations();
        var app = new AppMigrationSource().GetMigrations();

        await ExecuteAsync(
            connectionString,
            app[0].Sql + ";" + app[1].Sql + ";" + outbox[0].Sql + outbox[1].Sql
            + """
              CREATE TABLE IF NOT EXISTS __zaorm_migrations (version INTEGER PRIMARY KEY,
                  name TEXT NOT NULL, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
              INSERT INTO __zaorm_migrations (version, name) VALUES
                  (1, 'create_orders'), (2, 'create_customers'),
                  (1001, 'create_outbox_messages'), (1002, 'add_outbox_lease');
              """);

        await ExecuteAsync(
            connectionString,
            """
            BEGIN;
            ALTER TABLE __zaorm_migrations RENAME TO __zaorm_migrations_old;
            ALTER INDEX __zaorm_migrations_pkey RENAME TO __zaorm_migrations_old_pkey;
            CREATE TABLE __zaorm_migrations (source TEXT NOT NULL, version INTEGER NOT NULL,
              name TEXT NOT NULL, applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
              PRIMARY KEY (source, version));
            INSERT INTO __zaorm_migrations (source, version, name, applied_at)
              SELECT CASE WHEN version >= 1000 THEN 'ZeroAlloc.Outbox.Orm' ELSE 'Tests.App' END,
                     CASE WHEN version >= 1000 THEN version - 1000 ELSE version END,
                     name, applied_at
              FROM __zaorm_migrations_old;
            DROP TABLE __zaorm_migrations_old;
            COMMIT;
            """);

        foreach (var source in new[] { new AppMigrationSource(), OutboxOrmMigrations.Postgres })
        {
            var connection = new NpgsqlConnection(connectionString).AsAsync();
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync();
                await new MigrationRunner(connection, source, dialect).RunAsync();
            }
        }

        (await ScalarAsync(connectionString, "SELECT COUNT(*) FROM __zaorm_migrations"))
            .Should().Be(4, "each source found its own versions applied and applied nothing again");
        (await ScalarAsync(
                connectionString,
                "SELECT COUNT(*) FROM __zaorm_migrations WHERE source = 'ZeroAlloc.Outbox.Orm' AND version IN (1, 2)"))
            .Should().Be(2);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        var raw = new NpgsqlConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        var raw = new NpgsqlConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                return Convert.ToInt64(
                    await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
