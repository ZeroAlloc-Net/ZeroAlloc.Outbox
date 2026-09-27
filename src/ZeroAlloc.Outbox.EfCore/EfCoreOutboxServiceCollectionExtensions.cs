using System;
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
    public static IOutboxBuilder WithEfCore<TContext>(this IOutboxBuilder builder)
        where TContext : DbContext
    {
        builder.Services.AddScoped<EfCoreOutboxStore<TContext>>();
        builder.Services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        builder.Services.AddScoped<IOutboxDashboardStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        return builder;
    }
}
