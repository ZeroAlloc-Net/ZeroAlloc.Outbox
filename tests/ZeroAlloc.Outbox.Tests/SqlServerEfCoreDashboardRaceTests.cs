using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The dashboard races on SQL Server. The claim the tests set up with translates its
/// <c>Contains</c> to <c>OPENJSON</c>, which needs compatibility level 130 or later;
/// <see cref="SqlServerFixture"/> creates databases at 160.
/// </summary>
public sealed class SqlServerEfCoreDashboardRaceTests(SqlServerFixture server)
    : ServerEfCoreDashboardRaceTests, IClassFixture<SqlServerFixture>
{
    protected override Task<string> CreateDatabaseAsync() => server.CreateDatabaseAsync();

    protected override void UseServer(DbContextOptionsBuilder<ServerClaimDbContext> builder, string connectionString)
        => builder.UseSqlServer(connectionString);

    protected override void ClearPools() => SqlConnection.ClearAllPools();
}
