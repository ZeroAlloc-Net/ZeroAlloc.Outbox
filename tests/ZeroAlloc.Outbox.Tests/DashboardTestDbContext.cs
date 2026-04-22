using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class DashboardTestDbContext : DbContext
{
    public DashboardTestDbContext(DbContextOptions<DashboardTestDbContext> options) : base(options) { }

    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutboxMessages();

        // SQLite can't translate DateTimeOffset comparisons natively; convert to a
        // sortable tick-based representation so FetchPendingAsync queries translate.
        var dtoConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDtoConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        var entity = modelBuilder.Entity<OutboxMessageEntity>();
        entity.Property(m => m.CreatedAt).HasConversion(dtoConverter);
        entity.Property(m => m.NextRetryAt).HasConversion(dtoConverter);
        entity.Property(m => m.ProcessedAt).HasConversion(nullableDtoConverter);

        base.OnModelCreating(modelBuilder);
    }
}
