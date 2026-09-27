using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The dashboard races on a database server, on a database of their own per test, with the
/// schema created from the mapping an application gets from <c>AddOutboxMessages</c>.
/// </summary>
public abstract class ServerEfCoreDashboardRaceTests : EfCoreDashboardRaceTests<ServerClaimDbContext>
{
    private string _connectionString = string.Empty;

    /// <summary>Creates an empty database on the class's container.</summary>
    protected abstract Task<string> CreateDatabaseAsync();

    /// <summary>Points the options at this server.</summary>
    protected abstract void UseServer(DbContextOptionsBuilder<ServerClaimDbContext> builder, string connectionString);

    /// <summary>Drops pooled connections, so tests do not pile up server sessions.</summary>
    protected abstract void ClearPools();

    public override async Task InitializeAsync()
    {
        _connectionString = await CreateDatabaseAsync().ConfigureAwait(false);
        await Context().Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        ClearPools();
    }

    protected override ServerClaimDbContext CreateContext(IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ServerClaimDbContext>();
        UseServer(builder, _connectionString);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new ServerClaimDbContext(builder.Options);
    }
}
