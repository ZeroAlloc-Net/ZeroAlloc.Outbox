using System.Data.Common;
using Microsoft.Data.SqlClient;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The ORM claim on SQL Server: an updatable <c>TOP</c> CTE read with
/// <c>ROWLOCK, READPAST, UPDLOCK</c>, returning the leased rows through <c>OUTPUT</c>.
/// </summary>
public sealed class SqlServerOutboxClaimTests(SqlServerFixture server)
    : OrmServerClaimTests, IClassFixture<SqlServerFixture>
{
    protected override OutboxOrmDialect Dialect => OutboxOrmDialect.SqlServer;

    protected override IMigrationSource Migrations => OutboxOrmMigrations.SqlServer;

    protected override IMigrationDialect MigrationDialect { get; } = new SqlServerMigrationDialect();

    protected override Task<string> CreateDatabaseAsync() => server.CreateDatabaseAsync();

    protected override DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    protected override void ClearPools() => SqlConnection.ClearAllPools();
}
