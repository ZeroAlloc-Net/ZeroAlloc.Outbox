using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Outbox.EfCore;

public static class EfCoreOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EF Core outbox store using <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// Call <see cref="OutboxDbContextExtensions.AddOutboxMessages"/> in your DbContext's
    /// <c>OnModelCreating</c> to configure the <c>OutboxMessages</c> table.
    /// <para>
    /// Calling this again with the same <typeparamref name="TContext"/> is a no-op. An
    /// <see cref="IOutboxDashboardStore"/> the application registered first is kept.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered, such as another store adapter
    /// or the EF Core store for another <see cref="DbContext"/>. One container hosts one outbox pipeline.
    /// </exception>
    public static IOutboxBuilder WithEfCore<TContext>(this IOutboxBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        OutboxStoreRegistration.ThrowIfConflicting(
            builder.Services, typeof(EfCoreOutboxStore<TContext>), $"WithEfCore<{typeof(TContext).Name}>()");

        builder.Services.TryAddScoped<EfCoreOutboxStore<TContext>>();
        builder.Services.TryAddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        builder.Services.TryAddScoped<IOutboxDashboardStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        return builder;
    }
}
