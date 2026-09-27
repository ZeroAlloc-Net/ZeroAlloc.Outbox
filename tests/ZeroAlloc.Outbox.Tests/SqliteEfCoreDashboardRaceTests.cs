using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The dashboard races on SQLite, on a database file so that every context has a connection of
/// its own. SQLite serializes writers, and a busy writer waits for the lock.
/// </summary>
public sealed class SqliteEfCoreDashboardRaceTests : EfCoreDashboardRaceTests<DashboardTestDbContext>
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"outbox-race-{Guid.NewGuid():N}.db");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _path,
        Pooling = false,
    }.ToString();

    public override async Task InitializeAsync() =>
        await Context().Database.EnsureCreatedAsync().ConfigureAwait(false);

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        File.Delete(_path);
    }

    protected override DashboardTestDbContext CreateContext(IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<DashboardTestDbContext>().UseSqlite(ConnectionString);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return new DashboardTestDbContext(builder.Options);
    }
}
