using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The EF Core claim on SQL Server. EF Core translates the claim's <c>Contains</c> over the
/// candidate ids to <c>OPENJSON</c>, which needs database compatibility level 130 or later.
/// </summary>
public sealed class SqlServerEfCoreServerClaimTests(SqlServerFixture server)
    : EfCoreServerClaimTests, IClassFixture<SqlServerFixture>
{
    protected override Task<string> CreateDatabaseAsync() => server.CreateDatabaseAsync();

    protected override void UseServer(DbContextOptionsBuilder<ServerClaimDbContext> builder, string connectionString)
        => builder.UseSqlServer(connectionString);

    protected override void ClearPools() => SqlConnection.ClearAllPools();
}
