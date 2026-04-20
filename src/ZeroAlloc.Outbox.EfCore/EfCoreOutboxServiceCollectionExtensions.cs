using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.EfCore;

public static class EfCoreOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EF Core outbox store using <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// Call <see cref="OutboxDbContextExtensions.AddOutboxMessages"/> in your DbContext's
    /// <c>OnModelCreating</c> to configure the <c>OutboxMessages</c> table.
    /// </remarks>
    public static IServiceCollection AddOutboxEfCore<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<TContext>>();
        return services;
    }
}
