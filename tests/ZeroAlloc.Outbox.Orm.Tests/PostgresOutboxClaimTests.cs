using System.Data.Common;
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

    protected override void ClearPools() => NpgsqlConnection.ClearAllPools();
}
