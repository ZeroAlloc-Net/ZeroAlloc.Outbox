using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Outbox.EfCore;

/// <summary>
/// Extension methods for configuring the outbox messages table in a <see cref="DbContext"/>.
/// </summary>
public static class OutboxDbContextExtensions
{
    /// <summary>
    /// Adds the <see cref="OutboxMessageEntity"/> model to the <paramref name="modelBuilder"/>.
    /// Call this in your <c>DbContext.OnModelCreating</c>.
    /// </summary>
    public static void AddOutboxMessages(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TypeName).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.Status, e.NextRetryAt })
                  .HasFilter("[Status] = 0")
                  .HasDatabaseName("IX_OutboxMessages_Status_NextRetryAt");
        });
    }
}
