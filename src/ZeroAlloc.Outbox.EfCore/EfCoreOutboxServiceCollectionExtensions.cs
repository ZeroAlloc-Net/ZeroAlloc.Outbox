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

    /// <summary>
    /// Legacy shim that preserves the v1.x extension shape. Will be removed in the next major.
    /// </summary>
    [Obsolete("Use AddOutbox().WithEfCore<TContext>() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX003")]
    public static IServiceCollection AddOutboxEfCore<TContext>(
        this IServiceCollection services)
        where TContext : DbContext
    {
        services.AddScoped<EfCoreOutboxStore<TContext>>();
        services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        services.AddScoped<IOutboxDashboardStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<TContext>>());
        return services;
    }

    /// <summary>
    /// Legacy shim for the chained form <c>services.AddOutbox().AddOutboxEfCore&lt;TContext&gt;()</c>.
    /// Will be removed in the next major.
    /// </summary>
    [Obsolete("Use AddOutbox().WithEfCore<TContext>() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX003")]
    public static IOutboxBuilder AddOutboxEfCore<TContext>(this IOutboxBuilder builder)
        where TContext : DbContext
        => builder.WithEfCore<TContext>();
}
