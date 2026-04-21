using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class DashboardTestDbContext : DbContext
{
    public DashboardTestDbContext(DbContextOptions<DashboardTestDbContext> options) : base(options) { }

    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();
        base.OnModelCreating(modelBuilder);
    }
}
