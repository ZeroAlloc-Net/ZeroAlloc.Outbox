using System;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.InMemory;

public static class InMemoryOutboxServiceCollectionExtensions
{
    /// <summary>Registers the in-memory outbox store (for testing only).</summary>
    public static IOutboxBuilder WithInMemoryStore(this IOutboxBuilder builder)
    {
        builder.Services.AddSingleton<InMemoryOutboxStore>();
        builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        builder.Services.AddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        return builder;
    }

    /// <summary>
    /// Legacy shim that preserves the v1.x extension shape. Delegates to
    /// <see cref="WithInMemoryStore"/>. Will be removed in the next major.
    /// </summary>
    [Obsolete("Use AddOutbox().WithInMemoryStore() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX002")]
    public static IServiceCollection AddOutboxInMemory(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryOutboxStore>();
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        services.AddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        return services;
    }

    /// <summary>
    /// Legacy shim for the chained form <c>services.AddOutbox().AddOutboxInMemory()</c>.
    /// Delegates to <see cref="WithInMemoryStore"/>. Will be removed in the next major.
    /// </summary>
    [Obsolete("Use AddOutbox().WithInMemoryStore() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX002")]
    public static IOutboxBuilder AddOutboxInMemory(this IOutboxBuilder builder)
        => builder.WithInMemoryStore();
}
