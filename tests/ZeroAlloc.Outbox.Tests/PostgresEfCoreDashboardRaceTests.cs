using Microsoft.EntityFrameworkCore;
using Npgsql;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>The dashboard races on PostgreSQL, through Npgsql's EF Core provider.</summary>
public sealed class PostgresEfCoreDashboardRaceTests(PostgresServerFixture server)
    : ServerEfCoreDashboardRaceTests, IClassFixture<PostgresServerFixture>
{
    protected override Task<string> CreateDatabaseAsync() => server.CreateDatabaseAsync();

    protected override void UseServer(DbContextOptionsBuilder<ServerClaimDbContext> builder, string connectionString)
        => builder.UseNpgsql(connectionString);

    protected override void ClearPools() => NpgsqlConnection.ClearAllPools();
}
