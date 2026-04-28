using Microsoft.EntityFrameworkCore;
using ZeroAlloc.ValueObjects.EfCore;

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
            // Map OutboxMessageId <-> Guid via the ZeroAlloc.ValueObjects.EfCore converter.
            // The convention-based registration (Properties<TId>().HaveConversion) lives on
            // ModelConfigurationBuilder (i.e. DbContext.ConfigureConventions), which is not
            // available here; per-property HasConversion keeps the registration local to
            // AddOutboxMessages so consumers don't have to override ConfigureConventions.
            entity.Property(e => e.Id)
                  .HasConversion<TypedIdValueConverter<OutboxMessageId, Guid>>();
            entity.Property(e => e.TypeName).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => new { e.Status, e.NextRetryAt })
                  .HasDatabaseName("IX_OutboxMessages_Status_NextRetryAt");
        });
    }
}
