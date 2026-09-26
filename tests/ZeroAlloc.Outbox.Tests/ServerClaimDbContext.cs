using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// A context with exactly the mapping <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>
/// gives an application, for PostgreSQL and SQL Server. Unlike <see cref="DashboardTestDbContext"/>
/// it has no SQLite workarounds, so the provider's own <c>DateTimeOffset</c> mapping is tested.
/// </summary>
public sealed class ServerClaimDbContext(DbContextOptions<ServerClaimDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        base.OnModelCreating(modelBuilder);
    }
}
