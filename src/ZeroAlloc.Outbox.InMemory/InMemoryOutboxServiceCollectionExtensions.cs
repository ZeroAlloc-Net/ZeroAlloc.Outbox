using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Outbox.InMemory;

public static class InMemoryOutboxServiceCollectionExtensions
{
    /// <summary>Registers the in-memory outbox store (for testing only).</summary>
    /// <remarks>
    /// <para>
    /// Calling this again is a no-op. An <see cref="IOutboxDashboardStore"/> the application
    /// registered first is kept.
    /// </para>
    /// <para>
    /// On a named pipeline's builder, from <c>AddOutbox(name, configure)</c>, this registers a
    /// store of the pipeline's own, as an <see cref="InMemoryOutboxStore"/>, an
    /// <see cref="IOutboxStore"/> and an <see cref="IOutboxDashboardStore"/> keyed by the
    /// pipeline's name.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the same pipeline.
    /// </exception>
    public static IOutboxBuilder WithInMemoryStore(this IOutboxBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var pipeline = OutboxStoreRegistration.PipelineOf(builder);
        OutboxStoreRegistration.ThrowIfConflicting(
            services, pipeline, typeof(InMemoryOutboxStore), "WithInMemoryStore()");

        if (pipeline is null)
        {
            services.TryAddSingleton<InMemoryOutboxStore>();
            services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
            services.TryAddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        }
        else
        {
            services.TryAddKeyedSingleton<InMemoryOutboxStore>(pipeline);
            services.TryAddKeyedSingleton<IOutboxStore>(
                pipeline, static (sp, key) => sp.GetRequiredKeyedService<InMemoryOutboxStore>(key));
            services.TryAddKeyedSingleton<IOutboxDashboardStore>(
                pipeline, static (sp, key) => sp.GetRequiredKeyedService<InMemoryOutboxStore>(key));
        }

        return builder;
    }
}
