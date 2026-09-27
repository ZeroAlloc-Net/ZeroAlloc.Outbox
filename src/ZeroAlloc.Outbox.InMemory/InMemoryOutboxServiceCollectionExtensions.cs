using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Outbox.InMemory;

public static class InMemoryOutboxServiceCollectionExtensions
{
    /// <summary>Registers the in-memory outbox store (for testing only).</summary>
    /// <remarks>
    /// Calling this again is a no-op. An <see cref="IOutboxDashboardStore"/> the application
    /// registered first is kept.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered. One container hosts one outbox pipeline.
    /// </exception>
    public static IOutboxBuilder WithInMemoryStore(this IOutboxBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OutboxStoreRegistration.ThrowIfConflicting(
            builder.Services, typeof(InMemoryOutboxStore), "WithInMemoryStore()");

        builder.Services.TryAddSingleton<InMemoryOutboxStore>();
        builder.Services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        builder.Services.TryAddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        return builder;
    }
}
