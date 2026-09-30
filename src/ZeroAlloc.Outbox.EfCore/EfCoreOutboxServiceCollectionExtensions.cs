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
    /// <para>
    /// On a named pipeline's builder, from <c>AddOutbox(name, configure)</c>, this registers the
    /// store as an <see cref="EfCoreOutboxStore{TContext}"/> and an <see cref="IOutboxStore"/>
    /// keyed by the pipeline's name. No <see cref="IOutboxDashboardStore"/> is registered for it;
    /// the dashboard shows the default pipeline.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the same pipeline, such as
    /// another store adapter or the EF Core store for another <see cref="DbContext"/>. Or another
    /// pipeline already uses the EF Core store for <typeparamref name="TContext"/>, and would poll
    /// the same table.
    /// </exception>
    public static IOutboxBuilder WithEfCore<TContext>(this IOutboxBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var pipeline = OutboxStoreRegistration.PipelineOf(builder);
        var method = $"WithEfCore<{typeof(TContext).Name}>()";
        OutboxStoreRegistration.ThrowIfConflicting(services, pipeline, typeof(EfCoreOutboxStore<TContext>), method);
        OutboxStoreRegistration.ThrowIfSharedWithAnotherPipeline(
            services, pipeline, typeof(EfCoreOutboxStore<TContext>), method,
            "Give each pipeline a DbContext of its own, mapped to its own outbox table.");

        if (pipeline is null)
        {
            services.TryAddScoped<EfCoreOutboxStore<TContext>>();
            services.TryAddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
            services.TryAddScoped<IOutboxDashboardStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        }
        else
        {
            services.TryAddKeyedScoped(
                pipeline, static (sp, _) => new EfCoreOutboxStore<TContext>(sp.GetRequiredService<TContext>()));
            services.TryAddKeyedScoped<IOutboxStore>(
                pipeline, static (sp, key) => sp.GetRequiredKeyedService<EfCoreOutboxStore<TContext>>(key));
        }

        return builder;
    }
}
